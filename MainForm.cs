using System.Diagnostics;
using System.Runtime.InteropServices;

internal sealed class MainForm : Form
{
    private const int SampleCapacity = 120;          // 60 s of history at 500 ms
    private const string ConnectHeadline = "Cannot connect to CNC";

    private readonly UploaderView view = new();
    private readonly UploadEngine engine = new(() => AppSettings.Load(AppSettings.DefaultPath).ToFtpSettings());
    private readonly System.Windows.Forms.Timer refreshTimer = new() { Interval = 500 };
    private readonly System.Windows.Forms.Timer closeTimer = new() { Interval = 1500 };
    private readonly TaskCompletionSource ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly List<double> samples = new();
    private readonly Stopwatch clock = Stopwatch.StartNew();
    private readonly string[] startupPaths;

    private BatchResult? lastResult;
    private string? message;               // e.g. "No files were found."
    private bool userClosing;
    private bool wasRunning;
    private long lastWireBytes;
    private double lastTick;
    private double smoothedSpeed;
    private DateTime settingsStamp = DateTime.MinValue;
    private string targetText = string.Empty;   // "Sending to: ..." or the settings error

    public MainForm(string[] startupPaths)
    {
        this.startupPaths = startupPaths;

        Text = "MITSUBISHI UPLOADER";
        Icon = Environment.ProcessPath is string processPath
            ? System.Drawing.Icon.ExtractAssociatedIcon(processPath) ?? SystemIcons.Application
            : SystemIcons.Application;
        AutoScaleDimensions = new SizeF(96F, 96F);
        AutoScaleMode = AutoScaleMode.Dpi;
        ClientSize = new Size(600, 376);
        StartPosition = FormStartPosition.CenterScreen;
        BackColor = Color.FromArgb(0xF4, 0xF7, 0xFB);
        AllowDrop = true;

        view.Dock = DockStyle.Fill;
        view.AllowDrop = true;
        view.SampleCapacity = SampleCapacity;
        view.DragEnter += OnDragEnter;
        view.DragOver += OnDragEnter;
        view.DragLeave += OnDragLeave;
        view.DragDrop += OnDragDrop;
        view.PauseClicked += (_, _) => TogglePause();
        view.CancelClicked += (_, _) => engine.Cancel();
        view.SettingsClicked += (_, _) => OpenSettings();
        Controls.Add(view);

        engine.Finished += result =>
        {
            if (InvokeRequired) BeginInvoke(() => OnFinished(result));
            else OnFinished(result);
        };
        refreshTimer.Tick += (_, _) => RefreshView();
        closeTimer.Tick += (_, _) => { closeTimer.Stop(); Close(); };
        FormClosing += OnFormClosing;
        Shown += OnShown;

        RefreshView();
    }

    protected override void OnLoad(EventArgs e)
    {
        base.OnLoad(e);
        MinimumSize = Size; // after DPI scaling
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        // Windows 11: white title bar to match the design. Ignored on older Windows.
        try
        {
            var white = 0x00FFFFFF;
            DwmSetWindowAttribute(Handle, 35 /* DWMWA_CAPTION_COLOR */, ref white, sizeof(int));
        }
        catch { }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            refreshTimer.Dispose();
            closeTimer.Dispose();
        }
        base.Dispose(disposing);
    }

    private void OnShown(object? sender, EventArgs e)
    {
        refreshTimer.Start();
        if (startupPaths.Length > 0)
            AcceptPaths(startupPaths, bringToFront: false);
        ready.TrySetResult();
    }

    /// <summary>Called from the pipe server when the uploader is launched again.</summary>
    public async Task<bool> AcceptFromOtherInstanceAsync(IReadOnlyList<string> paths)
    {
        try
        {
            await ready.Task.WaitAsync(TimeSpan.FromSeconds(10));
            var answer = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            BeginInvoke(() => answer.TrySetResult(AcceptPaths(paths, bringToFront: true)));
            return await answer.Task.WaitAsync(TimeSpan.FromSeconds(10));
        }
        catch
        {
            return false;
        }
    }

    private bool AcceptPaths(IReadOnlyList<string> paths, bool bringToFront)
    {
        if (userClosing || IsDisposed)
            return false;

        if (bringToFront)
        {
            if (WindowState == FormWindowState.Minimized)
                WindowState = FormWindowState.Normal;
            Activate();
        }

        if (paths.Count == 0)
            return true;

        closeTimer.Stop();
        try
        {
            var added = engine.Enqueue(paths);
            if (added == 0 && !engine.IsRunning)
                message = "No files were found.";
            else if (added > 0)
                message = null;
        }
        catch (Exception ex)
        {
            if (!engine.IsRunning)
                message = $"Cannot read files: {ex.Message}";
        }

        RefreshView();
        return true;
    }

    private void OpenSettings()
    {
        var path = AppSettings.DefaultPath;
        if (!AppSettings.TryCreate(path))
        {
            MessageBox.Show(this, $"Cannot create the settings file:{Environment.NewLine}{path}{Environment.NewLine}{Environment.NewLine}Check that this folder can be written to.",
                "Settings", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo("notepad.exe", $"\"{path}\"") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"Cannot open {path}: {ex.Message}", "Settings", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    /// <summary>Re-reads the settings file when it changes, for the "Sending to" line.</summary>
    private string CurrentTarget()
    {
        var path = AppSettings.DefaultPath;
        DateTime stamp;
        try { stamp = File.Exists(path) ? File.GetLastWriteTimeUtc(path) : DateTime.MinValue.AddTicks(1); }
        catch { stamp = DateTime.MinValue.AddTicks(1); }

        if (stamp != settingsStamp)
        {
            settingsStamp = stamp;
            try { targetText = "Sending to: " + AppSettings.Load(path).TargetDescription; }
            catch (SettingsException ex) { targetText = "Settings problem: " + ex.Message; }
        }
        return targetText;
    }

    private void TogglePause()
    {
        if (engine.Pause.IsPaused) engine.Pause.Resume();
        else engine.Pause.Pause();
        RefreshView();
    }

    private void OnFinished(BatchResult result)
    {
        if (IsDisposed)
            return;

        lastResult = result;
        message = null;
        RefreshView();

        if (userClosing)
        {
            Close();
            return;
        }

        if (result.Success)
        {
            closeTimer.Start();
        }
        else if (result.Failures.Count > 1)
        {
            var lines = result.Failures.Select(f => $"{f.DisplayName}: {f.Error}");
            MessageBox.Show(this, string.Join(Environment.NewLine, lines), "Files that were not uploaded",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    private void OnFormClosing(object? sender, FormClosingEventArgs e)
    {
        if (!engine.IsRunning)
            return;

        // Stop the upload cleanly first; the window closes once it has.
        e.Cancel = true;
        userClosing = true;
        engine.Cancel();
    }

    private void OnDragEnter(object? sender, DragEventArgs e)
    {
        var ok = !userClosing && e.Data?.GetDataPresent(DataFormats.FileDrop) == true;
        e.Effect = ok ? DragDropEffects.Copy : DragDropEffects.None;
        if (view.DragActive != ok)
        {
            view.DragActive = ok;
            view.Invalidate();
        }
    }

    private void OnDragLeave(object? sender, EventArgs e)
    {
        view.DragActive = false;
        view.Invalidate();
    }

    private void OnDragDrop(object? sender, DragEventArgs e)
    {
        view.DragActive = false;
        if (e.Data?.GetData(DataFormats.FileDrop) is string[] paths)
            AcceptPaths(paths, bringToFront: false);
        view.Invalidate();
    }

    private void RefreshView()
    {
        var snap = engine.GetSnapshot();
        UpdateSpeed(snap);

        view.SpeedSamples = samples.ToArray();
        view.Paused = snap.Paused;
        view.PauseEnabled = snap.Running;
        view.CancelEnabled = snap.Running;

        if (snap.Running)
            ShowRunning(snap);
        else if (message is not null)
            ShowIdle("Nothing to upload", UploaderView.Warning, message, CurrentTarget());
        else if (lastResult is not null)
            ShowResult(lastResult, snap);
        else
            ShowIdle("Ready", UploaderView.HeadlineNormal, CurrentTarget());

        view.Invalidate();
    }

    private void UpdateSpeed(EngineSnapshot snap)
    {
        var now = clock.Elapsed.TotalSeconds;
        if (snap.Running && !wasRunning)
        {
            // New run: start a fresh graph.
            samples.Clear();
            smoothedSpeed = 0;
            lastWireBytes = 0;
            lastTick = now;
        }
        wasRunning = snap.Running;
        if (!snap.Running)
            return;

        var seconds = Math.Max(0.05, now - lastTick);
        var instant = Math.Max(0, snap.WireBytes - lastWireBytes) / seconds;
        lastWireBytes = snap.WireBytes;
        lastTick = now;

        smoothedSpeed = snap.Paused ? 0 : smoothedSpeed <= 0 ? instant : smoothedSpeed * 0.6 + instant * 0.4;
        samples.Add(smoothedSpeed);
        if (samples.Count > SampleCapacity)
            samples.RemoveAt(0);
    }

    private void ShowRunning(EngineSnapshot snap)
    {
        var percent = (int)Math.Floor(snap.Fraction * 100);
        view.Headline = $"{percent}% complete";
        view.HeadlineColor = UploaderView.HeadlineNormal;
        view.SpeedText = FormatSpeed(snap.Paused ? 0 : smoothedSpeed);
        view.Progress = snap.Fraction;
        view.BarColor = snap.Paused ? UploaderView.Warning : UploaderView.BarNormal;

        var what = snap.Group ?? snap.File ?? "...";
        view.Line1 = snap.Paused ? $"Paused: {what}"
            : snap.Retrying ? $"Retrying: {what}"
            : snap.File is null ? "Connecting..." : $"Uploading: {what}";
        view.Line2 = snap.File is null ? string.Empty : $"File:  {snap.File}";

        view.FooterLeft = "Time remaining: " + (snap.Paused ? "Paused" : FormatEta(snap.RemainingBytes, smoothedSpeed));
        view.FooterRight = $"Items remaining: {snap.RemainingCount} ({FormatSize(snap.RemainingBytes)})";
    }

    private void ShowResult(BatchResult r, EngineSnapshot snap)
    {
        view.SpeedText = string.Empty;
        view.FooterLeft = "Time remaining: -";
        view.FooterRight = $"Items remaining: {r.Total - r.Uploaded - r.Failures.Count}";
        var files = r.Total == 1 ? "file" : "files";

        if (r.Success)
        {
            view.Headline = "Transfer complete";
            view.HeadlineColor = UploaderView.Success;
            view.Line1 = $"Uploaded {r.Uploaded} {files}";
            view.Line2 = closeTimer.Enabled ? "Closing..." : string.Empty;
            view.Progress = 1;
            view.BarColor = UploaderView.Success;
            view.FooterRight = "Items remaining: 0";
        }
        else if (r.Cancelled)
        {
            view.Headline = "Cancelled";
            view.HeadlineColor = UploaderView.HeadlineNormal;
            view.Line1 = $"Uploaded {r.Uploaded} of {r.Total} {files}";
            view.Line2 = "Drop files to start again";
            view.Progress = 0;
        }
        else if (r.FatalError is not null)
        {
            var connect = r.FatalError.StartsWith(BatchResult.ConnectPrefix, StringComparison.Ordinal);
            var settings = r.FatalError.StartsWith(BatchResult.SettingsPrefix, StringComparison.Ordinal);
            view.Headline = connect ? ConnectHeadline : settings ? "Settings file problem" : "Upload stopped";
            view.HeadlineColor = UploaderView.Error;
            view.Line1 = connect ? r.FatalError[BatchResult.ConnectPrefix.Length..]
                : settings ? r.FatalError[BatchResult.SettingsPrefix.Length..]
                : r.FatalError;
            view.Line2 = settings ? "Click the settings button to fix it, then send the files again"
                : $"Uploaded {r.Uploaded} of {r.Total} {files}";
            view.Progress = snap.Fraction;
            view.BarColor = UploaderView.Error;
        }
        else
        {
            var first = r.Failures[0];
            var more = r.Failures.Count > 1 ? $" (+{r.Failures.Count - 1} more)" : string.Empty;
            view.Headline = "Completed with errors";
            view.HeadlineColor = UploaderView.Warning;
            view.Line1 = $"Uploaded {r.Uploaded} of {r.Total} {files}";
            view.Line2 = $"Failed: {first.DisplayName} - {first.Error}{more}";
            view.Progress = 1;
            view.BarColor = UploaderView.Warning;
            view.FooterRight = "Items remaining: 0";
        }
    }

    private void ShowIdle(string headline, Color color, string line1, string? line2 = null)
    {
        view.Headline = headline;
        view.HeadlineColor = color;
        view.SpeedText = string.Empty;
        view.Line1 = line1;
        view.Line2 = line2 ?? "Right-click a file or folder > Mitsubishi Uploader";
        view.Progress = 0;
        view.FooterLeft = "Time remaining: -";
        view.FooterRight = "Items remaining: 0";
    }

    internal static string FormatSize(long bytes)
    {
        const double kb = 1024, mb = kb * 1024, gb = mb * 1024;
        return bytes >= gb ? $"{bytes / gb:0.0} GB"
            : bytes >= mb ? $"{bytes / mb:0.0} MB"
            : bytes >= kb ? $"{bytes / kb:0.0} KB"
            : $"{bytes} B";
    }

    internal static string FormatSpeed(double bytesPerSecond) =>
        bytesPerSecond >= 1024 * 1024 ? $"{bytesPerSecond / (1024 * 1024):0.0} MB/s"
        : $"{bytesPerSecond / 1024:0} KB/s";

    internal static string FormatEta(long remainingBytes, double bytesPerSecond)
    {
        if (remainingBytes <= 0)
            return "Finishing...";
        if (bytesPerSecond < 1)
            return "Calculating...";
        var seconds = remainingBytes / bytesPerSecond;
        if (seconds < 5) return "A few seconds";
        if (seconds < 60) return $"About {Math.Ceiling(seconds / 5) * 5:0} seconds";
        if (seconds < 3600)
        {
            var minutes = (int)Math.Ceiling(seconds / 60);
            return minutes == 1 ? "About 1 minute" : $"About {minutes} minutes";
        }
        var span = TimeSpan.FromSeconds(seconds);
        return $"About {(int)span.TotalHours} h {span.Minutes} min";
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
}

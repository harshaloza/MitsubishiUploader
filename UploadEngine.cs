/// <summary>One file to upload. <paramref name="Group"/> is the dropped folder's name, or null for a single file.</summary>
internal sealed record TransferItem(string File, string? RemoteDirectory, string? Group, string DisplayName, long Length)
{
    public string FullDisplayName => Group is null ? DisplayName : $"{Group}\\{DisplayName}";
}

internal sealed record TransferFailure(string DisplayName, string Error);

internal sealed record BatchResult(int Total, int Uploaded, IReadOnlyList<TransferFailure> Failures, string? FatalError, bool Cancelled)
{
    public const string ConnectPrefix = "Cannot connect to CNC: ";
    public const string SettingsPrefix = "Settings file problem: ";

    public bool Success => FatalError is null && !Cancelled && Failures.Count == 0 && Total > 0;
}

/// <summary>What the window shows; taken a couple of times per second.</summary>
internal sealed record EngineSnapshot(
    bool Running, bool Paused, bool Retrying, string? Group, string? File,
    int TotalCount, int DoneCount, long TotalBytes, long DoneBytes, long WireBytes)
{
    public int RemainingCount => Math.Max(0, TotalCount - DoneCount);
    public long RemainingBytes => Math.Max(0, TotalBytes - DoneBytes);

    public double Fraction => TotalBytes > 0
        ? Math.Clamp((double)DoneBytes / TotalBytes, 0, 1)
        : TotalCount > 0 ? (double)DoneCount / TotalCount : 0;
}

/// <summary>Async on/off switch: while paused, <see cref="WaitAsync"/> does not complete.</summary>
internal sealed class PauseGate
{
    private readonly object sync = new();
    private TaskCompletionSource? paused;

    public bool IsPaused { get { lock (sync) return paused is not null; } }

    public void Pause()
    {
        lock (sync) paused ??= new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    public void Resume()
    {
        TaskCompletionSource? waiting;
        lock (sync) { waiting = paused; paused = null; }
        waiting?.TrySetResult();
    }

    public Task WaitAsync(CancellationToken token)
    {
        lock (sync) return paused is null ? Task.CompletedTask : paused.Task.WaitAsync(token);
    }
}

/// <summary>
/// A queue of files uploaded one after another over a single FTP session.
/// Files added while an upload is running join the same run.
/// Each failed file is retried once; the run stops early if the CNC cannot be reached at all.
/// </summary>
internal sealed class UploadEngine
{
    private readonly Func<FtpSettings> settingsProvider;
    private readonly object sync = new();
    private readonly Queue<TransferItem> pending = new();
    private readonly HashSet<string> pendingFiles = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<TransferFailure> failures = new();
    private CancellationTokenSource? cts;
    private TaskCompletionSource<BatchResult>? runCompletion;
    private TransferItem? current;
    private bool running;
    private bool retrying;
    private int totalCount, doneCount, uploadedCount;
    private long totalBytes, finishedBytes;
    private long currentSent, wireBytes; // updated with Interlocked

    /// <param name="settingsProvider">Called at the start of every run, so settings changes apply to the next upload.</param>
    public UploadEngine(Func<FtpSettings> settingsProvider) => this.settingsProvider = settingsProvider;

    public UploadEngine(FtpSettings settings) : this(() => settings) { }

    public PauseGate Pause { get; } = new();

    public TimeSpan RetryDelay { get; init; } = TimeSpan.FromSeconds(1);

    /// <summary>Raised when a run ends (all files done, cancelled, or connection lost).</summary>
    public event Action<BatchResult>? Finished;

    public bool IsRunning { get { lock (sync) return running; } }

    /// <summary>Completes when the current run ends; null if nothing has run yet.</summary>
    public Task<BatchResult>? CurrentRun { get { lock (sync) return runCompletion?.Task; } }

    /// <summary>Adds files/folders to the queue, starting a run if none is active. Returns the number of files added.</summary>
    public int Enqueue(IEnumerable<string> paths)
    {
        var items = TransferPlanner.Build(paths).ToList();
        if (items.Count == 0)
            return 0;

        var added = 0;
        var start = false;
        CancellationToken token;
        lock (sync)
        {
            if (!running)
            {
                running = true;
                start = true;
                totalCount = doneCount = uploadedCount = 0;
                totalBytes = finishedBytes = 0;
                Interlocked.Exchange(ref currentSent, 0);
                Interlocked.Exchange(ref wireBytes, 0);
                failures.Clear();
                cts = new CancellationTokenSource();
                runCompletion = new TaskCompletionSource<BatchResult>(TaskCreationOptions.RunContinuationsAsynchronously);
                Pause.Resume();
            }

            foreach (var item in items)
            {
                if (!pendingFiles.Add(item.File))
                    continue; // already waiting in the queue
                pending.Enqueue(item);
                totalCount++;
                totalBytes += item.Length;
                added++;
            }
            token = cts!.Token;
        }

        if (start)
            _ = RunAsync(token);
        return added;
    }

    /// <summary>Stops the current file and drops everything still queued.</summary>
    public void Cancel()
    {
        lock (sync) cts?.Cancel();
    }

    public EngineSnapshot GetSnapshot()
    {
        lock (sync)
        {
            var sent = Interlocked.Read(ref currentSent);
            return new EngineSnapshot(
                running, Pause.IsPaused, retrying, current?.Group, current?.DisplayName,
                totalCount, doneCount, totalBytes, finishedBytes + sent, Interlocked.Read(ref wireBytes));
        }
    }

    private async Task RunAsync(CancellationToken token)
    {
        string? fatal = null;
        var cancelled = false;
        while (true)
        {
            try
            {
                await using var session = new FtpSession(settingsProvider());
                await ProcessQueueAsync(session, token);
            }
            catch (SettingsException ex)
            {
                fatal = BatchResult.SettingsPrefix + ex.Message;
            }
            catch (Exception) when (token.IsCancellationRequested)
            {
                cancelled = true;
            }
            catch (FtpConnectionException ex)
            {
                fatal = BatchResult.ConnectPrefix + ex.Message;
            }
            catch (Exception ex)
            {
                fatal = ex.Message;
            }

            BatchResult result;
            TaskCompletionSource<BatchResult>? completion;
            lock (sync)
            {
                // Files may have arrived while the session was closing: keep going.
                if (!cancelled && fatal is null && pending.Count > 0)
                    continue;

                pending.Clear();
                pendingFiles.Clear();
                current = null;
                retrying = false;
                running = false;
                result = new BatchResult(totalCount, uploadedCount, failures.ToArray(), fatal, cancelled);
                completion = runCompletion;
            }

            completion?.TrySetResult(result);
            Finished?.Invoke(result);
            return;
        }
    }

    private async Task ProcessQueueAsync(FtpSession session, CancellationToken token)
    {
        while (true)
        {
            TransferItem item;
            lock (sync)
            {
                if (!pending.TryDequeue(out var next))
                    return;
                item = next;
                pendingFiles.Remove(item.File);
                current = item;
                retrying = false;
                Interlocked.Exchange(ref currentSent, 0);
            }

            try
            {
                await Pause.WaitAsync(token);
                await UploadWithRetryAsync(session, item, token);
                lock (sync) uploadedCount++;
            }
            catch (Exception ex) when (!token.IsCancellationRequested && ex is not FtpConnectionException)
            {
                lock (sync) failures.Add(new TransferFailure(item.FullDisplayName, ex.Message));
            }
            finally
            {
                lock (sync)
                {
                    doneCount++;
                    finishedBytes += item.Length;
                    Interlocked.Exchange(ref currentSent, 0);
                    current = null;
                }
            }
        }
    }

    private async Task UploadWithRetryAsync(FtpSession session, TransferItem item, CancellationToken token)
    {
        try
        {
            await session.UploadAsync(item.File, item.RemoteDirectory, OnBytesSent, Pause, token);
        }
        catch (Exception) when (!token.IsCancellationRequested)
        {
            lock (sync) retrying = true;
            Interlocked.Exchange(ref currentSent, 0);
            await Task.Delay(RetryDelay, token);
            await Pause.WaitAsync(token);
            await session.UploadAsync(item.File, item.RemoteDirectory, OnBytesSent, Pause, token);
        }
    }

    private void OnBytesSent(long count)
    {
        Interlocked.Add(ref currentSent, count);
        Interlocked.Add(ref wireBytes, count);
    }
}

internal static class TransferPlanner
{
    /// <summary>Expands files and folders (recursively) into upload items, keeping folder structure.</summary>
    public static IEnumerable<TransferItem> Build(IEnumerable<string> paths)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in paths)
        {
            if (File.Exists(path))
            {
                var file = Path.GetFullPath(path);
                if (seen.Add(file))
                    yield return new TransferItem(file, null, null, Path.GetFileName(file), new FileInfo(file).Length);
                continue;
            }

            if (!Directory.Exists(path))
                continue;

            var root = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var rootName = Path.GetFileName(root);
            foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories).OrderBy(value => value, StringComparer.OrdinalIgnoreCase))
            {
                var fullPath = Path.GetFullPath(file);
                if (!seen.Add(fullPath))
                    continue;

                var relativeDirectory = Path.GetDirectoryName(Path.GetRelativePath(root, fullPath));
                var remoteDirectory = string.IsNullOrEmpty(relativeDirectory) || relativeDirectory == "."
                    ? rootName
                    : $"{rootName}/{relativeDirectory.Replace(Path.DirectorySeparatorChar, '/').Replace(Path.AltDirectorySeparatorChar, '/')}";
                var displayName = Path.GetRelativePath(root, fullPath);
                yield return new TransferItem(fullPath, remoteDirectory, rootName, displayName, new FileInfo(fullPath).Length);
            }
        }
    }
}

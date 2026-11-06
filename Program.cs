using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text;

internal static class Program
{
    private static readonly string MutexName = $@"Local\MitsubishiUploader.SingleInstance";

    [STAThread]
    private static int Main(string[] args)
    {
        ApplicationConfiguration.Initialize();

        // Setup / uninstall (MitsubishiUploader-Setup.exe, --install, --uninstall).
        if (Installer.TryRun(args, out var exitCode))
            return exitCode;

        var paths = SingleInstance.NormalizePaths(args);

        // Only one window: a second launch (e.g. right-click while uploading) hands its
        // files to the window that is already open and exits.
        using var mutex = new Mutex(false, MutexName);
        var isFirst = TryAcquire(mutex, TimeSpan.Zero);
        if (!isFirst)
        {
            if (SingleInstance.TrySend(paths))
                return 0;
            // The open window did not answer (probably closing): wait for it, then take over.
            isFirst = TryAcquire(mutex, TimeSpan.FromSeconds(5));
        }

        using var form = new MainForm(paths);
        using (isFirst ? SingleInstance.StartServer(form.AcceptFromOtherInstanceAsync) : null)
        {
            Application.Run(form);
        }

        if (isFirst)
            mutex.ReleaseMutex();
        return 0;
    }

    private static bool TryAcquire(Mutex mutex, TimeSpan timeout)
    {
        try { return mutex.WaitOne(timeout); }
        catch (AbandonedMutexException) { return true; }
    }
}

/// <summary>Passes file paths from a second launch to the running window over a named pipe.</summary>
internal static class SingleInstance
{
    public static string PipeName { get; set; } = $"MitsubishiUploader.{Process.GetCurrentProcess().SessionId}";

    private static readonly Encoding Utf8 = new UTF8Encoding(false);

    public static string[] NormalizePaths(IEnumerable<string> args) =>
        args.Where(a => !string.IsNullOrWhiteSpace(a))
            .Select(a => { try { return Path.GetFullPath(a.Trim()); } catch { return a; } })
            .ToArray();

    /// <summary>Sends paths to the running window. True only if that window accepted them.</summary>
    public static bool TrySend(IReadOnlyList<string> paths, int connectTimeoutMs = 5000)
    {
        try
        {
            using var client = new NamedPipeClientStream(".", PipeName, PipeDirection.InOut,
                PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            client.Connect(connectTimeoutMs);

            // Let the running window come to the front.
            AllowSetForegroundWindow(-1);

            using var writer = new StreamWriter(client, Utf8, 4096, leaveOpen: true) { NewLine = "\n" };
            foreach (var path in paths)
                writer.WriteLine(path);
            writer.WriteLine(); // empty line = end of list
            writer.Flush();

            using var reader = new StreamReader(client, Utf8, false, 1024, leaveOpen: true);
            var answer = reader.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(15)).GetAwaiter().GetResult();
            return answer == "OK";
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Listens for other launches until disposed. <paramref name="accept"/> returns false to refuse.</summary>
    public static IDisposable StartServer(Func<IReadOnlyList<string>, Task<bool>> accept)
    {
        var cts = new CancellationTokenSource();
        _ = Task.Run(() => ServeAsync(accept, cts.Token));
        return new Stopper(cts);
    }

    private static async Task ServeAsync(Func<IReadOnlyList<string>, Task<bool>> accept, CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            NamedPipeServerStream? server = null;
            try
            {
                server = new NamedPipeServerStream(PipeName, PipeDirection.InOut,
                    NamedPipeServerStream.MaxAllowedServerInstances, PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                await server.WaitForConnectionAsync(token);
            }
            catch (OperationCanceledException)
            {
                server?.Dispose();
                return;
            }
            catch
            {
                server?.Dispose();
                try { await Task.Delay(500, token); } catch { return; }
                continue;
            }

            _ = HandleClientAsync(server, accept, token);
        }
    }

    private static async Task HandleClientAsync(NamedPipeServerStream server, Func<IReadOnlyList<string>, Task<bool>> accept, CancellationToken token)
    {
        await using (server)
        {
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
                timeout.CancelAfter(TimeSpan.FromSeconds(10));

                using var reader = new StreamReader(server, Utf8, false, 4096, leaveOpen: true);
                var paths = new List<string>();
                while (true)
                {
                    var line = await reader.ReadLineAsync(timeout.Token);
                    if (string.IsNullOrEmpty(line))
                        break;
                    paths.Add(line);
                }

                var accepted = await accept(paths);

                using var writer = new StreamWriter(server, Utf8, 1024, leaveOpen: true) { NewLine = "\n", AutoFlush = true };
                await writer.WriteLineAsync(accepted ? "OK" : "NO");
                server.WaitForPipeDrain();
            }
            catch { }
        }
    }

    [DllImport("user32.dll")]
    private static extern bool AllowSetForegroundWindow(int processId);

    private sealed class Stopper(CancellationTokenSource cts) : IDisposable
    {
        public void Dispose() => cts.Cancel();
    }
}

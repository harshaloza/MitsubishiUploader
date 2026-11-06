using System.Net;
using System.Net.Sockets;
using System.Text;

/// <summary>Connection details; built from <see cref="AppSettings"/> (the settings file).</summary>
internal sealed record FtpSettings(string Host, int Port, string User, string Password, IReadOnlyList<string> BaseDirectories)
{
    /// <summary>Time allowed to open the control or data connection.</summary>
    public TimeSpan ConnectTimeout { get; init; } = TimeSpan.FromSeconds(15);

    /// <summary>Time allowed for the server to answer a command.</summary>
    public TimeSpan CommandTimeout { get; init; } = TimeSpan.FromSeconds(15);

    /// <summary>Time allowed for each block of file data to be accepted (restarts for every block).</summary>
    public TimeSpan DataTimeout { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>Time allowed for the CNC to confirm a file after all data was sent.</summary>
    public TimeSpan CompletionTimeout { get; init; } = TimeSpan.FromSeconds(60);
}

/// <summary>The CNC could not be reached or refused the login: every remaining file would fail too.</summary>
internal sealed class FtpConnectionException : Exception
{
    public FtpConnectionException(string message, Exception inner) : base(message, inner) { }
}

/// <summary>
/// One FTP control connection reused for many uploads. Any failure drops the connection,
/// so the next upload starts from a clean login.
/// </summary>
internal sealed class FtpSession : IAsyncDisposable
{
    private readonly FtpSettings settings;
    private readonly List<string> currentDirectory = new();
    private TcpClient? control;
    private StreamReader? reader;
    private StreamWriter? writer;
    private bool cdupUnsupported;

    public FtpSession(FtpSettings settings) => this.settings = settings;

    private sealed record FtpReply(int Code, string Text)
    {
        public bool IsSuccess => Code is >= 200 and < 300;
    }

    /// <param name="bytesSent">Called after every block written, with the block size.</param>
    /// <param name="pause">While paused, no further data is sent.</param>
    public async Task UploadAsync(string file, string? remoteDirectory, Action<long>? bytesSent, PauseGate? pause, CancellationToken token)
    {
        try
        {
            await EnsureConnectedAsync(token);
            await ChangeDirectoryAsync(SplitRemotePath(remoteDirectory), token);
            await StoreAsync(file, bytesSent, pause, token);
        }
        catch
        {
            Disconnect();
            throw;
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (control is not null)
        {
            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                await SendAsync("QUIT", cts.Token);
            }
            catch { }
        }
        Disconnect();
    }

    private async Task EnsureConnectedAsync(CancellationToken token)
    {
        if (control is not null)
            return;

        try
        {
            var client = new TcpClient();
            control = client;
            await WithTimeoutAsync(async ct => await client.ConnectAsync(settings.Host, settings.Port, ct),
                settings.ConnectTimeout, $"Connecting to {settings.Host}:{settings.Port}", token);

            var stream = client.GetStream();
            reader = new StreamReader(stream, Encoding.ASCII, false, 1024, true);
            writer = new StreamWriter(stream, Encoding.ASCII, 1024, true) { NewLine = "\r\n", AutoFlush = true };

            Expect(await ReadReplyAsync(settings.CommandTimeout, "Welcome message", token), 220, "welcome");
            var r = await SendAsync($"USER {settings.User}", token);
            if (r.Code == 331) r = await SendAsync($"PASS {settings.Password}", token);
            Expect(r, 230, "login");

            foreach (var directory in settings.BaseDirectories)
            {
                r = await SendAsync($"CWD {directory}", token);
                if (!r.IsSuccess) throw new InvalidOperationException($"Cannot open '{directory}': {r.Text}");
            }

            Expect(await SendAsync("TYPE I", token), 200, "binary mode");
            currentDirectory.Clear();
        }
        catch (Exception ex) when (!token.IsCancellationRequested)
        {
            Disconnect();
            throw new FtpConnectionException(ex.Message, ex);
        }
    }

    private void Disconnect()
    {
        reader?.Dispose();
        writer?.Dispose();
        control?.Dispose();
        reader = null;
        writer = null;
        control = null;
        currentDirectory.Clear();
    }

    /// <summary>Moves from the current folder to <paramref name="target"/> (relative to the memory card).</summary>
    private async Task ChangeDirectoryAsync(IReadOnlyList<string> target, CancellationToken token)
    {
        var common = 0;
        while (common < currentDirectory.Count && common < target.Count
               && string.Equals(currentDirectory[common], target[common], StringComparison.Ordinal))
            common++;

        if (currentDirectory.Count > common)
        {
            if (!cdupUnsupported && await TryGoUpAsync(currentDirectory.Count - common, token))
            {
                currentDirectory.RemoveRange(common, currentDirectory.Count - common);
            }
            else
            {
                // A fresh login always lands on the memory card folder.
                Disconnect();
                await EnsureConnectedAsync(token);
                common = 0;
            }
        }

        for (var i = common; i < target.Count; i++)
        {
            await EnterDirectoryAsync(target[i], token);
            currentDirectory.Add(target[i]);
        }
    }

    private async Task<bool> TryGoUpAsync(int levels, CancellationToken token)
    {
        for (var i = 0; i < levels; i++)
        {
            var r = await SendAsync("CDUP", token);
            if (!r.IsSuccess)
            {
                cdupUnsupported = true;
                return false;
            }
        }
        return true;
    }

    private async Task EnterDirectoryAsync(string name, CancellationToken token)
    {
        var r = await SendAsync($"CWD {name}", token);
        if (r.IsSuccess)
            return;

        var mkd = await SendAsync($"MKD {name}", token);
        r = await SendAsync($"CWD {name}", token);
        if (!r.IsSuccess)
            throw new InvalidOperationException($"Cannot create remote folder '{name}': {mkd.Text}");
    }

    private async Task StoreAsync(string file, Action<long>? bytesSent, PauseGate? pause, CancellationToken token)
    {
        // Open the local file first so a locked/missing file never leaves an empty file on the CNC.
        await using var input = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, useAsync: true);

        var r = await SendAsync("PASV", token);
        Expect(r, 227, "PASV");
        var endpoint = ParsePasv(r.Text);

        using var data = new TcpClient();
        await WithTimeoutAsync(async ct => await data.ConnectAsync(endpoint.Address, endpoint.Port, ct),
            settings.ConnectTimeout, "Opening data connection", token);

        r = await SendAsync($"STOR {SanitizeName(Path.GetFileName(file))}", token);
        if (r.Code != 150 && r.Code != 125) throw new InvalidOperationException($"STOR failed: {r.Text}");

        using (var dataStream = data.GetStream())
        using (var writeCts = CancellationTokenSource.CreateLinkedTokenSource(token))
        {
            var buffer = new byte[64 * 1024];
            int read;
            try
            {
                while ((read = await input.ReadAsync(buffer.AsMemory(0, buffer.Length), token)) > 0)
                {
                    if (pause is not null)
                        await pause.WaitAsync(token);

                    writeCts.CancelAfter(settings.DataTimeout);          // limit for this block only
                    await dataStream.WriteAsync(buffer.AsMemory(0, read), writeCts.Token);
                    writeCts.CancelAfter(Timeout.InfiniteTimeSpan);      // stop the clock (e.g. while paused)
                    bytesSent?.Invoke(read);
                }
            }
            catch (Exception ex) when (writeCts.IsCancellationRequested && !token.IsCancellationRequested)
            {
                throw new TimeoutException($"Sending data timed out after {settings.DataTimeout.TotalSeconds:0} s.", ex);
            }
        }

        r = await ReadReplyAsync(settings.CompletionTimeout, "Transfer confirmation", token);
        if (r.Code != 226 && r.Code != 250) throw new InvalidOperationException($"Transfer final response failed: {r.Text}");
    }

    private Task<FtpReply> SendAsync(string command, CancellationToken token) =>
        WithTimeoutAsync(async ct =>
        {
            await writer!.WriteLineAsync(command.AsMemory(), ct);
            return await ReadReplyCoreAsync(ct);
        }, settings.CommandTimeout, $"Command {command.Split(' ')[0]}", token);

    private Task<FtpReply> ReadReplyAsync(TimeSpan timeout, string what, CancellationToken token) =>
        WithTimeoutAsync(ReadReplyCoreAsync, timeout, what, token);

    private async Task<FtpReply> ReadReplyCoreAsync(CancellationToken token)
    {
        var first = await reader!.ReadLineAsync(token) ?? throw new IOException("FTP server closed the connection.");
        if (first.Length < 3 || !int.TryParse(first[..3], out var code)) throw new IOException($"Invalid FTP response: {first}");
        if (first.Length >= 4 && first[3] == '-')
        {
            var prefix = first[..3] + " ";
            do { first = await reader.ReadLineAsync(token) ?? throw new IOException("FTP server closed a multiline response."); }
            while (!first.StartsWith(prefix, StringComparison.Ordinal));
        }
        return new FtpReply(code, first);
    }

    private static async Task WithTimeoutAsync(Func<CancellationToken, Task> operation, TimeSpan timeout, string what, CancellationToken token) =>
        await WithTimeoutAsync(async ct => { await operation(ct); return true; }, timeout, what, token);

    /// <summary>
    /// Runs a network operation with a time limit. A hit limit becomes a <see cref="TimeoutException"/>;
    /// a user cancel always surfaces as <see cref="OperationCanceledException"/>.
    /// </summary>
    private static async Task<T> WithTimeoutAsync<T>(Func<CancellationToken, Task<T>> operation, TimeSpan timeout, string what, CancellationToken token)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(token);
        cts.CancelAfter(timeout);
        try
        {
            return await operation(cts.Token);
        }
        catch (Exception ex) when (token.IsCancellationRequested && ex is not OperationCanceledException)
        {
            throw new OperationCanceledException(token);
        }
        catch (Exception ex) when (cts.IsCancellationRequested && !token.IsCancellationRequested)
        {
            throw new TimeoutException($"{what} timed out after {timeout.TotalSeconds:0} s.", ex);
        }
    }

    private static void Expect(FtpReply reply, int expected, string operation)
    {
        if (reply.Code != expected) throw new InvalidOperationException($"{operation} failed: {reply.Text}");
    }

    private static string SanitizeName(string name) => name.Replace("\r", string.Empty).Replace("\n", string.Empty);

    private static List<string> SplitRemotePath(string? remoteDirectory)
    {
        if (string.IsNullOrWhiteSpace(remoteDirectory))
            return new List<string>();
        return remoteDirectory.Replace('\\', '/')
            .Split('/', StringSplitOptions.RemoveEmptyEntries)
            .Select(SanitizeName)
            .Where(part => part.Length > 0 && part != "." && part != "..")
            .ToList();
    }

    private (IPAddress Address, int Port) ParsePasv(string reply)
    {
        var start = reply.IndexOf('(');
        var end = start < 0 ? -1 : reply.IndexOf(')', start + 1);
        if (start < 0 || end < 0) throw new InvalidOperationException($"Invalid PASV response: {reply}");
        var p = reply[(start + 1)..end].Split(',');
        if (p.Length != 6 || !int.TryParse(p[4], out var p1) || !int.TryParse(p[5], out var p2)) throw new InvalidOperationException($"Invalid PASV response: {reply}");
        var address = IPAddress.Parse($"{p[0]}.{p[1]}.{p[2]}.{p[3]}");
        if (IPAddress.IsLoopback(address) || address.Equals(IPAddress.Any) || address.Equals(IPAddress.None))
        {
            address = control?.Client.RemoteEndPoint is IPEndPoint ep
                ? ep.Address
                : Dns.GetHostAddresses(settings.Host).First(a => a.AddressFamily == AddressFamily.InterNetwork);
        }
        return (address, p1 * 256 + p2);
    }
}

using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;

internal sealed class FakeFtp : IDisposable
{
    public readonly string Root;
    public readonly string Base;
    public bool CdupSupported = true;
    public bool HangOnWelcome;
    public int ReadDelayMs;                 // throttle: delay per 16 KB received
    public HashSet<string> HangOnComplete = new(StringComparer.OrdinalIgnoreCase);
    public HashSet<string> FailMkd = new(StringComparer.OrdinalIgnoreCase);
    public int FailFirstStors;
    public int Logins;
    public int Stors;
    private readonly TcpListener listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource stop = new();
    public int Port => ((IPEndPoint)listener.LocalEndpoint).Port;

    public FakeFtp(string root)
    {
        Root = root;
        if (Directory.Exists(root)) Directory.Delete(root, true);
        Base = Path.Combine(root, "[SUBNET](192,168,200,1)", "MEMORY CARD");
        Directory.CreateDirectory(Base);
        listener.Start();
        _ = AcceptLoop();
    }

    private async Task AcceptLoop()
    {
        while (!stop.IsCancellationRequested)
        {
            TcpClient c;
            try { c = await listener.AcceptTcpClientAsync(stop.Token); } catch { return; }
            _ = Handle(c);
        }
    }

    private async Task Handle(TcpClient client)
    {
        using var _c = client;
        var s = client.GetStream();
        var r = new StreamReader(s, Encoding.ASCII);
        var w = new StreamWriter(s, Encoding.ASCII) { NewLine = "\r\n", AutoFlush = true };
        if (HangOnWelcome) { try { await Task.Delay(-1, stop.Token); } catch { } return; }
        await w.WriteLineAsync("220-Fake NC Explorer\r\n220 ready");
        var cwd = Root;
        TcpListener? pasv = null;
        try
        {
            while (true)
            {
                var line = await r.ReadLineAsync();
                if (line is null) return;
                var sp = line.IndexOf(' ');
                var cmd = (sp < 0 ? line : line[..sp]).ToUpperInvariant();
                var arg = sp < 0 ? "" : line[(sp + 1)..];
                switch (cmd)
                {
                    case "USER": await w.WriteLineAsync("331 pw"); break;
                    case "PASS": Interlocked.Increment(ref Logins); await w.WriteLineAsync("230 ok"); break;
                    case "TYPE": await w.WriteLineAsync("200 ok"); break;
                    case "CWD":
                        var t = Path.Combine(cwd, arg);
                        if (Directory.Exists(t)) { cwd = t; await w.WriteLineAsync("250 ok"); }
                        else await w.WriteLineAsync("550 no such dir");
                        break;
                    case "CDUP":
                        if (!CdupSupported) { await w.WriteLineAsync("502 not implemented"); break; }
                        if (cwd.Length <= Base.Length) { await w.WriteLineAsync("550 refusing to leave base"); break; }
                        cwd = Path.GetDirectoryName(cwd)!; await w.WriteLineAsync("250 ok"); break;
                    case "MKD":
                        if (FailMkd.Contains(arg)) { await w.WriteLineAsync("550 Permission denied"); break; }
                        var m = Path.Combine(cwd, arg);
                        if (Directory.Exists(m)) { await w.WriteLineAsync("550 exists"); break; }
                        Directory.CreateDirectory(m); await w.WriteLineAsync("257 created"); break;
                    case "PASV":
                        pasv = new TcpListener(IPAddress.Loopback, 0); pasv.Start();
                        var p = ((IPEndPoint)pasv.LocalEndpoint).Port;
                        await w.WriteLineAsync($"227 Entering Passive Mode (127,0,0,1,{p / 256},{p % 256})"); break;
                    case "STOR":
                        Interlocked.Increment(ref Stors);
                        using (var d = await pasv!.AcceptTcpClientAsync())
                        {
                            pasv.Stop();
                            if (FailFirstStors > 0) { FailFirstStors--; await w.WriteLineAsync("451 transient error"); break; }
                            await w.WriteLineAsync("150 go");
                            d.ReceiveBufferSize = 16 * 1024;
                            using var f = File.Create(Path.Combine(cwd, arg));
                            var buf = new byte[16 * 1024];
                            int n;
                            var ds = d.GetStream();
                            while ((n = await ds.ReadAsync(buf)) > 0)
                            {
                                await f.WriteAsync(buf.AsMemory(0, n));
                                if (ReadDelayMs > 0) await Task.Delay(ReadDelayMs);
                            }
                        }
                        if (HangOnComplete.Contains(arg)) { await Task.Delay(-1, stop.Token); }
                        await w.WriteLineAsync("226 done"); break;
                    case "QUIT": await w.WriteLineAsync("221 bye"); return;
                    default: await w.WriteLineAsync("500 ?"); break;
                }
            }
        }
        catch { }
    }

    public void Dispose() { stop.Cancel(); listener.Stop(); }
}

internal static class TestHarness
{
    static readonly string Scratch = Path.Combine(AppContext.BaseDirectory, "work");
    static int failures;

    static void Check(bool ok, string what)
    {
        Console.WriteLine($"  [{(ok ? "PASS" : "FAIL")}] {what}");
        if (!ok) failures++;
    }

    static string MakeLocal(string name, int sizeFactor, params string[] files)
    {
        var dir = Path.Combine(Scratch, "local", name);
        if (Directory.Exists(dir)) Directory.Delete(dir, true);
        foreach (var f in files)
        {
            var p = Path.Combine(dir, f);
            Directory.CreateDirectory(Path.GetDirectoryName(p)!);
            File.WriteAllText(p, $"content of {f} " + new string('x', f.Length * sizeFactor));
        }
        return dir;
    }

    static string MakeLocal(string name, params string[] files) => MakeLocal(name, 1000, files);

    static FtpSettings Settings(FakeFtp s, double timeoutSec = 2) => new("127.0.0.1", s.Port, "MITSUBISHI", "CNC",
        new[] { "[SUBNET](192,168,200,1)", "MEMORY CARD" })
    {
        ConnectTimeout = TimeSpan.FromSeconds(timeoutSec),
        CommandTimeout = TimeSpan.FromSeconds(timeoutSec),
        DataTimeout = TimeSpan.FromSeconds(timeoutSec),
        CompletionTimeout = TimeSpan.FromSeconds(timeoutSec),
    };

    static UploadEngine Engine(FtpSettings st) => new(st) { RetryDelay = TimeSpan.FromMilliseconds(200) };

    static async Task<(BatchResult r, double sec)> Run(FtpSettings st, params string[] paths)
    {
        var sw = Stopwatch.StartNew();
        var e = Engine(st);
        e.Enqueue(paths);
        var r = await e.CurrentRun!;
        return (r, sw.Elapsed.TotalSeconds);
    }

    static bool SameFile(string local, string remote) => File.Exists(remote) && File.ReadAllText(local) == File.ReadAllText(remote);

    static readonly string[] Tree = { "O1000.NC", "SUB1/O2000.NC", "SUB1/DEEP/O3000.NC", "SUB1/DEEP/O3001.NC", "SUB2/O4000.NC", "Z.NC" };

    static bool TreeArrived(FakeFtp s, string local, string[]? tree = null)
    {
        var name = Path.GetFileName(local);
        return (tree ?? Tree).All(f => SameFile(Path.Combine(local, f), Path.Combine(s.Base, name, f)));
    }

    [STAThread]
    public static int Main(string[] args)
    {
        if (args.Contains("--render"))
        {
            Render(Path.Combine(AppContext.BaseDirectory, "render"));
            return 0;
        }
        return Task.Run(RunTests).GetAwaiter().GetResult();
    }

    static async Task<int> RunTests()
    {
        Console.WriteLine("1. Folder with subfolders + single file");
        using (var s = new FakeFtp(Path.Combine(Scratch, "srv1")))
        {
            var local = MakeLocal("PROGS", Tree);
            var single = MakeLocal("single", "O9999.NC");
            var (r, _) = await Run(Settings(s), local, Path.Combine(single, "O9999.NC"));
            Check(r.Success && r.Total == 7, $"success, 7 files (uploaded={r.Uploaded})");
            Check(TreeArrived(s, local), "all files in correct remote folders with correct content");
            Check(SameFile(Path.Combine(single, "O9999.NC"), Path.Combine(s.Base, "O9999.NC")), "single file lands in MEMORY CARD root");
            Check(s.Logins == 1, $"one login for 7 files (logins={s.Logins})");
        }

        Console.WriteLine("2. Server without CDUP support");
        using (var s = new FakeFtp(Path.Combine(Scratch, "srv2")) { CdupSupported = false })
        {
            var local = MakeLocal("PROGS", Tree);
            var (r, _) = await Run(Settings(s), local);
            Check(r.Success && TreeArrived(s, local), $"success (logins={s.Logins})");
        }

        Console.WriteLine("3. Files added while an upload is running join the same run");
        using (var s = new FakeFtp(Path.Combine(Scratch, "srv3")) { ReadDelayMs = 5 })
        {
            var a = MakeLocal("FIRST", 20000, Tree);
            var b = MakeLocal("SECOND", 20000, "P1.NC", "P2/P3.NC");
            var e = Engine(Settings(s, 10));
            e.Enqueue(new[] { a });
            var run = e.CurrentRun!;
            await Task.Delay(300);
            var stillRunning = e.IsRunning;
            var added = e.Enqueue(new[] { b });
            var same = ReferenceEquals(run, e.CurrentRun);
            var mid = e.GetSnapshot();
            var r = await run;
            Check(stillRunning && same && added == 2, $"second batch queued into the running upload (added={added}, sameRun={same})");
            Check(mid.TotalCount == 8, $"queue total grew to 8 (was {mid.TotalCount})");
            Check(r.Success && r.Total == 8 && TreeArrived(s, a) && TreeArrived(s, b, new[] { "P1.NC", "P2/P3.NC" }), $"all 8 files uploaded (uploaded={r.Uploaded})");
            Check(s.Logins == 1, $"still a single login (logins={s.Logins})");
            var after = e.GetSnapshot();
            Check(!after.Running && after.DoneCount == 8 && after.RemainingBytes == 0 && after.Fraction == 1, "stats at end: 8 done, 0 bytes left, 100%");
        }

        Console.WriteLine("4. Pause stops sending, Resume continues");
        using (var s = new FakeFtp(Path.Combine(Scratch, "srv4")) { ReadDelayMs = 10 })
        {
            var local = MakeLocal("BIG", 600000, "LARGE.NC"); // ~4.8 MB
            var e = Engine(Settings(s, 3));
            e.Enqueue(new[] { local });
            await Task.Delay(500);
            e.Pause.Pause();
            await Task.Delay(300);
            var x = e.GetSnapshot();
            await Task.Delay(4000); // longer than the 3 s data timeout: must not time out while paused
            var y = e.GetSnapshot();
            Check(x.Paused && x.DoneBytes > 0 && x.DoneBytes < x.TotalBytes, $"paused part-way ({x.DoneBytes * 100 / x.TotalBytes}% sent)");
            Check(y.DoneBytes == x.DoneBytes, $"no data sent while paused ({x.DoneBytes} -> {y.DoneBytes})");
            e.Pause.Resume();
            var r = await e.CurrentRun!;
            Check(r.Success && TreeArrived(s, local, new[] { "LARGE.NC" }), $"completes after resume, content intact ({(r.Success ? "ok" : r.Failures.FirstOrDefault()?.Error ?? r.FatalError)})");
        }

        Console.WriteLine("5. CNC not answering -> stop early");
        using (var s = new FakeFtp(Path.Combine(Scratch, "srv5")) { HangOnWelcome = true })
        {
            var local = MakeLocal("PROGS", Tree);
            var (r, sec) = await Run(Settings(s, 1), local);
            Check(r.FatalError?.StartsWith(BatchResult.ConnectPrefix) == true, $"fatal: {r.FatalError}");
            Check(sec < 4, $"stopped after {sec:0.0}s");
        }

        Console.WriteLine("6. One file never confirmed -> times out, retried once, others upload");
        using (var s = new FakeFtp(Path.Combine(Scratch, "srv6")))
        {
            s.HangOnComplete.Add("O2000.NC");
            var local = MakeLocal("PROGS", Tree);
            var (r, _) = await Run(Settings(s, 1), local);
            Check(r.Uploaded == 5 && r.Failures.Count == 1 && r.Failures[0].Error.Contains("timed out") && s.Stors == 7,
                $"5 ok, 1 failed: {r.Failures.FirstOrDefault()?.DisplayName} - {r.Failures.FirstOrDefault()?.Error}");
        }

        Console.WriteLine("7. Transient error -> retry succeeds");
        using (var s = new FakeFtp(Path.Combine(Scratch, "srv7")) { FailFirstStors = 1 })
        {
            var local = MakeLocal("PROGS", Tree);
            var (r, _) = await Run(Settings(s), local);
            Check(r.Success && TreeArrived(s, local), "success");
        }

        Console.WriteLine("8. Cancel while CNC is not answering, then a new run works");
        using (var s = new FakeFtp(Path.Combine(Scratch, "srv8")))
        {
            s.HangOnComplete.Add("O1000.NC");
            var local = MakeLocal("PROGS", Tree);
            var e = Engine(Settings(s, 60));
            var sw = Stopwatch.StartNew();
            e.Enqueue(new[] { local });
            await Task.Delay(500);
            e.Cancel();
            var r = await e.CurrentRun!;
            Check(r.Cancelled && sw.Elapsed.TotalSeconds < 2 && !e.IsRunning, $"cancelled in {sw.Elapsed.TotalSeconds:0.0}s, queue dropped");
            s.HangOnComplete.Clear();
            e.Enqueue(new[] { local });
            r = await e.CurrentRun!;
            Check(r.Success && r.Total == 6, "next run after cancel starts fresh and succeeds");
        }

        Console.WriteLine("9. Second launch hands files to the running window (named pipe)");
        {
            SingleInstance.PipeName = "MitsubishiUploaderTest." + Guid.NewGuid().ToString("N");
            var received = new ConcurrentBag<string>();
            using (SingleInstance.StartServer(paths => { foreach (var p in paths) received.Add(p); return Task.FromResult(true); }))
            {
                var sends = Enumerable.Range(0, 12).Select(i => Task.Run(() =>
                    SingleInstance.TrySend(new[] { $@"C:\job\file{i}a.NC", $@"C:\job\file{i}b.NC" }))).ToArray();
                var results = await Task.WhenAll(sends);
                Check(results.All(x => x) && received.Count == 24, $"12 launches at once all accepted, {received.Count}/24 paths received");
            }

            using (SingleInstance.StartServer(_ => Task.FromResult(false)))
                Check(!SingleInstance.TrySend(new[] { @"C:\x.NC" }), "window that is closing refuses -> sender knows");

            await Task.Delay(200);
            var sw = Stopwatch.StartNew();
            Check(!SingleInstance.TrySend(new[] { @"C:\x.NC" }, 500) && sw.Elapsed.TotalSeconds < 2, "no window running -> sender starts its own");
        }

        Console.WriteLine("11. Settings file");
        {
            var dir = Path.Combine(Scratch, "settings");
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
            Directory.CreateDirectory(dir);
            var path = Path.Combine(dir, AppSettings.FileName);

            var d = AppSettings.Load(path);
            Check(File.Exists(path), "missing file is created with defaults");
            Check(d.ToFtpSettings().BaseDirectories.SequenceEqual(new[] { "[SUBNET](192,168,200,1)", "MEMORY CARD" })
                  && d.ToFtpSettings().Host == "NCExplorer" && d.ToFtpSettings().Port == 1217, "defaults = old built-in values");
            var created = AppSettings.Load(path);
            Check(created.TargetDescription == "192.168.200.1 > MEMORY CARD", $"created file (with comments) reads back: {created.TargetDescription}");

            File.WriteAllText(path, File.ReadAllText(path)
                .Replace("\"192.168.200.1\"", "\"10.0.0.5\"").Replace("\"MEMORY CARD\"", "\"MEMORY CARD/PROGRAMS\""));
            var edited = AppSettings.Load(path).ToFtpSettings();
            Check(edited.BaseDirectories.SequenceEqual(new[] { "[SUBNET](10,0,0,5)", "MEMORY CARD", "PROGRAMS" }),
                "edited IP + subfolder -> " + string.Join(" / ", edited.BaseDirectories));

            string Error(string json) { File.WriteAllText(path, json); try { AppSettings.Load(path); return "(no error)"; } catch (SettingsException ex) { return ex.Message; } }
            var e1 = Error("{ \"CncIpAddress\": \"192.168.200\" }");
            Check(e1.Contains("not a valid IP address"), e1);
            var e2 = Error("{\n  \"CncIpAddress\": \"192.168.200.1\"\n  \"DestinationFolder\": \"MEMORY CARD\"\n}");
            Check(e2.Contains("not written correctly") && e2.Contains("line 3"), e2);
            var e3 = Error("{ \"DestinationFolder\": \"  \" }");
            Check(e3.Contains("DestinationFolder is empty"), e3);

            // End to end: engine reads the file at every run, so a change applies to the next upload.
            using var s = new FakeFtp(Path.Combine(Scratch, "srv11"));
            Directory.CreateDirectory(Path.Combine(s.Root, "[SUBNET](10,0,0,5)", "MEMORY CARD", "PROGRAMS"));
            File.WriteAllText(path, $$"""{ "CncIpAddress": "10.0.0.5", "DestinationFolder": "MEMORY CARD/PROGRAMS", "NcExplorerHost": "127.0.0.1", "NcExplorerPort": {{s.Port}} }""");
            var engine = new UploadEngine(() => AppSettings.Load(path).ToFtpSettings()) { RetryDelay = TimeSpan.FromMilliseconds(200) };
            var single = MakeLocal("single11", "O5555.NC");
            engine.Enqueue(new[] { Path.Combine(single, "O5555.NC") });
            var r = await engine.CurrentRun!;
            Check(r.Success && SameFile(Path.Combine(single, "O5555.NC"), Path.Combine(s.Root, "[SUBNET](10,0,0,5)", "MEMORY CARD", "PROGRAMS", "O5555.NC")),
                "file lands in the folder from the settings file");

            File.WriteAllText(path, $$"""{ "CncIpAddress": "192.168.200.1", "DestinationFolder": "MEMORY CARD", "NcExplorerHost": "127.0.0.1", "NcExplorerPort": {{s.Port}} }""");
            engine.Enqueue(new[] { Path.Combine(single, "O5555.NC") });
            r = await engine.CurrentRun!;
            Check(r.Success && File.Exists(Path.Combine(s.Base, "O5555.NC")), "changed settings used by the next upload, no restart");

            File.WriteAllText(path, "{ \"CncIpAddress\": \"abc\" }");
            engine.Enqueue(new[] { Path.Combine(single, "O5555.NC") });
            r = await engine.CurrentRun!;
            Check(r.FatalError?.StartsWith(BatchResult.SettingsPrefix) == true, $"bad settings stop the upload with: {r.FatalError}");
        }

        Console.WriteLine("10. Text formatting");
        Check(MainForm.FormatSize(19084083) == "18.2 MB", MainForm.FormatSize(19084083));
        Check(MainForm.FormatSpeed(13002342) == "12.4 MB/s", MainForm.FormatSpeed(13002342));
        Check(MainForm.FormatEta(19084083, 1.5 * 1024 * 1024) == "About 15 seconds", MainForm.FormatEta(19084083, 1.5 * 1024 * 1024));
        Check(MainForm.FormatEta(100L * 1024 * 1024, 1024 * 1024) == "About 2 minutes", MainForm.FormatEta(100L * 1024 * 1024, 1024 * 1024));

        Console.WriteLine(failures == 0 ? "\nALL PASSED" : $"\n{failures} FAILED");
        return failures == 0 ? 0 : 1;
    }

    static void Render(string outDir)
    {
        Directory.CreateDirectory(outDir);
        Application.EnableVisualStyles();
        var rnd = new Random(3);
        var samples = new List<double>();
        double v = 6.5;
        for (var i = 0; i < 110; i++) { v = Math.Clamp(v + (rnd.NextDouble() - 0.42) * 0.9, 5, 14); samples.Add(v * 1024 * 1024); }

        void Save(string name, Action<UploaderView> setup)
        {
            using var view = new UploaderView { Width = 600, Height = 376 };
            setup(view);
            using var bmp = new Bitmap(view.Width, view.Height);
            view.DrawToBitmap(bmp, new Rectangle(0, 0, bmp.Width, bmp.Height));
            bmp.Save(Path.Combine(outDir, name + ".png"));
        }

        Save("running", w =>
        {
            w.Headline = "65% complete"; w.SpeedText = "12.4 MB/s";
            w.Line1 = "Uploading: New folder"; w.Line2 = "File:  Machine_Program_01.pcf";
            w.Progress = 0.65; w.FooterLeft = "Time remaining: About 15 seconds"; w.FooterRight = "Items remaining: 24 (18.2 MB)";
            w.SpeedSamples = samples; w.PauseEnabled = w.CancelEnabled = true;
        });
        Save("idle", w =>
        {
            w.Line1 = "Waiting for files"; w.Line2 = "Drop files here, or right-click a file or folder > Mitsubishi Uploader";
            w.FooterLeft = "Time remaining: -"; w.FooterRight = "Items remaining: 0";
        });
        Save("error", w =>
        {
            w.DragActive = true;
            w.Headline = "Cannot connect to CNC"; w.HeadlineColor = UploaderView.Error;
            w.Line1 = "Welcome message timed out after 15 s."; w.Line2 = "Uploaded 3 of 27 files";
            w.Progress = 0.11; w.BarColor = UploaderView.Error; w.SpeedSamples = samples.Take(30).ToList();
            w.FooterLeft = "Time remaining: -"; w.FooterRight = "Items remaining: 24";
        });
        Console.WriteLine("rendered to " + outDir);
    }
}

using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32;

/// <summary>
/// The exe installs and uninstalls itself (per user, no admin needed):
///   MitsubishiUploader-Setup.exe           double-click: asks, then installs
///   MitsubishiUploader.exe --install        same, from any file name
///   MitsubishiUploader.exe --uninstall      used by Windows Settings > Apps
/// Add --quiet to skip the questions and messages.
/// Installs to C:\Mitsubishi Uploader, adds the right-click menu for files and folders,
/// a Start menu shortcut and an entry in Settings > Apps. The settings file is kept on uninstall.
/// </summary>
internal static class Installer
{
    private const string AppName = "Mitsubishi Uploader";
    private const string ExeName = "MitsubishiUploader.exe";
    private const string Publisher = "harshaloza";
    private const string ProjectUrl = "https://github.com/harshaloza/MitsubishiUploader";
    private const string UninstallKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\MitsubishiUploader";

    private static readonly string[] MenuKeys =
    {
        @"Software\Classes\*\shell\Mitsubishi Uploader",
        @"Software\Classes\Directory\shell\Mitsubishi Uploader",
    };

    // Entries left by older versions of the batch installer.
    private static readonly string[] OldMenuKeys =
    {
        @"Software\Classes\*\shell\SendToMitsubishiCNC",
        @"Software\Classes\Directory\shell\SendToMitsubishiCNC",
    };

    /// <summary>Overridable for tests.</summary>
    public static string InstallDir { get; set; } =
        Path.Combine(Path.GetPathRoot(Environment.SystemDirectory) ?? @"C:\", AppName);

    public static string InstalledExe => Path.Combine(InstallDir, ExeName);

    private static string ShortcutPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), AppName + ".lnk");

    private static string Version =>
        FileVersionInfo.GetVersionInfo(Environment.ProcessPath!).ProductVersion?.Split('+')[0] ?? "";

    /// <summary>Runs setup or uninstall if the command line (or the exe name) asks for it.</summary>
    public static bool TryRun(string[] args, out int exitCode)
    {
        var quiet = args.Any(a => a.Equals("--quiet", StringComparison.OrdinalIgnoreCase));
        if (args.Any(a => a.Equals("--uninstall", StringComparison.OrdinalIgnoreCase)))
        {
            exitCode = Uninstall(quiet);
            return true;
        }

        var name = Path.GetFileNameWithoutExtension(Environment.ProcessPath ?? string.Empty);
        if (args.Any(a => a.Equals("--install", StringComparison.OrdinalIgnoreCase))
            || (args.All(a => a.StartsWith("--")) && name.Contains("setup", StringComparison.OrdinalIgnoreCase)))
        {
            exitCode = Install(quiet);
            return true;
        }

        exitCode = 0;
        return false;
    }

    public static int Install(bool quiet)
    {
        var version = Version;
        if (!quiet && !Ask($"Install {AppName} {version}?{Environment.NewLine}{Environment.NewLine}" +
                           $"It will be installed to {InstallDir} and added to the right-click menu of files and folders.{Environment.NewLine}" +
                           "No administrator rights are needed."))
            return 1;

        try
        {
            var source = Environment.ProcessPath!;
            Directory.CreateDirectory(InstallDir);
            if (!SamePath(source, InstalledExe))
            {
                CloseRunningCopies();
                // Copy next to the target first, then swap in, so a failed copy never leaves half a file.
                var temp = InstalledExe + ".new";
                File.Copy(source, temp, overwrite: true);
                File.Move(temp, InstalledExe, overwrite: true);
            }

            foreach (var key in OldMenuKeys)
                Registry.CurrentUser.DeleteSubKeyTree(key, throwOnMissingSubKey: false);
            foreach (var key in MenuKeys)
                WriteMenuKey(key, InstalledExe);

            CreateShortcut(ShortcutPath, InstalledExe);
            WriteUninstallEntry(version);
            AppSettings.TryCreate(Path.Combine(InstallDir, AppSettings.FileName));
            RefreshExplorer();
        }
        catch (Exception ex)
        {
            Show($"Setup could not finish:{Environment.NewLine}{ex.Message}{Environment.NewLine}{Environment.NewLine}" +
                 $"If {AppName} is open, close it and run setup again.", MessageBoxIcon.Error, quiet);
            return 2;
        }

        Show($"{AppName} {version} is installed.{Environment.NewLine}{Environment.NewLine}" +
             $"Right-click a file or folder and choose \"{AppName}\".{Environment.NewLine}" +
             "To change the CNC IP address or folder, click the gear button in the window.", MessageBoxIcon.Information, quiet);
        return 0;
    }

    public static int Uninstall(bool quiet)
    {
        if (!quiet && !Ask($"Uninstall {AppName}?{Environment.NewLine}{Environment.NewLine}" +
                           "The right-click menu, the Start menu shortcut and the program are removed. Your settings file is kept."))
            return 1;

        try
        {
            CloseRunningCopies();
            foreach (var key in MenuKeys.Concat(OldMenuKeys))
                Registry.CurrentUser.DeleteSubKeyTree(key, throwOnMissingSubKey: false);
            Registry.CurrentUser.DeleteSubKeyTree(UninstallKey, throwOnMissingSubKey: false);
            if (File.Exists(ShortcutPath))
                File.Delete(ShortcutPath);
            RefreshExplorer();
            DeleteProgramFile();
        }
        catch (Exception ex)
        {
            Show($"Uninstall could not finish:{Environment.NewLine}{ex.Message}", MessageBoxIcon.Error, quiet);
            return 2;
        }

        Show($"{AppName} was uninstalled.", MessageBoxIcon.Information, quiet);
        return 0;
    }

    private static void WriteMenuKey(string keyPath, string exe)
    {
        using var key = Registry.CurrentUser.CreateSubKey(keyPath);
        key.SetValue(string.Empty, AppName);
        key.SetValue("MUIVerb", AppName);
        key.SetValue("Icon", exe);
        using var command = key.CreateSubKey("command");
        command.SetValue(string.Empty, $"\"{exe}\" \"%1\"");
    }

    private static void WriteUninstallEntry(string version)
    {
        using var key = Registry.CurrentUser.CreateSubKey(UninstallKey);
        key.SetValue("DisplayName", AppName);
        key.SetValue("DisplayVersion", version);
        key.SetValue("Publisher", Publisher);
        key.SetValue("DisplayIcon", InstalledExe);
        key.SetValue("InstallLocation", InstallDir);
        key.SetValue("InstallDate", DateTime.Now.ToString("yyyyMMdd"));
        key.SetValue("URLInfoAbout", ProjectUrl);
        key.SetValue("UninstallString", $"\"{InstalledExe}\" --uninstall");
        key.SetValue("QuietUninstallString", $"\"{InstalledExe}\" --uninstall --quiet");
        key.SetValue("NoModify", 1, RegistryValueKind.DWord);
        key.SetValue("NoRepair", 1, RegistryValueKind.DWord);
        key.SetValue("EstimatedSize", (int)(new FileInfo(InstalledExe).Length / 1024), RegistryValueKind.DWord);
    }

    private static void CreateShortcut(string path, string target)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var shellType = Type.GetTypeFromProgID("WScript.Shell") ?? throw new InvalidOperationException("WScript.Shell is not available.");
        dynamic shell = Activator.CreateInstance(shellType)!;
        try
        {
            dynamic link = shell.CreateShortcut(path);
            link.TargetPath = target;
            link.WorkingDirectory = Path.GetDirectoryName(target);
            link.IconLocation = target + ",0";
            link.Description = "Send files to the Mitsubishi CNC";
            link.Save();
            Marshal.FinalReleaseComObject(link);
        }
        finally
        {
            Marshal.FinalReleaseComObject(shell);
        }
    }

    /// <summary>Closes other running copies of the installed exe so it can be replaced or removed.</summary>
    private static void CloseRunningCopies()
    {
        var self = Environment.ProcessId;
        foreach (var process in Process.GetProcessesByName(Path.GetFileNameWithoutExtension(ExeName)))
        {
            using (process)
            {
                try
                {
                    if (process.Id == self || !SamePath(process.MainModule?.FileName, InstalledExe))
                        continue;
                    process.CloseMainWindow();
                    if (!process.WaitForExit(5000))
                    {
                        process.Kill();
                        process.WaitForExit(3000);
                    }
                }
                catch { }
            }
        }
    }

    /// <summary>Deletes the installed exe (after this process exits, if it is the one running) and the folder if empty.</summary>
    private static void DeleteProgramFile()
    {
        if (!SamePath(Environment.ProcessPath, InstalledExe))
        {
            if (File.Exists(InstalledExe))
                File.Delete(InstalledExe);
            TryDeleteEmptyFolder();
            return;
        }

        // A running exe cannot delete itself: a hidden cmd waits for this process to exit, then deletes it.
        var script = $"/c ping -n 3 127.0.0.1 >nul & del /f /q \"{InstalledExe}\" & rmdir \"{InstallDir}\" 2>nul";
        Process.Start(new ProcessStartInfo("cmd.exe", script)
        {
            CreateNoWindow = true,
            UseShellExecute = false,
            WorkingDirectory = Path.GetTempPath(),
        });
    }

    private static void TryDeleteEmptyFolder()
    {
        try
        {
            if (Directory.Exists(InstallDir) && !Directory.EnumerateFileSystemEntries(InstallDir).Any())
                Directory.Delete(InstallDir);
        }
        catch { }
    }

    private static bool SamePath(string? a, string? b) =>
        a is not null && b is not null &&
        string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase);

    private static bool Ask(string text) =>
        MessageBox.Show(text, AppName, MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes;

    private static void Show(string text, MessageBoxIcon icon, bool quiet)
    {
        if (!quiet)
            MessageBox.Show(text, AppName, MessageBoxButtons.OK, icon);
    }

    private static void RefreshExplorer() => SHChangeNotify(0x08000000 /* SHCNE_ASSOCCHANGED */, 0, IntPtr.Zero, IntPtr.Zero);

    [DllImport("shell32.dll")]
    private static extern void SHChangeNotify(int eventId, uint flags, IntPtr item1, IntPtr item2);
}

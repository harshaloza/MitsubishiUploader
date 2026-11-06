using System.Net;
using System.Net.Sockets;
using System.Text.Json;

/// <summary>
/// User-editable settings in MitsubishiUploader.settings.json next to the exe.
/// Read at the start of every upload, so edits apply to the next upload without a rebuild.
/// </summary>
internal sealed class AppSettings
{
    public const string FileName = "MitsubishiUploader.settings.json";

    public static string DefaultPath => Path.Combine(AppContext.BaseDirectory, FileName);

    public string? CncIpAddress { get; set; } = "192.168.200.1";
    public string? DestinationFolder { get; set; } = "MEMORY CARD";
    public string? NcExplorerHost { get; set; } = "NCExplorer";
    public int NcExplorerPort { get; set; } = 1217;
    public string? UserName { get; set; } = "MITSUBISHI";
    public string? Password { get; set; } = "CNC";

    private const string Template = """
        {
          // IP address of the CNC, as shown in NC Explorer.
          "CncIpAddress": "192.168.200.1",

          // Folder on the CNC to copy files into.
          // Use / for a subfolder, for example "MEMORY CARD/PROGRAMS". The folder must already exist.
          "DestinationFolder": "MEMORY CARD",

          // NC Explorer connection. Normally there is no need to change these.
          "NcExplorerHost": "NCExplorer",
          "NcExplorerPort": 1217,
          "UserName": "MITSUBISHI",
          "Password": "CNC"
        }

        """;

    private static readonly JsonSerializerOptions Options = new()
    {
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        PropertyNameCaseInsensitive = true,
    };

    /// <summary>Reads and checks the file. Creates it with default values if it does not exist yet.</summary>
    /// <exception cref="SettingsException">The file cannot be read or has a wrong value.</exception>
    public static AppSettings Load(string path)
    {
        if (!File.Exists(path))
        {
            TryCreate(path);
            return new AppSettings();
        }

        AppSettings? settings;
        try
        {
            settings = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(path), Options);
        }
        catch (JsonException ex)
        {
            var where = ex.LineNumber is long line ? $" (line {line + 1})" : string.Empty;
            throw new SettingsException($"{FileName} is not written correctly{where}. Check quotes and commas.");
        }
        catch (Exception ex)
        {
            throw new SettingsException($"Cannot read {FileName}: {ex.Message}");
        }

        settings ??= new AppSettings();
        settings.Validate();
        return settings;
    }

    /// <summary>Writes the default file if it is missing. Returns false if it cannot be written.</summary>
    public static bool TryCreate(string path)
    {
        try
        {
            if (!File.Exists(path))
                File.WriteAllText(path, Template.Replace("\n", "\r\n"));
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>NC Explorer's folder name for the CNC, e.g. [SUBNET](192,168,200,1).</summary>
    public string CncFolder
    {
        get
        {
            var ip = (CncIpAddress ?? string.Empty).Trim();
            return ip.StartsWith('[') ? ip : $"[SUBNET]({ip.Replace('.', ',')})";
        }
    }

    public string[] DestinationParts =>
        (DestinationFolder ?? string.Empty)
            .Split('/', '\\')
            .Select(part => part.Trim())
            .Where(part => part.Length > 0)
            .ToArray();

    /// <summary>For display, e.g. "192.168.200.1 > MEMORY CARD".</summary>
    public string TargetDescription => $"{CncIpAddress?.Trim()} > {string.Join(" > ", DestinationParts)}";

    public FtpSettings ToFtpSettings() =>
        new(NcExplorerHost!.Trim(), NcExplorerPort, UserName ?? string.Empty, Password ?? string.Empty,
            new[] { CncFolder }.Concat(DestinationParts).ToArray());

    private void Validate()
    {
        var ip = (CncIpAddress ?? string.Empty).Trim();
        if (!ip.StartsWith('[') && !IsIPv4(ip))
            throw new SettingsException($"CncIpAddress \"{ip}\" is not a valid IP address (example: 192.168.200.1).");

        if (DestinationParts.Length == 0)
            throw new SettingsException("DestinationFolder is empty (example: MEMORY CARD).");

        if (string.IsNullOrWhiteSpace(NcExplorerHost))
            throw new SettingsException("NcExplorerHost is empty (normally: NCExplorer).");

        if (NcExplorerPort is < 1 or > 65535)
            throw new SettingsException($"NcExplorerPort {NcExplorerPort} is not valid (normally: 1217).");
    }

    private static bool IsIPv4(string value) =>
        value.Split('.').Length == 4
        && IPAddress.TryParse(value, out var address)
        && address.AddressFamily == AddressFamily.InterNetwork;
}

internal sealed class SettingsException : Exception
{
    public SettingsException(string message) : base(message) { }
}

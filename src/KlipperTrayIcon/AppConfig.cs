using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace KlipperTrayIcon;

/// <summary>
/// Settings, stored as config.json next to the exe. Same keys as the Python
/// version (1.x), so an existing config.json can simply be copied over.
/// </summary>
public sealed class AppConfig
{
    [JsonPropertyName("printer_type")] public string PrinterType { get; set; } = "klipper";
    [JsonPropertyName("moonraker_url")] public string MoonrakerUrl { get; set; } = "http://mainsail.local";
    [JsonPropertyName("moonraker_api_key")] public string MoonrakerApiKey { get; set; } = "";
    [JsonPropertyName("bambu_ip")] public string BambuIp { get; set; } = "";
    [JsonPropertyName("bambu_serial")] public string BambuSerial { get; set; } = "";
    [JsonPropertyName("bambu_access_code")] public string BambuAccessCode { get; set; } = "";
    [JsonPropertyName("update_interval_seconds")] public double UpdateIntervalSeconds { get; set; } = 2;
    [JsonPropertyName("notifications")] public bool Notifications { get; set; } = true;
    [JsonPropertyName("webcam_console")] public bool WebcamConsole { get; set; } = true;
    /// <summary>Hide the commands timelapse macros send on every frame.</summary>
    [JsonPropertyName("console_hide_timelapse")] public bool ConsoleHideTimelapse { get; set; } = true;
    /// <summary>Moonraker webcam to show; empty picks the first one.</summary>
    [JsonPropertyName("webcam_name")] public string WebcamName { get; set; } = "";
    /// <summary>Webcam the popup can switch to (e.g. a remote touchscreen); empty for none.</summary>
    [JsonPropertyName("second_view_name")] public string SecondViewName { get; set; } = "";
    /// <summary>How to fetch each: "h264", "mjpeg" or "snapshots"; empty picks the best one the camera offers.</summary>
    [JsonPropertyName("webcam_stream")] public string WebcamStream { get; set; } = "";
    [JsonPropertyName("second_view_stream")] public string SecondViewStream { get; set; } = "";

    /// <summary>Keys this version doesn't know about, kept so saving doesn't drop them.</summary>
    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }

    [JsonIgnore] public bool IsBambu => string.Equals(PrinterType, "bambu", StringComparison.OrdinalIgnoreCase);

    [JsonIgnore]
    public TimeSpan UpdateInterval => TimeSpan.FromSeconds(Math.Clamp(UpdateIntervalSeconds, 1, 60));

    public AppConfig Clone() => JsonSerializer.Deserialize<AppConfig>(JsonSerializer.Serialize(this, ConfigStore.JsonOptions), ConfigStore.JsonOptions)!;
}

public static class ConfigStore
{
    internal static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        IndentSize = 4,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        NumberHandling = JsonNumberHandling.AllowReadingFromString,
    };

    /// <summary>Folder the exe lives in (config.json sits here).</summary>
    public static string AppDir { get; } = AppContext.BaseDirectory;

    public static string ConfigPath => Path.Combine(AppDir, "config.json");

    /// <summary>The saved config, or null if there isn't one yet. Throws on unreadable JSON.</summary>
    public static AppConfig? Load()
    {
        if (!File.Exists(ConfigPath))
            return null;
        string json = File.ReadAllText(ConfigPath);
        return JsonSerializer.Deserialize<AppConfig>(json, JsonOptions) ?? new AppConfig();
    }

    public static void Save(AppConfig config)
    {
        // Write then swap, so a crash mid-write can't leave a half-written file.
        string tmp = ConfigPath + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(config, JsonOptions));
        File.Move(tmp, ConfigPath, overwrite: true);
    }

    public static DateTime? LastWriteTime()
    {
        try { return File.Exists(ConfigPath) ? File.GetLastWriteTimeUtc(ConfigPath) : null; }
        catch (IOException) { return null; }
    }
}

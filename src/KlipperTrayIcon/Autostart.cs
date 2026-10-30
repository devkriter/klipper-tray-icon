using System;
using Microsoft.Win32;

namespace KlipperTrayIcon;

/// <summary>"Start with Windows" via the per-user Run registry key (no admin needed).</summary>
public static class Autostart
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    // Same value name as the Python versions (1.x), so upgrading keeps the setting.
    private const string ValueName = "KlipperTrayIcon";

    private static string LaunchCommand => $"\"{Environment.ProcessPath}\"";

    private static string? Read()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey);
        return key?.GetValue(ValueName) as string;
    }

    public static bool IsEnabled => Read() != null;

    public static void SetEnabled(bool enabled)
    {
#if DEBUG
        // Dev builds must never point the user's autostart entry at themselves.
        if (enabled)
            return;
#endif
        using var key = Registry.CurrentUser.CreateSubKey(RunKey, writable: true);
        if (enabled)
            key.SetValue(ValueName, LaunchCommand);
        else
            key.DeleteValue(ValueName, throwOnMissingValue: false);
    }

    /// <summary>
    /// If autostart is on but points somewhere else (an older version, or the
    /// app was unzipped to a new folder), repoint it at this copy.
    /// </summary>
    public static void RefreshPath()
    {
#if DEBUG
        // A dev build must never hijack the installed copy's autostart entry.
        return;
#endif
#pragma warning disable CS0162 // unreachable in Debug builds
        try
        {
            string? current = Read();
            if (current != null && !string.Equals(current, LaunchCommand, StringComparison.OrdinalIgnoreCase))
                SetEnabled(true);
        }
        catch (Exception)
        {
            // Not worth failing startup over.
        }
#pragma warning restore CS0162
    }
}

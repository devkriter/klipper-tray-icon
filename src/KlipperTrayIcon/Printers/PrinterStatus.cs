using System.Collections.Generic;

namespace KlipperTrayIcon.Printers;

/// <summary>
/// State is one of: printing, paused, complete, cancelled, error (the printer
/// reports a fault), standby, or offline (we can't talk to the printer -
/// Message says why). Preparing: printing, but still in the start routine
/// (start macro, heating, heat soak), before the first extrusion.
/// </summary>
public sealed record PrinterStatus(
    string State,
    double Progress,
    string Filename = "",
    double TimeLeftSeconds = 0,
    string EtaSource = "Calc",
    string Message = "",
    PrintDetails? Details = null,
    bool Preparing = false)
{
    public static PrinterStatus Connecting { get; } = new("standby", 0, Message: "Connecting...");

    public static PrinterStatus Offline(string message) => new("offline", 0, Message: message);
}

/// <summary>
/// Extra numbers for the stats strip above the console (Klipper only). Layer
/// counts are null when neither the slicer nor Klipper reports them; Z is the
/// fallback then.
/// </summary>
public sealed record PrintDetails(
    Heater? Nozzle,
    Heater? Bed,
    int? Layer = null,
    int? TotalLayers = null,
    double Z = 0,
    string? Tool = null, // "T2" on printers with several extruders
    IReadOnlyDictionary<string, Heater>? Sensors = null, // other sensors shown, by Klipper name ("temperature_fan chamber")
    IReadOnlyList<string>? Tiles = null); // the stats strip's tiles (see StatusText.StatsTiles)

/// <summary>Target 0 means the heater is off. Heats is false for a plain sensor (or a fan's), which has no target to reach.</summary>
public sealed record Heater(double Temperature, double Target, bool Heats = true);

/// <summary>One line of the Klipper console (Moonraker's gcode store).</summary>
public sealed record ConsoleEntry(string Message, double Time, string Type);

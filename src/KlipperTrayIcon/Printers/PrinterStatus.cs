namespace KlipperTrayIcon.Printers;

/// <summary>
/// State is one of: printing, paused, complete, cancelled, error (the printer
/// reports a fault), standby, or offline (we can't talk to the printer -
/// Message says why).
/// </summary>
public sealed record PrinterStatus(
    string State,
    double Progress,
    string Filename = "",
    double TimeLeftSeconds = 0,
    string EtaSource = "Calc",
    string Message = "")
{
    public static PrinterStatus Connecting { get; } = new("standby", 0, Message: "Connecting...");

    public static PrinterStatus Offline(string message) => new("offline", 0, Message: message);
}

/// <summary>One line of the Klipper console (Moonraker's gcode store).</summary>
public sealed record ConsoleEntry(string Message, double Time, string Type);

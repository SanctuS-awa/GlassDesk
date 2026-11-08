namespace GlassDesk.Core;

public enum SystemNetworkKind
{
    Wifi,
    Ethernet,
    Offline,
    Unknown
}

/// <summary>A privacy-preserving snapshot of connectivity, battery, and default output volume.</summary>
public readonly record struct SystemStatusSnapshot(
    DateTimeOffset CapturedAt,
    SystemNetworkKind NetworkKind,
    bool? HasInternet,
    byte? SignalBars0To5,
    bool? HasBattery,
    byte? BatteryPercent0To100,
    bool? Charging,
    bool? OnAc,
    float? VolumeScalar,
    bool? Muted,
    bool AudioAvailable)
{
    public static SystemStatusSnapshot Unknown(DateTimeOffset capturedAt) => new(
        capturedAt,
        SystemNetworkKind.Unknown,
        null,
        null,
        null,
        null,
        null,
        null,
        null,
        null,
        false);
}

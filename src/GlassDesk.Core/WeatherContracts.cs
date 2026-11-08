namespace GlassDesk.Core;

/// <summary>How the Home weather card chooses its place.</summary>
public enum WeatherLocationMode
{
    /// <summary>Windows location first, then an approximate IP lookup.</summary>
    Auto = 0,
    /// <summary>A province / city / district picked in Settings.</summary>
    Manual = 1
}

/// <summary>Persisted weather choice (settings.json → "Weather"). Coordinates are only stored for a manual pick.</summary>
public sealed record WeatherPreferences
{
    public bool Enabled { get; init; } = true;
    public WeatherLocationMode Mode { get; init; } = WeatherLocationMode.Auto;
    /// <summary>Administrative code of the manual pick (province, city or district); 0 = none.</summary>
    public int RegionCode { get; init; }
    /// <summary>Short display name of the manual pick, e.g. "朝阳区".</summary>
    public string? RegionName { get; init; }
    /// <summary>Full display path of the manual pick, e.g. "北京市 · 朝阳区".</summary>
    public string? RegionPath { get; init; }
    public double? Latitude { get; init; }
    public double? Longitude { get; init; }

    public bool HasManualPlace => RegionCode > 0 && Latitude is double latitude && Longitude is double longitude
        && double.IsFinite(latitude) && double.IsFinite(longitude)
        && latitude is >= -90 and <= 90 && longitude is >= -180 and <= 180;

    public WeatherPreferences Validated()
    {
        static string? Clip(string? value, int max) => string.IsNullOrWhiteSpace(value) ? null
            : value.Trim().Length > max ? value.Trim()[..max] : value.Trim();
        var mode = Mode is WeatherLocationMode.Auto or WeatherLocationMode.Manual ? Mode : WeatherLocationMode.Auto;
        var result = this with { Mode = mode, RegionName = Clip(RegionName, 32), RegionPath = Clip(RegionPath, 96) };
        if (!result.HasManualPlace)
            result = result with { RegionCode = 0, RegionName = null, RegionPath = null, Latitude = null, Longitude = null };
        return result;
    }
}

public enum WeatherStatus
{
    /// <summary>Weather is turned off in Settings.</summary>
    Disabled = 0,
    /// <summary>No data yet; a location or forecast request is running.</summary>
    Loading = 1,
    /// <summary>Fresh data (fetched within the last refresh period plus grace).</summary>
    Ready = 2,
    /// <summary>Showing the last good data because the latest refresh failed (offline, timeout, server error).</summary>
    Stale = 3,
    /// <summary>Manual mode without a picked place, or auto mode where every location source failed.</summary>
    NeedsLocation = 4,
    /// <summary>No data at all and the last attempt failed (offline / server error).</summary>
    Unavailable = 5
}

/// <summary>Icon family used by the Home weather card. Derived from the WMO weather code.</summary>
public enum WeatherIconKind { Unknown = 0, Clear, PartlyCloudy, Cloudy, Fog, Rain, HeavyRain, Snow, Sleet, Thunder }

/// <summary>Immutable view of the current weather for the Home card. Temperatures are °C.</summary>
public sealed record WeatherSnapshot(
    WeatherStatus Status,
    string? PlaceName,
    bool AutoLocated,
    double? TemperatureC,
    double? ApparentTemperatureC,
    int? HumidityPercent,
    double? WindSpeedKmh,
    int? WeatherCode,
    bool IsDay,
    double? TodayMaxC,
    double? TodayMinC,
    DateTimeOffset? FetchedAt,
    string? Message)
{
    public static WeatherSnapshot Initial { get; } = new(WeatherStatus.Loading, null, false,
        null, null, null, null, null, true, null, null, null, null);
    public static WeatherSnapshot Off { get; } = new(WeatherStatus.Disabled, null, false,
        null, null, null, null, null, true, null, null, null, null);
    public bool HasData => TemperatureC is double value && double.IsFinite(value) && WeatherCode is not null;
}

/// <summary>WMO 4677 weather interpretation codes (as returned by Open-Meteo) → Chinese text and icon family.</summary>
public static class WeatherCodes
{
    public static string ChineseText(int? code) => code switch
    {
        0 => "晴",
        1 => "晴间多云",
        2 => "多云",
        3 => "阴",
        45 => "雾",
        48 => "雾凇",
        51 or 53 or 55 => "毛毛雨",
        56 or 57 => "冻毛毛雨",
        61 => "小雨",
        63 => "中雨",
        65 => "大雨",
        66 or 67 => "冻雨",
        71 => "小雪",
        73 => "中雪",
        75 => "大雪",
        77 => "雪粒",
        80 => "阵雨",
        81 => "中阵雨",
        82 => "强阵雨",
        85 => "阵雪",
        86 => "强阵雪",
        95 => "雷阵雨",
        96 or 99 => "雷阵雨伴冰雹",
        _ => "天气未知"
    };

    public static WeatherIconKind Icon(int? code) => code switch
    {
        0 => WeatherIconKind.Clear,
        1 or 2 => WeatherIconKind.PartlyCloudy,
        3 => WeatherIconKind.Cloudy,
        45 or 48 => WeatherIconKind.Fog,
        51 or 53 or 55 or 61 or 63 or 80 or 81 => WeatherIconKind.Rain,
        65 or 82 => WeatherIconKind.HeavyRain,
        56 or 57 or 66 or 67 => WeatherIconKind.Sleet,
        71 or 73 or 75 or 77 or 85 or 86 => WeatherIconKind.Snow,
        95 or 96 or 99 => WeatherIconKind.Thunder,
        _ => WeatherIconKind.Unknown
    };
}

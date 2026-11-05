namespace GlassDesk.Core;

/// <summary>An app-owned support color and its quantized peak opacity.</summary>
public readonly record struct ReadingMaterialSupport(uint ColorArgb, byte Opacity8Bit)
{
    public float Opacity => Opacity8Bit / 255f;
}

/// <summary>
/// Computes conservative local text support from the selected primary ink, configured
/// white tint, and bounded primary transmission loss. This policy does not sample or
/// infer runtime backgrounds.
/// </summary>
public static class ReadingMaterialPolicy
{
    public const double MinimumContrast = 4.5;
    public const double ContrastTarget = 4.6;
    public const double MaximumPrimaryAbsorption = .075;
    // Conservative effective channel after the strongest white tint and the
    // maximum transmission loss of the primary optical edge.
    public const double NativeTintMinimumChannel = 246 / 255d * (1 - MaximumPrimaryAbsorption);
    public const byte MaximumOpacity8Bit = 142;
    public const double MaximumOpacity = MaximumOpacity8Bit / 255d;

    /// <summary>Returns white behind dark primary ink and pure black behind light primary ink.</summary>
    public static ReadingMaterialSupport Resolve(bool lightInk, float tintOpacity)
        => ResolveTarget(lightInk, tintOpacity, ContrastTarget);

    /// <summary>Only for a >=24 DIP clock with no small date/weekday line.</summary>
    public static ReadingMaterialSupport ResolveLargeClock(bool lightInk, float tintOpacity)
        => ResolveTarget(lightInk, tintOpacity, 3.1);

    static ReadingMaterialSupport ResolveTarget(bool lightInk, float tintOpacity, double targetContrast)
    {
        // Invalid values take the darkest-material fallback for dark ink. Finite settings
        // outside the supported interval clamp to its endpoints.
        double tint = float.IsFinite(tintOpacity) ? Math.Clamp(tintOpacity, 0, 1) : 0;
        uint ink = TypographyCatalog.Ink(InkRole.Primary, darkMaterial: lightInk);
        double inkLuminance = Luminance(ink);
        double targetBackgroundLuminance = lightInk
            ? (inkLuminance + .05) / targetContrast - .05
            : targetContrast * (inkLuminance + .05) - .05;
        double targetGray = EncodeSrgb(Math.Clamp(targetBackgroundLuminance, 0, 1));

        double alpha = lightInk
            ? 1 - targetGray
            : Math.Max(0, targetGray - tint * NativeTintMinimumChannel)
                / (1 - tint * NativeTintMinimumChannel);
        byte opacity = (byte)Math.Clamp((int)Math.Ceiling(Math.Clamp(alpha, 0, 1) * 255), 0, 255);
        uint supportColor = lightInk ? 0xFF000000u : 0xFFFFFFFFu;
        return new(supportColor, opacity);
    }

    static double Luminance(uint argb)
        => .2126 * DecodeSrgb((argb >> 16 & 255) / 255d)
         + .7152 * DecodeSrgb((argb >> 8 & 255) / 255d)
         + .0722 * DecodeSrgb((argb & 255) / 255d);

    static double DecodeSrgb(double value)
    {
        value = Math.Clamp(value, 0, 1);
        return value <= .04045 ? value / 12.92 : Math.Pow((value + .055) / 1.055, 2.4);
    }

    static double EncodeSrgb(double linear)
        => linear <= .0031308 ? 12.92 * linear : 1.055 * Math.Pow(linear, 1 / 2.4) - .055;
}

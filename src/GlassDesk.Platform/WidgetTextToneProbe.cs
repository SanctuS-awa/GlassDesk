using GlassDesk.Core;

namespace GlassDesk.Platform;

/// <summary>Capture-free light-material ink policy. No desktop pixel reads.</summary>
public static class WidgetTextToneProbe
{
    // Retain the public shape for callers; screen coordinates no longer drive ink.
    // The light material uses dark ink and promoted secondary text. Actual
    // composed contrast is unknown and must not be reported as a measurement.
    public static WidgetReadabilityDecision ReadPolicy(HostContext card, float textY, float tintOpacity,
        bool clockWhiteplate, bool previousLight, double primaryMinimumContrast = 4.5, bool hasRequiredSecondary = true)
        => new(false, hasRequiredSecondary, true, 0,
            "Capture-free light material: dark ink; composed contrast not measured.");

    public static bool TryRead(HostContext card, float textY, bool previousLight, out bool light)
    { light = previousLight; return false; }

    public static bool TryChooseLight(ReadOnlySpan<uint> colors, bool previousLight, out bool light)
    {
        light = previousLight;
        Span<double> luminance = stackalloc double[4];
        int count = 0;
        foreach (uint color in colors)
        {
            if (color == uint.MaxValue) continue;
            // COLORREF is 0x00BBGGRR, unlike the renderer's ARGB colors.
            double value = .2126 * Linear(color & 255) + .7152 * Linear((color >> 8) & 255) + .0722 * Linear((color >> 16) & 255);
            if (count < luminance.Length) luminance[count++] = value;
        }
        if (count == 0) return false;
        luminance[..count].Sort();
        double median = (luminance[(count - 1) / 2] + luminance[count / 2]) / 2;
        // Hysteresis keeps near-threshold backgrounds from flipping the ink.
        light = median < (previousLight ? .225 : .175);
        return true;
    }

    static double Linear(uint channel)
    {
        double value = channel / 255d;
        return value <= .04045 ? value / 12.92 : Math.Pow((value + .055) / 1.055, 2.4);
    }
}

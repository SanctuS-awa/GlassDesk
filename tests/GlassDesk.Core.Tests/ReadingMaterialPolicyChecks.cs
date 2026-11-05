using GlassDesk.Core;

internal static class ReadingMaterialPolicyChecks
{
    public static void Run(Action<bool,string> check)
    {
        var darkInk = ReadingMaterialPolicy.Resolve(lightInk: false, tintOpacity: .24f);
        var lightInk = ReadingMaterialPolicy.Resolve(lightInk: true, tintOpacity: .24f);
        check(darkInk.ColorArgb == 0xFFFFFFFFu && lightInk.ColorArgb == 0xFF000000u,
            "reading support polarity uses white behind dark ink and pure black behind light ink");
        double expectedMinimumChannel = 246 / 255d * (1 - ReadingMaterialPolicy.MaximumPrimaryAbsorption);
        check(darkInk.Opacity8Bit == 110 && lightInk.Opacity8Bit == 142
            && Math.Abs(ReadingMaterialPolicy.NativeTintMinimumChannel - expectedMinimumChannel) < 1e-12
            && darkInk.Opacity8Bit <= 142 && lightInk.Opacity8Bit <= 142,
            "default .24 tint budgets maximum primary absorption while white ink retains conservative pure-black support");

        bool bounded = true, polarityStable = true;
        double worstContrast = double.PositiveInfinity;
        for (int step = 0; step <= 1000; step++)
        {
            float tint = step / 1000f;
            var dark = ReadingMaterialPolicy.Resolve(false, tint);
            var light = ReadingMaterialPolicy.Resolve(true, tint);
            bounded &= dark.Opacity8Bit <= ReadingMaterialPolicy.MaximumOpacity8Bit
                && light.Opacity8Bit <= ReadingMaterialPolicy.MaximumOpacity8Bit
                && float.IsFinite(dark.Opacity) && float.IsFinite(light.Opacity);
            polarityStable &= dark.ColorArgb == 0xFFFFFFFFu && light.ColorArgb == 0xFF000000u;

            double darkContrast = ContrastAfterShells(dark, tint, lightInk: false);
            double lightContrast = ContrastAfterShells(light, tint, lightInk: true);
            worstContrast = Math.Min(worstContrast, Math.Min(darkContrast, lightContrast));
        }
        check(bounded && polarityStable && worstContrast >= ReadingMaterialPolicy.MinimumContrast,
            $"all 0..1 tint steps remain finite and at least 4.5:1 under conservative absorption bounds and 64 quantized shells (worst {worstContrast:F3}:1)");

        double largeWorst = double.PositiveInfinity;
        bool noStronger = true;
        for (int step = 0; step <= 1000; step++)
        foreach (bool light in new[] { false, true })
        {
            float tint = step / 1000f;
            var support = ReadingMaterialPolicy.ResolveLargeClock(light, tint);
            largeWorst = Math.Min(largeWorst, ContrastAfterShells(support, tint, light));
            noStronger &= support.Opacity8Bit <= ReadingMaterialPolicy.Resolve(light, tint).Opacity8Bit;
        }
        check(largeWorst >= 3 && noStronger,
            $"large-clock-only support keeps 3:1 with less coverage; small accompanying text retains ordinary policy (worst {largeWorst:F3}:1)");

        bool directMargin = true;
        foreach (float tint in new[] { 0f, .1f, .24f, .5f, .9f, 1f })
        {
            directMargin &= DirectContrast(ReadingMaterialPolicy.Resolve(false, tint), tint, false)
                >= ReadingMaterialPolicy.ContrastTarget;
            directMargin &= DirectContrast(ReadingMaterialPolicy.Resolve(true, tint), tint, true)
                >= ReadingMaterialPolicy.ContrastTarget;
        }
        check(directMargin,
            "byte-quantized solutions retain the 4.6:1 pre-shell target for both primary ink families");

        var conservative = ReadingMaterialPolicy.Resolve(false, 0);
        bool invalidSafe = ReadingMaterialPolicy.Resolve(false, float.NaN) == conservative
            && ReadingMaterialPolicy.Resolve(false, float.PositiveInfinity) == conservative
            && ReadingMaterialPolicy.Resolve(false, float.NegativeInfinity) == conservative
            && ReadingMaterialPolicy.Resolve(false, -1) == conservative
            && ReadingMaterialPolicy.Resolve(false, 2) == ReadingMaterialPolicy.Resolve(false, 1)
            && ReadingMaterialPolicy.Resolve(true, float.NaN) == ReadingMaterialPolicy.Resolve(true, 0)
            && float.IsFinite(ReadingMaterialPolicy.Resolve(false, float.NaN).Opacity)
            && float.IsFinite(ReadingMaterialPolicy.Resolve(true, float.NaN).Opacity);
        check(invalidSafe,
            "NaN and non-finite tint values use a conservative finite fallback; finite values clamp to endpoints");

        check(ReadingMaterialPolicy.Resolve(false, 1).Opacity8Bit == 0
            && Enumerable.Range(0, 101).Select(step => ReadingMaterialPolicy.Resolve(true, step / 100f).Opacity8Bit).Distinct().Count() == 1,
            "full white tint needs no extra dark-ink support and light-ink support stays conservative across tint");

    }

    static double ContrastAfterShells(ReadingMaterialSupport support, float tint, bool lightInk)
    {
        float accumulated = 0;
        for (int index = 1; index <= 64; index++)
        {
            float t = index / 64f;
            float desired = support.Opacity * t * t * (3 - 2 * t);
            int alpha = Math.Clamp((int)Math.Round(255 * (desired - accumulated) / (1 - accumulated)), 0, 255);
            if (alpha == 0) continue;
            accumulated += (1 - accumulated) * alpha / 255f;
        }

        double baseGray = lightInk ? 1 : Math.Clamp(tint, 0, 1) * ReadingMaterialPolicy.NativeTintMinimumChannel;
        double composite = lightInk ? baseGray * (1 - accumulated) : baseGray + (1 - baseGray) * accumulated;
        double inkLuminance = Luminance(TypographyCatalog.Ink(InkRole.Primary, darkMaterial: lightInk));
        return Ratio(DecodeSrgb(composite), inkLuminance);
    }

    static double DirectContrast(ReadingMaterialSupport support, float tint, bool lightInk)
    {
        double amount = support.Opacity;
        double baseGray = lightInk ? 1 : Math.Clamp(tint, 0, 1) * ReadingMaterialPolicy.NativeTintMinimumChannel;
        double composite = lightInk ? baseGray * (1 - amount) : baseGray + (1 - baseGray) * amount;
        double inkLuminance = Luminance(TypographyCatalog.Ink(InkRole.Primary, darkMaterial: lightInk));
        return Ratio(DecodeSrgb(composite), inkLuminance);
    }

    static double Luminance(uint argb)
        => .2126 * DecodeSrgb((argb >> 16 & 255) / 255d)
         + .7152 * DecodeSrgb((argb >> 8 & 255) / 255d)
         + .0722 * DecodeSrgb((argb & 255) / 255d);

    static double DecodeSrgb(double value)
        => value <= .04045 ? value / 12.92 : Math.Pow((value + .055) / 1.055, 2.4);

    static double Ratio(double first, double second)
        => (Math.Max(first, second) + .05) / (Math.Min(first, second) + .05);
}

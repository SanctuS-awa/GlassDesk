using GlassDesk.Core;

internal static class TransportButtonMotionChecks
{
    public static void Run(Action<bool,string> check)
    {
        var hover = new TransportButtonMotion();
        hover.Tick(0, true, false);
        check(hover.Tick(16, true, false) > 1,
            "transport hover enlarges the button instead of shrinking it");
        float hoverPeak = 1;
        for (int now = 32; now <= 480; now += 16)
        {
            hover.Tick(now, true, false);
            hoverPeak = Math.Max(hoverPeak, hover.Scale);
        }
        check(hoverPeak is > 1.035f and < 1.06f,
            "transport hover enlargement stays gentle while allowing a small spring settle");
        check(!hover.Active && Math.Abs(hover.Scale - TransportButtonMotion.HoverScale) < .0001f,
            "transport hover settles exactly without a continuing idle tick");

        float beforePress = hover.Scale;
        hover.Tick(496, true, true);
        check(hover.Scale == beforePress,
            "press retarget preserves the presented scale at the interruption point");
        check(hover.Tick(512, true, true) < beforePress,
            "press compresses the transport button");
        float beforeRelease = hover.Scale;
        hover.Tick(512, true, false);
        check(hover.Scale == beforeRelease,
            "release retarget also preserves the presented scale");
        float releasePeak = hover.Scale;
        for (int now = 528; now <= 960; now += 16)
        {
            hover.Tick(now, true, false);
            releasePeak = Math.Max(releasePeak, hover.Scale);
        }
        check(releasePeak > TransportButtonMotion.HoverScale,
            "release carries spring momentum into an elastic overshoot");
        check(!hover.Active && Math.Abs(hover.Scale - TransportButtonMotion.HoverScale) < .0001f,
            "elastic release returns to its hovered scale");

        var click = new TransportButtonMotion();
        click.Tick(0, false, false);
        click.TriggerClick(0);
        float minimum = click.Scale, maximum = click.Scale;
        for (int now = 16; now <= 480; now += 16)
        {
            click.Tick(now, false, false);
            minimum = Math.Min(minimum, click.Scale);
            maximum = Math.Max(maximum, click.Scale);
        }
        check(minimum < .98f && maximum > 1.001f,
            "keyboard activation adds a damped down-and-back spring response");
        check(!click.Active && click.Scale == TransportButtonMotion.RestScale,
            "activation spring settles exactly with no idle tick");

        var physical = new TransportButtonMotion();
        var physicalOnly = new TransportButtonMotion();
        foreach (var (now, pressed) in new[] { (0d, true), (64d, true), (80d, false) })
        {
            physical.Tick(now, true, pressed);
            physicalOnly.Tick(now, true, pressed);
        }
        physical.TriggerClick(80);
        bool samePhysicalReturn = true;
        for (int now = 96; now <= 420; now += 16)
        {
            float withPulse = physical.Tick(now, true, false);
            float withoutPulse = physicalOnly.Tick(now, true, false);
            samePhysicalReturn &= withPulse == withoutPulse && physical.Active == physicalOnly.Active;
        }
        check(samePhysicalReturn,
            "activation pulse does not add a second impulse after a sampled physical release");

        var unsampledTap = new TransportButtonMotion();
        unsampledTap.Tick(0, false, false);
        unsampledTap.TriggerClick(1);
        check(unsampledTap.Tick(17, false, false) < TransportButtonMotion.RestScale,
            "activation still responds when a fast tap was not sampled as pressed");

        var longIdle = new TransportButtonMotion();
        longIdle.Tick(0, true, false);
        longIdle.Tick(500, true, false);
        longIdle.Tick(5000, false, false);
        longIdle.TriggerClick(5000);
        float afterLongIdle = longIdle.Tick(5016, false, false);
        check(float.IsFinite(afterLongIdle) && afterLongIdle < 1.1f,
            "click after a long idle starts from a finite settled pose");

        var rapidClicks = new TransportButtonMotion();
        rapidClicks.Tick(0, false, false);
        for (int burst = 0; burst < 1000; burst++) rapidClicks.TriggerClick(0);
        bool boundedClicks = true;
        float clickMinimum = 1, clickMaximum = 1;
        for (int now = 1; now <= 10_000; now++)
        {
            rapidClicks.Tick(now, false, false);
            rapidClicks.TriggerClick(now);
            boundedClicks &= float.IsFinite(rapidClicks.Scale)
                && rapidClicks.Scale is > .8f and < 1.2f;
            clickMinimum = Math.Min(clickMinimum, rapidClicks.Scale);
            clickMaximum = Math.Max(clickMaximum, rapidClicks.Scale);
        }
        check(boundedClicks && clickMinimum > .85f && clickMaximum < 1.15f,
            "same-time bursts and sustained fast activations remain finite and bounded");

        var reduced = new TransportButtonMotion();
        reduced.Tick(0, true, false);
        reduced.Tick(16, true, false);
        reduced.Tick(32, true, true, reducedMotion: true);
        reduced.TriggerClick(32, reducedMotion: true);
        check(!reduced.Active && reduced.Scale == TransportButtonMotion.RestScale,
            "reduced motion suppresses hover, press and click scaling immediately");

        var reducedSettled = new TransportButtonMotion();
        reducedSettled.Tick(0, true, false);
        reducedSettled.Tick(500, true, false);
        reducedSettled.Tick(516, true, false, reducedMotion: true);
        check(!reducedSettled.Active && reducedSettled.Scale == TransportButtonMotion.RestScale,
            "reduced motion also clears an already-settled hover enlargement");

        var interrupted = new TransportButtonMotion();
        bool finite = true;
        for (int step = 0; step < 1000; step++)
        {
            double now = step * 13;
            bool hovered = step % 3 != 0;
            bool pressed = step % 11 < 3;
            interrupted.Tick(now, hovered, pressed);
            finite &= float.IsFinite(interrupted.Scale) && interrupted.Scale is > .8f and < 1.2f;
        }
        interrupted.Tick(14_000, false, false);
        check(finite && !interrupted.Active && interrupted.Scale == TransportButtonMotion.RestScale,
            "rapid pointer reversals remain bounded and finish at rest");
    }
}

using GlassDesk.Core;

internal static class TransportLensHandoffChecks
{
    public static void Run(Action<bool, string> check)
    {
        var handoff = new TransportLensHandoff();
        var frame = handoff.Tick(0, 1, 0, peekFits: true, detailsFits: false);
        check(frame.Owner == WidgetForm.Peek && frame.PeekOpacity == 1
            && frame.DetailsOpacity == 0 && !frame.Active,
            "lens handoff starts with the only visible transport row");

        frame = handoff.Tick(1, .3f, .4f, peekFits: true, detailsFits: false);
        check(frame.Owner == WidgetForm.Peek && frame.PeekOpacity > 0
            && frame.DetailsOpacity == 0 && !frame.Active,
            "dominant destination cannot take lens ownership before its scaled row fits");

        frame = handoff.Tick(2, .3f, .4f, peekFits: true, detailsFits: true);
        check(frame.Owner == WidgetForm.Peek && frame.PeekOpacity is > 0 and < .3f
            && frame.DetailsOpacity == 0 && frame.Active,
            "outgoing glyphs and lenses fade together before ownership switches");
        frame = handoff.Tick(92, .3f, .4f, peekFits: true, detailsFits: true);
        check(frame.Owner == WidgetForm.Details && frame.PeekOpacity == 0
            && frame.DetailsOpacity == 0 && frame.Active,
            "handoff switches only at zero outgoing opacity and starts the incoming row hidden");
        frame = handoff.Tick(137, .3f, .4f, peekFits: true, detailsFits: true);
        check(frame.Owner == WidgetForm.Details && frame.DetailsOpacity is > 0 and < .4f
            && frame.PeekOpacity == 0 && frame.Active,
            "incoming glyph and lens opacity rise together after the handoff");
        frame = handoff.Tick(227, .3f, .4f, peekFits: true, detailsFits: true);
        check(frame.Owner == WidgetForm.Details && frame.DetailsOpacity == .4f
            && frame.PeekOpacity == 0 && !frame.Active,
            "settled handoff retains one lens owner without idle work");

        var tiedFit = new TransportLensHandoff();
        tiedFit.Tick(0, 0, 0, peekFits: false, detailsFits: true);
        frame = tiedFit.Tick(1, .5f, .5f, peekFits: false, detailsFits: true);
        check(frame.Owner == WidgetForm.Details && frame.PeekOpacity == 0
            && frame.DetailsOpacity == 0 && frame.Active,
            "equal parent opacity selects the fitting row when the prior owner no longer fits");

        var reverse = new TransportLensHandoff();
        reverse.Tick(0, 1, 0, peekFits: true, detailsFits: true);
        frame = reverse.Tick(45, .4f, .6f, peekFits: true, detailsFits: true);
        check(frame.Owner == WidgetForm.Peek && frame.Active,
            "rapid reversal begins by fading the current owner");
        float factorBeforeReverse = reverse.OwnerFactor;
        frame = reverse.Tick(60, .7f, .3f, peekFits: true, detailsFits: true);
        check(frame.Owner == WidgetForm.Peek && reverse.OwnerFactor > factorBeforeReverse
            && Math.Abs((reverse.OwnerFactor - factorBeforeReverse) - 15f / 90f) < .0001f
            && frame.DetailsOpacity == 0 && frame.Active,
            "rapid reversal continues from the current opacity without a discontinuity");
        frame = reverse.Tick(150, .7f, .3f, peekFits: true, detailsFits: true);
        check(frame.Owner == WidgetForm.Peek && frame.PeekOpacity == .7f && !frame.Active,
            "reversed handoff settles at the current owner without a lingering tick");

        var cadence = new TransportLensHandoff();
        cadence.Tick(0, 1, 0, peekFits: true, detailsFits: true);
        cadence.Tick(0, .8f, 1, peekFits: true, detailsFits: true);
        double switchAt = -1, settledAt = -1;
        for (int tick = 1; tick <= 20; tick++)
        {
            double now = tick * 16;
            frame = cadence.Tick(now, .8f, 1, peekFits: true, detailsFits: true);
            if (frame.Owner == WidgetForm.Details && switchAt < 0) switchAt = now;
            if (switchAt >= 0 && !frame.Active && settledAt < 0) settledAt = now;
        }
        check(switchAt is >= 74 and <= 106 && settledAt - switchAt is >= 74 and <= 106
            && settledAt is > 0 and <= 210,
            "16 ms cadence fade endpoints are bounded near 90 ms each and 210 ms total");
        check(!frame.Active && !frame.Changed && frame.Owner == WidgetForm.Details
            && frame.DetailsOpacity == 1,
            "cadence handoff reaches an unchanged idle endpoint");

        frame = reverse.Tick(151, 0, 0, peekFits: true, detailsFits: true);
        check(frame.PeekOpacity == 0 && frame.DetailsOpacity == 0,
            "zero parent opacity clears both glyph groups and all lens opacity");
        frame = reverse.Tick(152, .2f, .8f, peekFits: true, detailsFits: true, reducedMotion: true);
        check(frame.Owner == WidgetForm.Details && frame.DetailsOpacity == .8f
            && frame.PeekOpacity == 0 && !frame.Active,
            "reduced motion snaps the handoff to its fitting dominant row");

        var invalid = new TransportLensHandoff();
        frame = invalid.Tick(double.NaN, float.NaN, float.PositiveInfinity,
            peekFits: true, detailsFits: true);
        check(float.IsFinite(frame.PeekOpacity) && float.IsFinite(frame.DetailsOpacity)
            && frame.PeekOpacity == 0 && frame.DetailsOpacity == 0,
            "non-finite clocks and parent opacities cannot poison handoff state");
        frame = invalid.Tick(double.PositiveInfinity, .5f, .5f, peekFits: false, detailsFits: false);
        check(float.IsFinite(frame.PeekOpacity) && float.IsFinite(frame.DetailsOpacity)
            && frame.PeekOpacity == 0 && frame.DetailsOpacity == 0,
            "repeated invalid input remains finite and inactive");

        var invalidDuringFade = new TransportLensHandoff();
        invalidDuringFade.Tick(0, 1, 0, peekFits: true, detailsFits: true);
        invalidDuringFade.Tick(10, .2f, .8f, peekFits: true, detailsFits: true);
        frame = invalidDuringFade.Tick(double.NaN, .2f, .8f, peekFits: true, detailsFits: true);
        check(frame.Owner == WidgetForm.Details && frame.DetailsOpacity == .8f
            && !frame.Active && float.IsFinite(frame.DetailsOpacity),
            "invalid clock settles an in-flight handoff instead of keeping an idle loop alive");

        bool scaledContainment = TransportLensGeometry.IsFullyContained(
                21.5f, 50, 42, 1, 44, 100)
            && !TransportLensGeometry.IsFullyContained(21.5f, 50, 42, 1.04f, 44, 100)
            && !TransportLensGeometry.IsFullyContained(180, 368, 54, 1.04f, 360, 395)
            && TransportLensGeometry.IsFullyContained(180, 368, 54, 1.04f, 360, 397)
            && !TransportLensGeometry.IsFullyContained(180, 368, 62, 1.04f, 360, 500)
            && !TransportLensGeometry.IsFullyContained(float.NaN, 1, 42, 1, 100, 100);
        check(scaledContainment,
            "viewport fit includes hover scale, native diameter cap, and finite geometry");

        bool peekRowFits = TransportLensGeometry.IsFullyContained(108, 146, 42, 1, 320, 176)
            && TransportLensGeometry.IsFullyContained(160, 146, 42, 1, 320, 176)
            && TransportLensGeometry.IsFullyContained(212, 146, 42, 1, 320, 176);
        bool detailsRowFitsAt371 = TransportLensGeometry.IsFullyContained(118, 368, 42, 1.04f, 360, 371)
            && TransportLensGeometry.IsFullyContained(180, 368, 54, 1.04f, 360, 371)
            && TransportLensGeometry.IsFullyContained(242, 368, 42, 1.04f, 360, 371);
        bool detailsRowFitsAt395 = TransportLensGeometry.IsFullyContained(118, 368, 42, 1.04f, 360, 395)
            && TransportLensGeometry.IsFullyContained(180, 368, 54, 1.04f, 360, 395)
            && TransportLensGeometry.IsFullyContained(242, 368, 42, 1.04f, 360, 395);
        bool detailsRowFitsAt397 = TransportLensGeometry.IsFullyContained(118, 368, 42, 1.04f, 360, 397)
            && TransportLensGeometry.IsFullyContained(180, 368, 54, 1.04f, 360, 397)
            && TransportLensGeometry.IsFullyContained(242, 368, 42, 1.04f, 360, 397);
        var presentation = new TransportLensHandoff();
        frame = presentation.Tick(0, 1, 0, peekFits: peekRowFits, detailsFits: detailsRowFitsAt371);
        check(frame.HasVisibleRow(WidgetForm.Peek)
            && TransportLensHandoffFrame.CanExposeTransportControls(WidgetForm.Peek,true,true,false,frame)
            && !TransportLensHandoffFrame.CanExposeTransportControls(WidgetForm.Details,true,true,false,frame)
            && !TransportLensHandoffFrame.CanExposeTransportControls(WidgetForm.Peek,true,true,true,frame),
            "submitted Peek row alone publishes its three transport targets");
        frame = presentation.Tick(65, 0, .78f, peekFits: peekRowFits, detailsFits: detailsRowFitsAt371);
        check(!detailsRowFitsAt371&&!frame.HasVisibleRow(WidgetForm.Peek)&&!frame.HasVisibleRow(WidgetForm.Details)
            &&!TransportLensHandoffFrame.CanExposeTransportControls(WidgetForm.Peek,true,true,false,frame)
            &&!TransportLensHandoffFrame.CanExposeTransportControls(WidgetForm.Details,true,true,false,frame),
            "at the fixed 371 DIP transition pose both hit rows stay unpublished while neither lens row is visible");
        frame = presentation.Tick(66, 0, .90f, peekFits: peekRowFits, detailsFits: detailsRowFitsAt395);
        check(!detailsRowFitsAt395&&!frame.HasVisibleRow(WidgetForm.Details),
            "the 395 DIP pose still fails the 56 DIP toggle lens at 1.04 hover scale");
        frame = presentation.Tick(67, 0, .91f, peekFits: peekRowFits, detailsFits: detailsRowFitsAt397);
        check(detailsRowFitsAt397&&frame.Owner==WidgetForm.Details&&!frame.HasVisibleRow(WidgetForm.Details)
            &&!TransportLensHandoffFrame.CanExposeTransportControls(WidgetForm.Details,true,true,false,frame),
            "a fitting Details row remains unpublished through the zero-opacity handoff frame");
        frame = presentation.Tick(68, 0, .91f, peekFits: peekRowFits, detailsFits: detailsRowFitsAt397);
        check(frame.HasVisibleRow(WidgetForm.Details)
            &&TransportLensHandoffFrame.CanExposeTransportControls(WidgetForm.Details,true,true,false,frame)
            &&!TransportLensHandoffFrame.CanExposeTransportControls(WidgetForm.Peek,true,true,false,frame),
            "Details transport targets appear only when the submitted incoming row has visible opacity");
        check(TransportLensHandoffFrame.CanExposeTransportControls(WidgetForm.Details,false,true,true,null)
            &&TransportLensHandoffFrame.CanExposeTransportControls(WidgetForm.Details,true,false,true,null),
            "non-system and system fallback input semantics do not depend on lens ownership");

        var idle = new TransportLensHandoff();
        idle.Tick(0, 1, 0, peekFits: true, detailsFits: false);
        idle.Tick(10, .2f, .8f, peekFits: true, detailsFits: true);
        frame = idle.Tick(100, .2f, .8f, peekFits: true, detailsFits: true);
        frame = idle.Tick(190, .2f, .8f, peekFits: true, detailsFits: true);
        bool settled = !frame.Active && frame.Owner == WidgetForm.Details
            && frame.DetailsOpacity == .8f;
        for (int tick = 1; tick <= 100; tick++)
        {
            frame = idle.Tick(190 + tick * 16, .2f, .8f, peekFits: true, detailsFits: true);
            settled &= !frame.Active && !frame.Changed
                && float.IsFinite(frame.PeekOpacity) && float.IsFinite(frame.DetailsOpacity);
        }
        check(settled, "settled handoff stays finite and does no ongoing idle work");

        var reset = new TransportLensHandoff();
        reset.Tick(0, 1, 0, peekFits: true, detailsFits: false);
        reset.Tick(10, .2f, .8f, peekFits: true, detailsFits: true);
        reset.Reset();
        frame = reset.Tick(20, 0, 1, peekFits: false, detailsFits: true);
        check(frame.Owner == WidgetForm.Details && frame.DetailsOpacity == 1
            && frame.PeekOpacity == 0 && !frame.Active,
            "reset forgets an interrupted handoff before glass is re-enabled");
    }
}

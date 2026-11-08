using GlassDesk.Core;

static class UiNavigationChecks
{
    public static void Run(Action<bool, string> check)
    {
        var model = new NavigationInteractionModel();
        int consumed = 0; long lastSequence = 0;
        model.IntroConsumed += sequence => { consumed++; lastSequence = sequence; };
        model.BeginOpen(1, 0, false, true);
        model.SetPointerInside(true, 0); model.Tick(99);
        check(model.State.OuterState == NavigationOuterState.HoverPending && model.Expansion == 0, "navigation waits the full 100 ms");
        model.Tick(100);
        check(model.State.OuterState == NavigationOuterState.Opening && model.State.IntroConsumed && consumed == 1, "navigation opens and consumes intro at 100 ms");
        model.Tick(320);
        check(model.State.OuterState == NavigationOuterState.Open && model.Expansion == 1, "navigation expansion settles after 220 ms");
        model.SetHoveredItem("clock", 320); model.Tick(500);
        check(model.State.HoveredItemId == "clock" && model.State.SelectedPreviewItemId == "home" && model.EmphasisIndex == 1,
            "hover moves shared emphasis without selecting a business page");
        check(model.SelectPreview("notes", 500) && model.State.SelectedPreviewItemId == "notes" && model.State.HoveredItemId == "clock",
            "click commits only the separate preview selection");
        check(!model.SelectPreview("unknown", 500) && model.State.SelectedPreviewItemId == "notes", "unknown preview IDs are rejected");
        model.SetPointerInside(false, 500); model.Tick(799);
        check(model.State.OuterState == NavigationOuterState.ClosePending && model.Expansion == 1, "navigation waits the full 300 ms before closing");
        model.Tick(800);
        check(model.State.OuterState == NavigationOuterState.Closing, "navigation starts closing at 300 ms");
        model.Tick(1020);
        check(model.Expansion == 0 && model.State.SelectedPreviewItemId == "notes" && consumed == 1, "collapsed navigation retains selection and never repeats intro");
        model.SetPointerInside(true, 1100); model.Tick(1420);
        check(consumed == 1 && model.Expansion == 1, "second hover in an open cycle leaves intro consumed");

        model.Close(); model.BeginOpen(2, 2000, false, true);
        model.SetPointerInside(true, 2000); model.SetPointerInside(false, 2050); model.Tick(2400);
        check(model.Expansion == 0 && !model.State.IntroConsumed && consumed == 1, "quick enter and leave cancels hover confirmation");
        model.SetPointerInside(true, 2500); model.Close(); model.BeginOpen(3, 2550, false, true); model.Tick(4000);
        check(model.Expansion == 0 && model.State.SelectedPreviewItemId == "home" && !model.State.IntroConsumed,
            "new cycle cancels all previous deadlines and resets preview selection");
        model.SetPointerInside(true, 4000); model.Tick(4320); model.SetPointerInside(false, 4400);
        model.SetPointerInside(true, 4500); model.Tick(5000);
        check(model.Expansion == 1 && consumed == 2 && lastSequence == 3, "reentry cancels close without repeating intro");
        model.SetPointerInside(false, 5100); model.Tick(5500);
        double interrupted = model.Expansion;
        model.SetPointerInside(true, 5500);
        bool continuous = model.Expansion == interrupted;
        model.Tick(5720);
        check(continuous && model.Expansion == 1 && model.State.OuterState == NavigationOuterState.Open,
            "reentry redirects from current shape and velocity, then settles without a new hover delay");

        model.BeginOpen(4, 6000, true, false); model.SetPointerInside(true, 6000); model.Tick(6100);
        check(model.Expansion == 1 && model.State.IntroConsumed && consumed == 2, "disabled companion leaves navigation fully usable without deferred intro");
        model.SetHoveredItem("focus", 6100);
        check(model.EmphasisIndex == 3 && model.ItemEmphasis[3] == 1 && model.State.SelectedPreviewItemId == "home",
            "reduced motion keeps separate visible hover and selection feedback");
        model.SetPointerInside(false, 6200); model.Tick(6500);
        check(model.Expansion == 0 && !model.NeedsTick, "reduced motion settles with no residual ticking");

        bool centered = true, hitGeometry = true;
        for (int selected = 0; selected < 5; selected++)
        {
            model.BeginOpen(10 + selected, 7000, false, true);
            model.OpenImmediately(7000); model.SelectPreview(PreviewNavigationItems.All[selected].Id, 7010); model.Tick(7500);
            centered &= Math.Abs(NavigationRibbonLayout.ItemOffset(selected, model.CenterIndex)) < .001;
            for (int i = 0; i < 5; i++) hitGeometry &= NavigationRibbonLayout.Hit(NavigationRibbonLayout.ItemOffset(i, model.CenterIndex), model.CenterIndex) == i;
        }
        check(centered && hitGeometry, "all five navigation selections settle at center with matching wrapped hit regions");
        model.BeginOpen(20, 8000, false, true); model.OpenImmediately(8000); model.Tick(8100);
        check(model.Expansion > 1 && NavigationRibbonLayout.Width(model.Expansion) < 288,
            "navigation has a bounded elastic overshoot inside the card");

        model.SelectPreview("notes", 8100); model.Tick(8130); model.Close();
        model.BeginOpen(21, 8140, false, true); model.Tick(8150);
        check(model.Expansion == 0 && model.CenterIndex == 0 && !model.NeedsTick,
            "reopening during spring motion clears both previous velocities");

        model.BeginOpen(22, 9000, false, true, keepExpanded: true); model.Tick(9220);
        check(model.Expansion == 1 && model.State.OuterState == NavigationOuterState.Open,
            "details navigation opens its complete companion body without a hover");
        model.SetPointerInside(true, 9230); model.SetHoveredItem("clock", 9230);
        model.SetPointerInside(false, 9250); model.Tick(11000);
        check(model.Expansion == 1 && model.State.OuterState == NavigationOuterState.Open &&
            model.State.HoveredItemId is null && !model.NeedsTick,
            "details companion navigation remains expanded after pointer exit and settles idle");
        model.Close(); model.BeginOpen(23, 12000, true, true, keepExpanded: true);
        check(model.Expansion == 1 && !model.NeedsTick,
            "reduced motion presents the complete details navigation immediately");

        for (int cycle = 0; cycle < 1000; cycle++)
        {
            double now = 10000 + cycle * 1000;
            model.BeginOpen(100 + cycle, now, cycle % 2 == 0, true);
            model.SetPointerInside(true, now); model.Tick(now + 100);
            model.SetHoveredItem(PreviewNavigationItems.All[cycle % 5].Id, now + 110);
            model.SelectPreview(PreviewNavigationItems.All[(cycle + 1) % 5].Id, now + 120);
            model.SetPointerInside(false, now + 160); model.Close(); model.Tick(now + 900);
            if (model.IsActive || model.NeedsTick || model.State.CaptureOwner != PointerCaptureOwner.None)
            { check(false, "1000 navigation lifecycle interleavings release deadlines and capture"); return; }
        }
        check(true, "1000 navigation lifecycle interleavings release deadlines and capture");
    }
}

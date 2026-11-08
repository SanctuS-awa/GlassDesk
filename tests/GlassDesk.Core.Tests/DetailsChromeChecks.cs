using System.Numerics;
using GlassDesk.Core;

/// <summary>Frozen Phase 2 chrome geometry and dock destination checks.</summary>
static class DetailsChromeChecks
{
    public static void Run(Action<bool, string> check)
    {
        var music = new Vector2(360, 476);
        var widgets = new Vector2(360, 512);
        var empty = new Vector2(360, 300);

        var home = DetailsChromeLayout.Home(music);
        check(DetailsChromeLayout.CardWidth == 360 && home == new FloatRect(151, 14, 58, 28)
            && DetailsChromeLayout.Home(empty) == home && DetailsChromeLayout.Home(widgets) == home,
            "home handle is a centred 58x28 handle at y=14 on every Details page");
        check(DetailsChromeLayout.HomeHit(empty) == new FloatRect(151, 6, 58, 44),
            "home handle hit target covers the full 58x28 visual and stays at least 44x44");

        check(DetailsChromeLayout.Dock(music) == new FloatRect(20, 412, 320, 48)
            && DetailsChromeLayout.Dock(widgets) == new FloatRect(20, 448, 320, 48)
            && DetailsChromeLayout.Dock(empty) == new FloatRect(20, 236, 320, 48),
            "dock is a centred 320x48 bar with a 16 DIP bottom inset on music/widgets/empty pages");

        bool centres = true, hits = true;
        for (int index = 0; index < 5; index++)
        {
            float localCentre = DetailsChromeLayout.DockItemCenter(music, index) - DetailsChromeLayout.Dock(music).X;
            centres &= Math.Abs(localCentre - (32 + 64 * index)) < .001f;
            var item = DetailsChromeLayout.DockItem(music, index);
            hits &= item.Width == 44 && item.Height == 48 && Math.Abs(item.X + 22 - DetailsChromeLayout.DockItemCenter(music, index)) < .001f;
        }
        check(centres, "five dock item centres are 32/96/160/224/288 inside the 320 DIP dock");
        check(hits, "every dock item owns a 44x48 pointer target centred on its slot");

        var active = DetailsChromeLayout.DockActive(music, 0);
        check(active == new FloatRect(34, 418, 36, 36)
            && DetailsChromeLayout.DockActive(widgets, 1) == new FloatRect(98, 454, 36, 36),
            "selection indicator is a 36x36 circle, radius 18 and vertically centred in the dock");

        check(DetailsDockItems.All.Count == 5
            && DetailsDockItems.All.Select(e => e.Id).SequenceEqual(new[] { "home", "widgets", "clock", "focus", "notes" })
            && DetailsDockItems.All.Select(e => e.Label).SequenceEqual(new[] { "首页", "小组件", "时钟", "专注", "随记" }),
            "dock declares home/widgets/clock/focus/notes in order");
        check(DetailsDockItems.All.Select(e => e.ActionId).SequenceEqual(new[] { 120, 121, 122, 123, 124 })
            && DetailsDockItems.All.Select(e => e.IsPage).SequenceEqual(new[] { true, true, false, false, false }),
            "only home and widgets are real pages; clock/focus/notes are preview destinations");
        check(WidgetActions.Body == 0 && WidgetActions.PluginTools == 8 && WidgetActions.NavigationShell == 99
            && WidgetActions.NavigationFirst == 100 && WidgetActions.CompanionReveal == 110
            && WidgetActions.DockFirst == 120,
            "dock action IDs append at 120 without renumbering any existing action");
        check(DetailsDockItems.IndexOf("music") == 0 && DetailsDockItems.IndexOf("widgets") == 1
            && DetailsDockItems.IndexOf("clock") == 0 && DetailsDockItems.IndexOf(null) == 0,
            "dock selection index derives from the real page ID and falls back to music");

        var label = DetailsChromeLayout.DockLabel(music, 0, 56);
        check(label.Height == 20 && label.Y == DetailsChromeLayout.Dock(music).Y - 24 && label.X >= 4
            && DetailsChromeLayout.HomeLabel(music, 56).Y == DetailsChromeLayout.Home(music).Bottom + 2,
            "dock nameplates sit 4 DIP above the dock and the home nameplate below the handle");

        var controller = new WidgetController(new(), new("qa", new(0, 0, 1920, 1080), new(0, 0, 1920, 1040), 1, true), new(960, 24));
        controller.UpdateContent(CapsuleContentMode.Music, true, true, 256, 0, timeline: true);
        controller.ChangeForm(WidgetForm.Details, 1);
        bool timelineDoesNotGrow = controller.Layout.Width == 360 && controller.Layout.Height == 476;
        controller.UpdateContent(CapsuleContentMode.Music, true, true, 256, 2, timeline: false);
        timelineDoesNotGrow &= controller.Layout.Height == 476;
        controller.SetDetailsPageHeight(512, 3);
        bool pageHeightOwns = controller.Layout.Height == 512;
        controller.SetDetailsPageHeight(null, 4);
        bool musicRestored = controller.Layout.Height == 476;
        check(timelineDoesNotGrow && pageHeightOwns && musicRestored,
            "music Details height is fixed at 476 and only a real page height (512) may override it");
    }
}

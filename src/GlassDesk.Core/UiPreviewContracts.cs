using System.Numerics;
namespace GlassDesk.Core;

public static class CompanionAppearanceIds
{
    public const string Original="glassdesk.orb.original", CatEar="glassdesk.orb.cat-ear", Devil="glassdesk.orb.devil";
    public static IReadOnlyList<string> All { get; } = Array.AsReadOnly(new[] { Original, CatEar, Devil });
    public static string Normalize(string? value)=>value is CatEar or Devil?value:Original;
}

public enum PointerCaptureOwner { None, WidgetDrag, Timeline, CompanionReveal, Navigation, SettingsNavigation, WidgetBoard }
public enum CompanionPlacementMode { Front, NavigationIntro, CoverPeek, Revealing }
public enum CompanionShapePose { Rounded, Stretch, Squash, Droplet, Peek }
public enum NavigationOuterState { Collapsed, HoverPending, Opening, Open, ClosePending, Closing }
public readonly record struct NavigationState(long OpenSequence,NavigationOuterState OuterState,string? HoveredItemId,
    string SelectedPreviewItemId,bool IntroConsumed,PointerCaptureOwner CaptureOwner=PointerCaptureOwner.None);
public sealed record NavigationEntry(string Id,string Label,int ActionId);
public static class PreviewNavigationItems
{
    public static IReadOnlyList<NavigationEntry> All {get;}=Array.AsReadOnly(new NavigationEntry[]
    {
        new NavigationEntry("home","首页",100),new("clock","时钟",101),new("companion","陪伴",102),
        new("focus","专注",103),new("notes","随记",104)
    });
}
public readonly record struct CompanionPose(Vector2 BodyAnchor,FloatRect FullVisualBounds,CompanionPlacementMode PlacementMode,
    CompanionShapePose ShapePose,string? OccluderId=null);
public sealed record HideAnchor(string StableId,FloatRect OccluderBounds,float OccluderRadius,Vector2 BodyAnchor,
    FloatRect VisibleEyeRegion,FloatRect RevealHitRegion);

/// <summary>One bottom-dock destination. Only <see cref="IsPage"/> entries switch the real page.</summary>
public sealed record DetailsDockEntry(string Id, string Label, int ActionId, bool IsPage);

/// <summary>The five dock destinations in left-to-right order (home, widgets, clock, focus, notes).</summary>
public static class DetailsDockItems
{
    public const int Home = 0, Music = Home, Widgets = 1, Clock = 2, Focus = 3, Notes = 4;
    public static IReadOnlyList<DetailsDockEntry> All { get; } = Array.AsReadOnly(new[]
    {
        new DetailsDockEntry("home", "首页", WidgetActions.DockFirst + Home, true),
        new DetailsDockEntry("widgets", "小组件", WidgetActions.DockFirst + Widgets, true),
        new DetailsDockEntry("clock", "时钟", WidgetActions.DockFirst + Clock, false),
        new DetailsDockEntry("focus", "专注", WidgetActions.DockFirst + Focus, false),
        new DetailsDockEntry("notes", "随记", WidgetActions.DockFirst + Notes, false)
    });

    /// <summary>Dock slot for a real page ID; unknown IDs fall back to home.</summary>
    public static int IndexOf(string? pageId)
    {
        string normalized = DetailsPageController.Normalize(pageId);
        for (int index = 0; index < All.Count; index++)
            if (All[index].Id == normalized) return index;
        return Home;
    }
}

public readonly record struct FloatRect(float X,float Y,float Width,float Height)
{
    public float Right=>X+Width;public float Bottom=>Y+Height;
    public bool Contains(float x,float y)=>x>=X&&y>=Y&&x<Right&&y<Bottom;
    public bool Intersects(FloatRect other)=>X<other.Right&&Right>other.X&&Y<other.Bottom&&Bottom>other.Y;
    public FloatRect Inflate(float amount)=>new(X-amount,Y-amount,Width+2*amount,Height+2*amount);
}

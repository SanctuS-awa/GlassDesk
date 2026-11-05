using GlassDesk.Core;

namespace GlassDesk.App;

internal enum HomeActionKind { App, ToggleNav, NavDestination, Folder, Preview, OpenFolder, ClosePopover }
internal readonly record struct HomeAction(HomeActionKind Kind, int Index);

public sealed partial class WidgetScene
{
    static readonly string[] HomeAppNames = ["网易云音乐", "Edge"];
    static readonly string[] HomeAppGlyphs = ["\uE8D6", "\uE774"];
    static readonly uint[] HomeAppColors = [0xFFE11D48, 0xFF0284C7];
    static readonly string[] HomeFolderLabels = ["桌面"];
    readonly Dictionary<int, HomeAction> _homeActions = new();
    HomeVisual? _homeVisual;
    bool _homeNavExpanded, _homeHoverNav;
    int _homeHoverFolder = -1, _homeHoverApp = -1;
    int _homePopoverFolder = -1, _homePressedAction = -1;
    HomeFolderPreview? _homePreview;
    float _homeScroll;
    internal bool HomeNavExpanded => _homeNavExpanded;
    internal bool HomePopoverOpen => _homePopoverFolder >= 0;
    internal int HomePopoverFolder => _homePopoverFolder;
    internal HomeFolderPreview? HomeFolderPreview => _homePreview;
    internal IReadOnlyDictionary<int, HomeAction> HomeActions => _homeActions;
    HomePresentationLayout HomeGeometry => HomePresentationLayout.Resolve(CurrentSize.X, CurrentSize.Y, _homeScroll);

    void InitializeHomeVisual()
    {
        _homeVisual = new(_host);
        _bodyRoot.Children.InsertAtTop(_homeVisual.Visual);
    }
    internal void SetHomeAppIcon(int index, byte[] bytes)
    { if (_disposed) return; _homeVisual?.SetAppIcon(index, bytes); UpdateHomeVisual(); }
    void UpdateHomeVisual()
    {
        if (_homeVisual is null) return;
        var layout = HomeGeometry; _homeScroll = layout.Scroll;
        bool high = _accessibility.HighContrast;
        _homeVisual.Present(layout, HomePage && _form == WidgetForm.Details && _sceneVisible,
            _homeNavExpanded, _homeHoverNav, _homeHoverApp, _homeHoverFolder,
            _homePopoverFolder, _homePreview, _homePressedAction,
            _preferences.ReducedMotion || !_uiSettings.AnimationsEnabled, high,
            high ? _text : TypographyCatalog.Ink(InkRole.Primary),
            high ? _button : 0xFFF5F7FA,
            high ? _text : TypographyCatalog.Ink(InkRole.Secondary),
            _host.Context.Dpi, HomeAppNames, HomeAppGlyphs, HomeAppColors, HomeFolderLabels, _notice);
    }

    // Existing coordinator tick hook is deliberately inert: Home motion runs on the compositor.
    internal bool TickHome(double now) => false;

    internal void SetHomeNavExpanded(bool expanded)
    {
        if (_homeNavExpanded == expanded) return;
        _homeNavExpanded = expanded; UpdateHomeVisual(); BuildControls();
    }
    internal bool UpdateHomePointer(float x, float y)
    {
        if (!HomePage) return false;
        var layout = HomeGeometry;
        bool nav = !HomePopoverOpen && layout.NavTrigger.Contains(x, y);
        int folder = -1, app = -1;
        if (layout.DockFolder.Contains(x, y)) folder = 0;
        if (!HomePopoverOpen)
        {
            for (int i = 0; i < HomeAppNames.Length; i++)
                if (layout.DockAppSlot(i).Contains(x, y)) app = i;
        }
        if (nav == _homeHoverNav && folder == _homeHoverFolder && app == _homeHoverApp) return false;
        _homeHoverNav = nav; _homeHoverFolder = folder; _homeHoverApp = app;
        UpdateHomeVisual(); BuildControls(); return true;
    }
    internal bool ClearHomeHover()
    {
        if (!_homeHoverNav && _homeHoverFolder < 0 && _homeHoverApp < 0) return false;
        _homeHoverNav = false; _homeHoverFolder = _homeHoverApp = -1;
        UpdateHomeVisual(); BuildControls(); return true;
    }
    internal void ScrollHome(float delta)
    {
        // Home is a single Dock surface now; it has no scrollable app grid.
    }
    internal void ResetHomeState()
    {
        _homeActions.Clear();
        _homeNavExpanded = _homeHoverNav = false;
        _homeHoverFolder = _homeHoverApp = _homePopoverFolder = _homePressedAction = -1;
        _homePreview = null; UpdateHomeVisual();
    }
    internal void OpenHomePopover(int index, HomeFolderPreview preview)
    {
        _homePopoverFolder = index; _homePreview = preview;
        _homeHoverNav = false; _homeHoverApp = -1; _homePressedAction = -1;
        UpdateHomeVisual(); BuildControls();
    }
    internal void CloseHomePopover()
    {
        if (_homePopoverFolder < 0) return;
        _homePopoverFolder = -1; _homePressedAction = -1;
        // The presenter retains the exit snapshot; logical actions disappear immediately.
        UpdateHomeVisual(); BuildControls();
    }
    internal void SetHomePointerFeedback(int actionId)
    {
        if (_homePressedAction == actionId) return;
        _homePressedAction = actionId; UpdateHomeVisual();
    }
    void DrawHome(List<GlassDesk.NativeBridge.SurfaceOp> operations) => UpdateHomeVisual();

    void AddHomeControls(List<AccessibleControl> controls)
    {
        _homeActions.Clear(); var layout = HomeGeometry;
        void Add(int id, string name, FloatRect rect, HomeAction action, bool enabled = true)
        {
            controls.Add(new AccessibleControl(id, name, rect.X, rect.Y, rect.Width, rect.Height, enabled));
            _homeActions[id] = action;
        }
        if (HomePopoverOpen && _homePreview is not null)
        {
            // This backdrop consumes the first outside-popup press before an app can launch.
            Add(WidgetActions.HomeClosePopover, "关闭文件夹预览", new(0, 54, layout.Width, layout.Height - 54),
                new(HomeActionKind.ClosePopover, 0));
            for (int i = 0; i < HomeFolderLabels.Length; i++)
                Add(WidgetActions.HomeFolderFirst + i, HomeFolderLabels[i] + " 文件夹",
                    layout.DockFolder, new(HomeActionKind.Folder, 0));
            // Whitespace is a pointer-only guard in HitTest, not an invokable UIA button.
            if (_homePreview.Status == HomeFolderStatus.Ready)
                for (int i = 0; i < Math.Min(3, _homePreview.Entries.Count); i++)
                    Add(WidgetActions.HomePreviewFirst + i, _homePreview.Entries[i].Name,
                        layout.Preview(i), new(HomeActionKind.Preview, i));
            Add(WidgetActions.HomeOpenFolder, "打开文件夹", layout.PopoverOpenButton, new(HomeActionKind.OpenFolder, 0),
                !string.IsNullOrWhiteSpace(_homePreview.Path));
            return;
        }
        for (int i = 0; i < HomeAppNames.Length; i++)
            Add(WidgetActions.HomeAppFirst + i, "启动 " + HomeAppNames[i], layout.DockAppSlot(i), new(HomeActionKind.App, i));
        Add(WidgetActions.HomeNavToggle, _homeNavExpanded ? "收起导航" : "展开导航", layout.NavTrigger,
            new(HomeActionKind.ToggleNav, 0));
        if (_homeNavExpanded)
        {
            string[] names = ["音乐", "本地音乐", "歌单", "收藏", "最近播放"];
            for (int i = 0; i < names.Length; i++)
                Add(WidgetActions.HomeNavFirst + i, names[i], layout.NavRailItem(i),
                    new(HomeActionKind.NavDestination, i), enabled: i == 0);
        }
        Add(WidgetActions.HomeFolderFirst, HomeFolderLabels[0] + " 文件夹", layout.DockFolder,
            new(HomeActionKind.Folder, 0));
    }
}

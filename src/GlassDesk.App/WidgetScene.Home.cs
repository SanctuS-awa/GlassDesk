using GlassDesk.Core;
using GlassDesk.NativeBridge;

namespace GlassDesk.App;

internal enum HomeActionKind { App, ToggleNav, NavDestination, Folder, Preview, OpenFolder, ClosePopover, Media, Mute, CalendarMonth }
internal readonly record struct HomeAction(HomeActionKind Kind, int Index);

public sealed partial class WidgetScene
{
    static readonly string[] HomeAppNames = ["网易云音乐", "Edge"];
    static readonly string[] HomeAppGlyphs = ["\uE8D6", "\uE774"];
    static readonly uint[] HomeAppColors = [0xFFE11D48u, 0xFF0284C7u];
    static readonly string[] HomeFolderLabels = ["桌面"];
    readonly Dictionary<int, HomeAction> _homeActions = new();
    HomeVisual? _homeVisual;
    bool _homeNavExpanded, _homeHoverNav;
    int _homeHoverFolder = -1, _homeHoverApp = -1;
    int _homePopoverFolder = -1, _homePressedAction = -1, _homeHoverAction = -1;
    HomeFolderPreview? _homePreview;
    float _homeScroll, _homeScrollX;
    DateTime _homeCalendarMonth;
    HomeSystemStatusSnapshot _homeSystemStatus = HomeSystemStatusSnapshot.Unknown;
    bool _homeVolumeDragging;
    string? _homeVolumeNotice;
    internal bool HomeNavExpanded => _homeNavExpanded;
    internal bool HomePopoverOpen => _homePopoverFolder >= 0;
    internal int HomePopoverFolder => _homePopoverFolder;
    internal HomeFolderPreview? HomeFolderPreview => _homePreview;
    internal bool HomeFolderPreviewAvailable => HomeGeometry.CanShowFolderPopover;
    internal IReadOnlyDictionary<int, HomeAction> HomeActions => _homeActions;
    HomePresentationLayout HomeGeometry => HomePresentationLayout.Resolve(CurrentSize.X, CurrentSize.Y, _homeScrollX, _homeScroll);
    internal FloatRect HomeVolumeTrack
        => HomeGeometry.VolumeTrack;

    internal GlassShape[] HomeGlassScene(ulong sequence)
    {
        if (sequence == 0) throw new ArgumentOutOfRangeException(nameof(sequence));
        var result = new List<GlassShape>(32);
        var identifiers = new HashSet<uint>();
        var homeIdentifiers = new HashSet<uint>();
        bool peekVisible = _media.Connected && _contentWeights.Y > .001f && _sceneVisible
            && !_solidMaterial && !_accessibility.HighContrast;

        bool IsValid(GlassShape shape)
        {
            float right = shape.X + shape.Width, bottom = shape.Y + shape.Height;
            return shape.Id != 0 && (shape.Layer == 2u || shape.Layer == 3u)
                && float.IsFinite(_presentedSize.X) && float.IsFinite(_presentedSize.Y)
                && _presentedSize.X > 0 && _presentedSize.Y > 0
                && float.IsFinite(shape.X) && float.IsFinite(shape.Y)
                && float.IsFinite(shape.Width) && float.IsFinite(shape.Height)
                && float.IsFinite(right) && float.IsFinite(bottom)
                && float.IsFinite(shape.Radius) && float.IsFinite(shape.Materialize)
                && float.IsFinite(shape.Press) && shape.Width > 0 && shape.Height > 0
                && shape.X >= 0 && shape.Y >= 0 && right <= _presentedSize.X && bottom <= _presentedSize.Y;
        }

        bool Add(GlassShape shape)
        {
            if (!IsValid(shape) || result.Count >= 32 || !identifiers.Add(shape.Id)) return false;

            result.Add(shape with
            {
                Radius = Math.Clamp(shape.Radius, 0, Math.Min(shape.Width, shape.Height) / 2),
                Materialize = Math.Clamp(shape.Materialize, 0, 1),
                Press = Math.Clamp(shape.Press, 0, 1)
            });
            return true;
        }

        if (HomePage && _homeVisual is not null && _contentWeights.Z > .001f)
        {
            float homeWeight = Math.Clamp(_contentWeights.Z, 0, 1);
            foreach (var shape in _homeVisual.GlassShapes)
            {
                var weighted = shape with { Materialize = Math.Clamp(shape.Materialize, 0, 1) * homeWeight };
                if (Add(weighted)) homeIdentifiers.Add(shape.Id);
            }
        }

        void MakeRoomForPeekButton()
        {
            if (result.Count < 32) return;
            int removeAt = -1;
            for (int index = result.Count - 1; index >= 0; index--)
                if (homeIdentifiers.Contains(result[index].Id) && result[index].Layer == 3u)
                { removeAt = index; break; }
            if (removeAt < 0)
                for (int index = result.Count - 1; index >= 0; index--)
                    if (homeIdentifiers.Contains(result[index].Id)) { removeAt = index; break; }
            if (removeAt < 0) return;
            homeIdentifiers.Remove(result[removeAt].Id);
            identifiers.Remove(result[removeAt].Id);
            result.RemoveAt(removeAt);
        }

        if (peekVisible)
        {
            for (int index = 0; index < 3; index++)
            {
                if (!_buttons.TryGetValue((WidgetForm.Peek, WidgetActions.Previous + index), out var button)) continue;
                float original = button.Visual.Size.X;
                float scale = _ownedControlMotion[index].Scale;
                float diameter = Math.Min(62, Math.Max(0, original - 2)) * scale;
                float centerX = _peekButtons.Offset.X + button.Visual.Offset.X + original / 2;
                float centerY = _peekButtons.Offset.Y + button.Visual.Offset.Y + original / 2;
                var shape = new GlassShape(200u + (uint)index, 0, 3, 0,
                    centerX - diameter / 2, centerY - diameter / 2, diameter, diameter, diameter / 2,
                    Math.Clamp(_contentWeights.Y, 0, 1), Math.Clamp(_ownedControlActivity[index], 0, 1), true);

                if (IsValid(shape) && !identifiers.Contains(shape.Id)) MakeRoomForPeekButton();
                bool shapeVisible = Add(shape);
                bool nativeGlassOwned = _host.IsLiquidGlassCapture && OwnedGlassEnabled && shapeVisible;
                button.SetOwnedGlass(nativeGlassOwned);
                if (nativeGlassOwned)
                {
                    // The shape and the visible button use the same sampled scale
                    // in this frame. Stop the legacy track so it cannot drift from
                    // the native lens. Other backends keep their own button motion.
                    button.Visual.StopAnimation("Scale");
                    button.Visual.CenterPoint = new(original / 2, original / 2, 0);
                    button.Visual.Scale = new(scale, scale, 1);
                }
            }

            var pose = _artworkPose;
            var cover = new GlassShape(210u, 0, 2, 0, pose.X - 2, pose.Y - 2,
                pose.Width + 4, pose.Height + 4,
                Math.Min(pose.Radius + 2, Math.Min(pose.Width + 4, pose.Height + 4) / 2),
                Math.Clamp(_artworkViewport.Opacity, 0, 1), 0, true);
            float clipTop = _artworkViewportClip.TopInset;
            float clipBottom = _presentedSize.Y - _artworkViewportClip.BottomInset;
            bool coverWithinClip = float.IsFinite(cover.X) && float.IsFinite(cover.Y)
                && float.IsFinite(cover.Width) && float.IsFinite(cover.Height)
                && cover.Y >= clipTop && cover.Y + cover.Height <= clipBottom;
            if (coverWithinClip) Add(cover);
        }

        foreach (var pair in _buttons)
        {
            if (pair.Key.Form != WidgetForm.Peek) continue;
            int index = pair.Key.Action - WidgetActions.Previous;
            if (!peekVisible || index is < 0 or > 2 || !identifiers.Contains(200u + (uint)index))
                pair.Value.SetOwnedGlass(false);
        }

        return result.ToArray();
    }

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
        // Keep the outgoing Details layout while its foreground fades into
        // the closing shell; resolving a Capsule layout here would jump it.
        if (HomePage && _form != WidgetForm.Details && (_contentWeights.Z > .001f
            || _detailsBodyOpacity.Sample(_motionNow).Position > .001)) return;
        var layout = HomeGeometry; _homeScroll = layout.ScrollY; _homeScrollX = layout.ScrollX;
        if (_homeCalendarMonth == default) _homeCalendarMonth = _now.Date;
        bool high = _accessibility.HighContrast;
        float progress = 0;
        _homeVisual.Present(layout, HomePage && (_form == WidgetForm.Details || _contentWeights.Z > .001f
            || _detailsBodyOpacity.Sample(_motionNow).Position > .001) && _sceneVisible,
            _homeNavExpanded, _homeHoverNav, _homeHoverApp, _homeHoverFolder,
            _homePopoverFolder, _homePreview, _homePressedAction, _homeHoverAction,
            _preferences.ReducedMotion || !_uiSettings.AnimationsEnabled, high,
            high ? _text : TypographyCatalog.Ink(InkRole.Primary),
            high ? _button : 0xFFF5F7FAu,
            high ? _text : TypographyCatalog.Ink(InkRole.Secondary),
            _accent, _host.Context.Dpi, HomeAppNames, HomeAppGlyphs, HomeAppColors, HomeFolderLabels,
            _media, _now, progress, _notice, _homeCalendarMonth, _homeSystemStatus,
            _homeVolumeDragging, _homeVolumeNotice);
    }

    internal void RefreshHomeDashboardProgress() { }

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
        int action = -1;
        if (layout.DockFolder.Contains(x, y)) folder = 0;
        if (!HomePopoverOpen)
        {
            for (int i = 0; i < HomeAppNames.Length; i++)
                if (layout.DockAppSlot(i).Contains(x, y)) app = i;
        }
        if (!HomePopoverOpen)
        {
            foreach (var item in new[]
            {
                (WidgetActions.HomeCalendarPrevious, layout.CalendarPrevious),
                (WidgetActions.HomeCalendarNext, layout.CalendarNext),
                (WidgetActions.HomeMediaPrevious, layout.MediaPrevious),
                (WidgetActions.HomeMediaToggle, layout.MediaToggle),
                (WidgetActions.HomeMediaNext, layout.MediaNext),
                (WidgetActions.HomeMediaOpen, layout.MediaOpenMusic)
            })
            {
                if (layout.VisibleContentTarget(item.Item2)?.Contains(x, y) == true) { action = item.Item1; break; }
            }
            if (layout.VisibleOverlayTarget(layout.Volume)?.Contains(x, y) == true) action = WidgetActions.HomeVolume;
        }
        if (nav == _homeHoverNav && folder == _homeHoverFolder && app == _homeHoverApp && action == _homeHoverAction) return false;
        _homeHoverNav = nav; _homeHoverFolder = folder; _homeHoverApp = app; _homeHoverAction = action;
        UpdateHomeVisual(); BuildControls(); return true;
    }
    internal bool ClearHomeHover()
    {
        if (!_homeHoverNav && _homeHoverFolder < 0 && _homeHoverApp < 0 && _homeHoverAction < 0) return false;
        _homeHoverNav = false; _homeHoverFolder = _homeHoverApp = _homeHoverAction = -1;
        UpdateHomeVisual(); BuildControls(); return true;
    }
    internal void ScrollHome(float delta)
    {
        if (!float.IsFinite(delta) || Math.Abs(delta) < .01f) return;
        var layout = HomeGeometry;
        float remaining = delta;
        float nextX = layout.ScrollX;
        if (layout.MaxScrollX > 0)
        {
            nextX = Math.Clamp(nextX + remaining, 0, layout.MaxScrollX);
            remaining -= nextX - layout.ScrollX;
        }
        float nextY = layout.ScrollY;
        if (Math.Abs(remaining) > .01f && layout.MaxScrollY > 0)
            nextY = Math.Clamp(nextY + remaining, 0, layout.MaxScrollY);
        if (Math.Abs(nextX - _homeScrollX) < .1f && Math.Abs(nextY - _homeScroll) < .1f) return;
        _homeScrollX = nextX; _homeScroll = nextY;
        UpdateHomeVisual(); BuildControls();
    }

    internal void UpdateHomeSystemStatus(HomeSystemStatusSnapshot snapshot, bool volumeDragging, string? volumeNotice)
    {
        if (_disposed) return;
        if (_homeSystemStatus == snapshot && _homeVolumeDragging == volumeDragging
            && string.Equals(_homeVolumeNotice, volumeNotice, StringComparison.Ordinal)) return;
        _homeSystemStatus = snapshot;
        _homeVolumeDragging = volumeDragging;
        _homeVolumeNotice = volumeNotice;
        UpdateHomeVisual(); BuildControls();
    }

    internal void ChangeHomeCalendarMonth(int direction)
    {
        if (direction is not (-1 or 1)) return;
        if (_homeCalendarMonth == default) _homeCalendarMonth = _now.Date;
        try
        {
            _homeCalendarMonth = new DateTime(_homeCalendarMonth.Year, _homeCalendarMonth.Month, 1).AddMonths(direction);
        }
        catch (ArgumentOutOfRangeException) { return; }
        UpdateHomeVisual(); BuildControls();
    }
    internal void ResetHomeState()
    {
        _homeActions.Clear();
        _homeNavExpanded = _homeHoverNav = false;
        _homeHoverFolder = _homeHoverApp = _homeHoverAction = _homePopoverFolder = _homePressedAction = -1;
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

    void AddHomeControls(List<AccessibleControl> controls, MediaSnapshot media)
    {
        _homeActions.Clear(); var layout = HomeGeometry;
        void Add(int id, string name, FloatRect rect, HomeAction action, bool enabled = true)
        {
            controls.Add(new AccessibleControl(id, name, rect.X, rect.Y, rect.Width, rect.Height, enabled));
            _homeActions[id] = action;
        }
        if (HomePopoverOpen && _homePreview is not null && layout.CanShowFolderPopover)
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
            if (layout.VisibleOverlayTarget(layout.DockAppSlot(i)) is { } appTarget)
                Add(WidgetActions.HomeAppFirst + i, "启动 " + HomeAppNames[i], appTarget, new(HomeActionKind.App, i));
        if (layout.VisibleOverlayTarget(layout.NavTrigger) is { } navTarget)
            Add(WidgetActions.HomeNavToggle, _homeNavExpanded ? "收起导航" : "展开导航", navTarget,
                new(HomeActionKind.ToggleNav, 0));
        if (_homeNavExpanded)
        {
            string[] names = ["首页", "本地音乐", "歌单", "收藏", "最近播放"];
            for (int i = 0; i < names.Length; i++)
                if (layout.VisibleOverlayTarget(layout.NavRailItem(i)) is { } navItem)
                    Add(WidgetActions.HomeNavFirst + i, names[i], navItem,
                        new(HomeActionKind.NavDestination, i), enabled: i == 0);
        }
        if (layout.VisibleOverlayTarget(layout.DockFolder) is { } folderTarget)
            Add(WidgetActions.HomeFolderFirst, HomeFolderLabels[0] + " 文件夹", folderTarget,
                new(HomeActionKind.Folder, 0));

        if (layout.VisibleContentTarget(layout.CalendarPrevious) is { } previousMonth)
            Add(WidgetActions.HomeCalendarPrevious, "上一个月", previousMonth, new(HomeActionKind.CalendarMonth, -1));
        if (layout.VisibleContentTarget(layout.CalendarNext) is { } nextMonth)
            Add(WidgetActions.HomeCalendarNext, "下一个月", nextMonth, new(HomeActionKind.CalendarMonth, 1));

        if (layout.VisibleOverlayTarget(layout.Volume) is { } volume)
            controls.Add(new AccessibleControl(WidgetActions.HomeVolume,
                !_homeSystemStatus.AudioAvailable ? "系统音量，没有可用的输出设备"
                    : !_homeSystemStatus.HasValidVolume ? "系统音量，状态不可用"
                    : _homeSystemStatus.Muted == true ? "系统音量，已静音" : "系统音量",
                volume.X, volume.Y, volume.Width, volume.Height,
                _homeSystemStatus.AudioAvailable && _homeSystemStatus.HasValidVolume,
                IsRange: true, Value: _homeSystemStatus.HasValidVolume && _homeSystemStatus.VolumeScalar is float scalar
                    ? Math.Clamp(scalar * 100, 0, 100) : 0, Minimum: 0, Maximum: 100));
        if (layout.MediaActionsVisible && media.Connected)
        {
            if (layout.VisibleContentTarget(layout.MediaPrevious) is { } previous)
                Add(WidgetActions.HomeMediaPrevious, "上一首", previous,
                    new(HomeActionKind.Media, 0), media.Capabilities.Previous);
            if (layout.VisibleContentTarget(layout.MediaToggle) is { } toggle)
                Add(WidgetActions.HomeMediaToggle, media.State == PlaybackState.Playing ? "暂停" : "播放",
                    toggle, new(HomeActionKind.Media, 1), media.Capabilities.Toggle);
            if (layout.VisibleContentTarget(layout.MediaNext) is { } next)
                Add(WidgetActions.HomeMediaNext, "下一首", next,
                    new(HomeActionKind.Media, 2), media.Capabilities.Next);
        }
        else if (layout.MediaActionsVisible && layout.VisibleContentTarget(layout.MediaOpenMusic) is { } open)
            Add(WidgetActions.HomeMediaOpen, "打开网易云音乐", open, new(HomeActionKind.Media, 3));
    }
}

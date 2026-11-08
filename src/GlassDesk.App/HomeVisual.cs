using System.Globalization;
using System.Numerics;
using System.Runtime.CompilerServices;
using GlassDesk.Core;
using GlassDesk.NativeBridge;
using Windows.UI;
using Windows.UI.Composition;

namespace GlassDesk.App;

/// <summary>Persistent horizontal Home dashboard, Dock, and folder surfaces.</summary>
internal sealed class HomeVisual : IDisposable
{
    readonly NativeWidgetHost _host;
    readonly Compositor _compositor;
    readonly CompositionSpring _spring;
    public bool MotionActive => _visible && (_spring.Active || _dockPlate.MotionActive || _dockNavPlate.MotionActive || _navigationPlate.MotionActive);
    readonly List<IDisposable> _owned = [];
    readonly List<ContainerVisual> _containers = [];
    readonly List<ShapeVisual> _shapeVisuals = [];
    readonly List<Visual> _animated = [];
    readonly Dictionary<(Visual, string), Vector3> _vectorTargets = [];
    readonly Dictionary<int, byte[]> _appIcons = [];
    GlassShape[] _glassShapes = [];
    int _iconRevision;
    readonly Dictionary<CompositionPathGeometry, CompositionPath> _paths = [];
    readonly ContainerVisual _popup;
    readonly ControlDepthVisual _dockPlate, _dockNavPlate, _navigationPlate;
    readonly Layer _dashboard, _dashboardStatus, _dockTriggerIcon, _navigationIcons, _help, _popupInk, _notice;
    readonly Layer _ethernet;
    readonly ShapeVisual _wifi;
    readonly CompositionColorBrush[] _wifiInk = new CompositionColorBrush[3];
    readonly ContainerVisual _dashboardViewport;
    readonly ContainerVisual _mediaToggleRoot;
    readonly PlaybackGlyphVisual _mediaToggleGlyph;
    readonly InsetClip _dashboardClip;
    readonly Layer[] _appArt = new Layer[2];
    readonly Folder[] _folders = new Folder[1];
    readonly CompositionPathGeometry _popupGeometry;
    readonly ShapeVisual _popupFace;
    readonly CompositionSpriteShape _popupShape;
    readonly CompositionLinearGradientBrush _popupFill, _popupRim;
    HomePresentationLayout? _layout;
    bool _visible, _popupOpen, _reduced, _disposed;
    bool _highContrast;
    uint _foreground, _background;
    string? _helpKey;
    HomePopupRenderKey? _popupKey;
    public ContainerVisual Visual { get; }
    public IReadOnlyList<GlassShape> GlassShapes => _glassShapes;

    T Own<T>(T value) where T : IDisposable { _owned.Add(value); return value; }
    ContainerVisual Container()
    {
        var value = Own(_compositor.CreateContainerVisual()); _containers.Add(value); return value;
    }
    ShapeVisual Shape()
    {
        var value = Own(_compositor.CreateShapeVisual()); _shapeVisuals.Add(value); return value;
    }
    CompositionColorBrush Ink(uint color) => Own(_compositor.CreateColorBrush(ColorOf(color)));
    CompositionLinearGradientBrush Gradient(uint top, uint bottom)
    {
        var brush = Own(_compositor.CreateLinearGradientBrush());
        brush.MappingMode = CompositionMappingMode.Relative;
        brush.StartPoint = Vector2.Zero; brush.EndPoint = new(0, 1);
        brush.ColorStops.Add(Own(_compositor.CreateColorGradientStop(0, ColorOf(top))));
        brush.ColorStops.Add(Own(_compositor.CreateColorGradientStop(1, ColorOf(bottom))));
        return brush;
    }
    static Color ColorOf(uint argb) => Color.FromArgb((byte)(argb >> 24), (byte)(argb >> 16), (byte)(argb >> 8), (byte)argb);
    static void Colors(CompositionLinearGradientBrush brush, uint top, uint bottom)
    { brush.ColorStops[0].Color = ColorOf(top); brush.ColorStops[1].Color = ColorOf(bottom); }
    void Path(CompositionPathGeometry geometry, Vector2[] points)
    {
        var clean = new List<Vector2>();
        foreach (var point in points)
            if (clean.Count == 0 || Vector2.DistanceSquared(clean[^1], point) > .00001f) clean.Add(point);
        if (clean.Count > 2 && Vector2.DistanceSquared(clean[0], clean[^1]) < .00001f) clean.RemoveAt(clean.Count - 1);
        var path = _host.CreatePolygonPath(clean.ToArray(), 0);
        geometry.Path = path;
        _paths[geometry] = path;
    }
    ShapeVisual Rounded(Vector2 size, float radius, CompositionBrush brush)
    {
        var visual = Shape(); visual.Size = size;
        var geometry = Own(_compositor.CreateRoundedRectangleGeometry()); geometry.Size = size; geometry.CornerRadius = new(radius);
        var shape = Own(_compositor.CreateSpriteShape(geometry)); shape.FillBrush = brush; visual.Shapes.Add(shape);
        return visual;
    }

    public HomeVisual(NativeWidgetHost host)
    {
        _host = host; _compositor = host.Compositor; _spring = new(_compositor);
        Visual = Container();
        _dashboardViewport = Container(); Visual.Children.InsertAtTop(_dashboardViewport);
        _dashboardClip = Own(_compositor.CreateInsetClip()); _dashboardViewport.Clip = _dashboardClip;
        _dashboard = new(host, _dashboardViewport);
        _mediaToggleRoot = Container(); _dashboardViewport.Children.InsertAtTop(_mediaToggleRoot);
        _mediaToggleGlyph = new(host); _mediaToggleRoot.Children.InsertAtTop(_mediaToggleGlyph.Visual);
        _dashboardStatus = new(host, Visual);
        _wifi = Shape(); _wifi.Size = new(18, 12); Visual.Children.InsertAtTop(_wifi);
        float[][] bands = [[0f, 3.4f], [5.4f, 7.7f], [9.6f, 11.8f]];
        for (int i = 0; i < 3; i++)
        {
            var geometry = Own(_compositor.CreatePathGeometry());
            Path(geometry, WifiBand(bands[i][0], bands[i][1]));
            _wifiInk[i] = Ink(0xFF1C1C1Eu);
            var sprite = Own(_compositor.CreateSpriteShape(geometry)); sprite.FillBrush = _wifiInk[i];
            _wifi.Shapes.Add(sprite);
        }
        _ethernet = new(host, Visual);
        _ethernet.Visual.IsVisible = false;
        _dockPlate = Own(new ControlDepthVisual(_compositor)); Visual.Children.InsertAtTop(_dockPlate.Visual);
        _dockNavPlate = Own(new ControlDepthVisual(_compositor)); Visual.Children.InsertAtTop(_dockNavPlate.Visual);
        _dockTriggerIcon = new(host, Visual);
        _navigationPlate = Own(new ControlDepthVisual(_compositor)); Visual.Children.InsertAtTop(_navigationPlate.Visual);
        _navigationIcons = new(host, Visual);
        for (int i = 0; i < _appArt.Length; i++) _appArt[i] = new(host, Visual);
        for (int i = 0; i < _folders.Length; i++)
        {
            _folders[i] = new(this); Visual.Children.InsertAtTop(_folders[i].Visual);
        }
        _help = new(host, Visual);
        _notice = new(host, Visual);

        _popup = Container(); Visual.Children.InsertAtTop(_popup);
        _popupFace = Shape(); _popup.Children.InsertAtTop(_popupFace);
        _popupGeometry = Own(_compositor.CreatePathGeometry());
        _popupShape = Own(_compositor.CreateSpriteShape(_popupGeometry));
        _popupFill = Gradient(0xFAFFFFFFu, 0xF5EEF2F6u); _popupRim = Gradient(0xE6FFFFFFu, 0x403F5368u);
        _popupShape.FillBrush = _popupFill; _popupShape.StrokeBrush = _popupRim; _popupShape.StrokeThickness = .8f;
        _popupFace.Shapes.Add(_popupShape); _popupInk = new(host, _popup);
        _popup.Opacity = 0; Visual.Opacity = 0;
    }

    public void Present(HomePresentationLayout layout, bool visible, bool active, bool hoveredNav, int hoveredApp, int hoveredFolder,
        int selectedFolder, HomeFolderPreview? preview, int pressed, int hoveredAction, bool reduced, bool highContrast,
        uint foreground, uint background, uint secondary, uint accent, uint dpi, IReadOnlyList<string> appNames,
        IReadOnlyList<string> appGlyphs, IReadOnlyList<uint> appColors, IReadOnlyList<string> folderNames,
        MediaSnapshot media, DateTimeOffset now, float progress, string? notice, DateTime calendarMonth,
        HomeSystemStatusSnapshot systemStatus, bool volumeDragging, string? volumeNotice)
    {
        bool initial = !_visible || _layout is null;
        bool geometryChanged = _layout?.Width != layout.Width || _layout?.Height != layout.Height;
        bool paletteChanged = _highContrast != highContrast || _foreground != foreground || _background != background;
        _highContrast = highContrast; _foreground = foreground; _background = background;
        bool wasVisible = _visible; _visible = visible; _reduced = reduced;
        Visual.IsVisible = visible;
        if (!visible)
        {
            if (wasVisible) StopMotion();
            _mediaToggleRoot.IsVisible = false;
            _mediaToggleGlyph.SetVisible(false);
            _wifi.IsVisible = false; _ethernet.Visual.IsVisible = false;
            _popupOpen = false; _popup.Opacity = 0; _popup.IsVisible = false;
            Visual.Opacity = 0; _glassShapes = []; return;
        }
        Visual.Opacity = 1;
        bool immediate = initial || reduced || geometryChanged;
        _layout = layout;
        Visual.Size = new(layout.Width, layout.Height);
        _dashboardViewport.Size = new(layout.Width, layout.Height);
        var contentClip = layout.ContentViewport;
        _dashboardClip.LeftInset = contentClip.X;
        _dashboardClip.TopInset = contentClip.Y;
        _dashboardClip.RightInset = Math.Max(0, layout.Width - contentClip.Right);
        _dashboardClip.BottomInset = Math.Max(0, layout.Height - contentClip.Bottom);
        _dashboard.Visual.Offset = new(-layout.ScrollX, -layout.ScrollY, 0);
        bool lightInk = IsLightInk(foreground);
        int canvasHover = hoveredAction is WidgetActions.HomeCalendarPrevious or WidgetActions.HomeCalendarNext
            or WidgetActions.HomeMediaPrevious or WidgetActions.HomeMediaToggle or WidgetActions.HomeMediaNext
            ? hoveredAction : -1;
        int statusHover = hoveredAction == WidgetActions.HomeVolume ? hoveredAction : -1;
        var dashboardKey = DashboardKey(layout, media, now, calendarMonth, progress, foreground, secondary, accent,
            highContrast, lightInk, pressed, canvasHover, dpi);
        _dashboard.SetCached(dashboardKey, new(layout.CanvasWidth, layout.CanvasHeight),
            DashboardOperations(layout, media, now, calendarMonth, progress, pressed, canvasHover, highContrast, lightInk,
                foreground, secondary, accent));
        var toggle = layout.PresentedContent(layout.MediaToggle);
        _mediaToggleRoot.Offset = new(toggle.X, toggle.Y, 0);
        _mediaToggleRoot.Size = new(toggle.Width, toggle.Height);
        bool toggleVisible = media.Connected && layout.VisibleContentTarget(layout.MediaToggle) is not null;
        _mediaToggleRoot.IsVisible = toggleVisible;
        _mediaToggleGlyph.SetVisible(toggleVisible);
        uint toggleInk = media.Capabilities.Toggle ? foreground : (foreground & 0x00FFFFFFu) | 0x59000000u;
        _mediaToggleGlyph.SetOpticalGlass(_host.IsLiquidGlassCapture && !highContrast);
        _mediaToggleGlyph.Update(media.State == PlaybackState.Playing, toggleInk, toggle.Width, 20, reduced);
        _dashboardStatus.SetCached($"{layout.Width}/{layout.Height}/{now:yyyyMMddHHmm}/{foreground}/{secondary}/{background}/{highContrast}/{lightInk}/{systemStatus}/{volumeDragging}/{volumeNotice}/{pressed}/{statusHover}/{dpi}",
            new(layout.Width, layout.Height), StatusOperations(layout, now, systemStatus, volumeDragging,
                volumeNotice, pressed, statusHover, highContrast, lightInk, foreground, secondary));
        bool wifi = string.Equals(systemStatus.NetworkKind, "Wifi", StringComparison.OrdinalIgnoreCase);
        bool ethernet = string.Equals(systemStatus.NetworkKind, "Ethernet", StringComparison.OrdinalIgnoreCase);
        bool hasBattery = systemStatus.HasBattery != false;
        var wifiRect = hasBattery ? layout.StatusWifi : layout.StatusWifiNoBattery;
        _wifi.Offset = new(wifiRect.X, wifiRect.Y, 0);
        _wifi.IsVisible = !ethernet;
        int level = wifi && systemStatus.SignalBars0To5 is byte signal && signal <= 5
            ? (int)Math.Ceiling(signal * 3d / 5d) : 0;
        uint inkOn = highContrast ? foreground : lightInk ? 0xFFFFFFFFu : 0xFF1C1C1Eu;
        uint inkOff = highContrast ? secondary : (inkOn & 0x00FFFFFFu) | 0x4D000000u;
        for (int i = 0; i < 3; i++) _wifiInk[i].Color = ColorOf(i < level ? inkOn : inkOff);
        _ethernet.Visual.IsVisible = ethernet;
        if (ethernet)
        {
            _ethernet.SetCached($"eth/{inkOn}/{dpi}", new(18, 12), [Glyph("\uE839", 2, -1, 14, inkOn)]);
            _ethernet.Visual.Offset = new(wifiRect.X, wifiRect.Y, 0);
        }
        _dockPlate.SetBounds(layout.DockPill.X, layout.DockPill.Y, layout.DockPill.Width, layout.DockPill.Height, layout.DockPill.Height / 2);
        _dockPlate.Visual.IsVisible = highContrast;
        _dockPlate.SetAppearance(highContrast ? background | 0xFF000000u : 0x00000000u, foreground, highContrast);
        bool dockHover = hoveredFolder >= 0 || hoveredApp >= 0;
        bool dockPressed = pressed == WidgetActions.HomeFolderFirst
            || pressed >= WidgetActions.HomeAppFirst && pressed < WidgetActions.HomeAppFirst + WidgetActions.HomeAppCount;
        _dockPlate.SetFeedback(highContrast && dockHover, highContrast && dockPressed, reduced);
        var navTrigger = layout.NavTrigger;
        _dockNavPlate.SetBounds(navTrigger.X, navTrigger.Y, navTrigger.Width, navTrigger.Height, navTrigger.Width / 2);
        _dockNavPlate.Visual.IsVisible = highContrast;
        _dockNavPlate.SetAppearance(highContrast ? background | 0xFF000000u : 0x00000000u, foreground, highContrast);
        _dockNavPlate.SetFeedback(highContrast && (hoveredNav || active),
            highContrast && pressed == WidgetActions.HomeNavToggle, reduced);
        string triggerGlyph = active ? "\uE711" : "\uE700";
        float triggerSize = Math.Min(navTrigger.Width, navTrigger.Height);
        float triggerGlyphSize = Math.Min(18, triggerSize * .42f);
        _dockTriggerIcon.SetCached($"{triggerGlyph}/{foreground}/{dpi}/{triggerSize}", new(triggerSize),
            [Glyph(triggerGlyph, (triggerSize - triggerGlyphSize) / 2, (triggerSize - triggerGlyphSize) / 2,
                triggerGlyphSize, foreground)]);
        _dockTriggerIcon.Visual.Offset = new(navTrigger.X, navTrigger.Y, 0);
        _dockTriggerIcon.Visual.Size = new(triggerSize);
        _dockTriggerIcon.Visual.CenterPoint = new(triggerSize / 2, triggerSize / 2, 0);
        Scale(_dockTriggerIcon.Visual, new(pressed == WidgetActions.HomeNavToggle ? .96f
            : hoveredNav || active ? 1.03f : 1), immediate, pressed == WidgetActions.HomeNavToggle ? 90 : 160);
        var rail = layout.NavRail;
        _navigationPlate.SetBounds(rail.X, rail.Y, rail.Width, rail.Height, rail.Width / 2);
        _navigationPlate.SetAppearance(highContrast ? background | 0xFF000000u : 0x00000000u, foreground, highContrast);
        _navigationPlate.Visual.IsVisible = highContrast && active && rail.Height > 0;
        _navigationIcons.Visual.IsVisible = active && rail.Height > 0;
        if (active && rail.Height > 0)
        {
            string[] glyphs = ["\uE80F", "\uE8D6", "\uE8FD", "\uE734", "\uE823"];
            var icons = new List<SurfaceOp>();
            for (int i = 0; i < glyphs.Length; i++)
            {
                var item = layout.NavRailItem(i);
                float x = item.X - rail.X, y = item.Y - rail.Y;
                if (i == 0) icons.Add(Round(x, y, item.Width, item.Height, item.Width / 2,
                    highContrast ? background : 0x99242B35u));
                float glyphSize = Math.Min(20, item.Width * .55f);
                icons.Add(Glyph(glyphs[i], x + (item.Width - glyphSize) / 2,
                    y + (item.Height - glyphSize) / 2, glyphSize,
                    highContrast ? foreground : i == 0 ? 0xFFFFFFFFu : 0xAAE5EAF1u));
            }
            _navigationIcons.SetCached($"{rail.Width}/{rail.Height}/{foreground}/{background}/{highContrast}/{dpi}",
                new(rail.Width, rail.Height), icons);
            _navigationIcons.Visual.Offset = new(rail.X, rail.Y, 0);
        }
        for (int i = 0; i < appNames.Count; i++)
        {
            var icon = layout.DockAppIcon(i); bool down = pressed == WidgetActions.HomeAppFirst + i;
            var ink = new List<SurfaceOp>();
            if (!highContrast && _appIcons.TryGetValue(i, out var artwork))
                ink.Add(new SurfaceOp { Kind = SurfaceOpKind.Image, Width = 32, Height = 32, Radius = 9, Image = artwork });
            else
            {
                ink.Add(Round(0, 0, 32, 32, 9, highContrast ? background : appColors[i]));
                ink.Add(Glyph(appGlyphs[i], 5, 5, 22, highContrast ? foreground : 0xFFFFFFFFu));
            }
            _appArt[i].SetCached($"{i}/{_iconRevision}/{foreground}/{background}/{highContrast}/{dpi}", new(32), ink);
            _appArt[i].Visual.CenterPoint = new(16, 16, 0);
            float lift = down ? .8f : hoveredApp == i && !reduced ? -2 : 0;
            Move(_appArt[i].Visual, new(icon.X, icon.Y + lift, 0), immediate, down ? 90 : 160);
            float scale = down ? .97f : hoveredApp == i && !reduced ? 1.03f : 1;
            Scale(_appArt[i].Visual, new(scale, scale, 1), immediate, down ? 90 : 160);
        }
        for (int i = 0; i < folderNames.Count; i++)
            _folders[i].Set(layout.DockFolder, hoveredFolder == i || selectedFolder == i,
                pressed == WidgetActions.HomeFolderFirst + i, immediate, highContrast, foreground, background);
        string helpName = selectedFolder >= 0 ? "" : hoveredFolder == 0 ? folderNames[0]
            : hoveredApp >= 0 && hoveredApp < appNames.Count ? appNames[hoveredApp]
            : hoveredNav ? "导航" : "";
        string helpKey = $"{helpName}/{secondary}/{dpi}/{foreground}/{background}/{highContrast}";
        if (_helpKey != helpKey)
        {
            float width = helpName == "网易云音乐" ? 104 : helpName == "导航" ? 64 : 72;
            _help.Set(new(width, 22),
                [Round(0, 0, width, 22, 9, highContrast ? background : 0xF02A3441u),
                 SurfaceOp.Typography(helpName, TextRole.Caption, 4, 3, width - 8, 16,
                    highContrast ? foreground : 0xFFF8FAFCu, true)]);
            _helpKey = helpKey;
        }
        if (helpName.Length > 0)
        {
            float center = hoveredFolder == 0 ? layout.DockFolder.X + 22
                : hoveredApp >= 0 ? layout.DockAppIcon(hoveredApp).X + 22
                : navTrigger.X + navTrigger.Width / 2;
            float width = helpName == "网易云音乐" ? 104 : helpName == "导航" ? 64 : 72;
            _help.Visual.Offset = new(Math.Clamp(center - width / 2, 8, layout.Width - width - 8), layout.Dock.Y - 28, 0);
        }
        Scalar(_help.Visual, "Opacity", helpName.Length > 0 ? 1 : 0, immediate, 120);
        bool hasNotice = !string.IsNullOrWhiteSpace(notice) && selectedFolder < 0;
        _notice.Visual.Opacity = hasNotice ? 1 : 0;
        if (hasNotice)
        {
            float available = Math.Max(0, Math.Min(layout.Width - 16, layout.Volume.X - 16));
            float width = Math.Min(220, available);
            if (width < 40)
            {
                _notice.Visual.Opacity = 0;
                PresentPopup(layout, selectedFolder, preview, pressed, secondary, dpi, paletteChanged);
                _glassShapes = BuildGlassShapes(layout, media, systemStatus, volumeDragging, active,
                    selectedFolder, preview, hoveredNav, hoveredApp, hoveredFolder, hoveredAction, pressed);
                return;
            }
            float x = Math.Max(8, layout.Volume.X - width - 8);
            float y = Math.Max(0, layout.Dock.Y - 28);
            _notice.Visual.Offset = new(x, y, 0);
            _notice.SetCached($"{notice}/{width}/{foreground}/{background}/{highContrast}/{dpi}", new(width, 24),
                [Round(0, 0, width, 24, 10, highContrast ? background : 0xD923272Eu),
                (Glyph("\uE7BA", 7, 5, 14, highContrast ? foreground : 0xFFFFCB69u)),
                SurfaceOp.Typography(notice!, TextRole.Caption, 26, 4, width - 32, 16, highContrast ? foreground : 0xFFF8FAFCu)]);
        }
        PresentPopup(layout, selectedFolder, preview, pressed, secondary, dpi, paletteChanged);
        _glassShapes = BuildGlassShapes(layout, media, systemStatus, volumeDragging, active,
            selectedFolder, preview, hoveredNav, hoveredApp, hoveredFolder, hoveredAction, pressed);
    }

    static string DashboardKey(HomePresentationLayout layout, MediaSnapshot media, DateTimeOffset now, DateTime calendarMonth,
        float progress, uint foreground, uint secondary, uint accent, bool highContrast, bool lightInk, int pressed, int hoveredAction, uint dpi)
    {
        _ = progress;
        return $"{layout.CanvasWidth}/{layout.CanvasHeight}/{layout.ScrollX}/{layout.ScrollY}/{calendarMonth:yyyyMM}/{now:yyyyMMddHHmm}/{CultureInfo.CurrentCulture.DateTimeFormat.ShortTimePattern}/{media.SessionToken}/{media.ContentVersion}/{media.State}/{media.Connected}/{media.Title}/{media.Artist}/{media.Album}/{(media.Cover is null ? 0 : RuntimeHelpers.GetHashCode(media.Cover))}/{media.Capabilities}/{foreground}/{secondary}/{accent}/{highContrast}/{lightInk}/{pressed}/{hoveredAction}/{dpi}";
    }

    static bool IsLightInk(uint color)
    {
        double red = ((color >> 16) & 255) / 255d, green = ((color >> 8) & 255) / 255d, blue = (color & 255) / 255d;
        return .2126 * red + .7152 * green + .0722 * blue > .52;
    }

    List<SurfaceOp> DashboardOperations(HomePresentationLayout layout, MediaSnapshot media, DateTimeOffset now,
        DateTime calendarMonth, float progress, int pressed, int hoveredAction,
        bool highContrast, bool lightInk, uint foreground, uint secondary, uint accent)
    {
        _ = progress;
        uint quiet = highContrast ? foreground : secondary;
        uint weatherInk = highContrast ? foreground : 0xFFFFFFFFu;
        uint weatherQuiet = highContrast ? foreground : 0xE6FFFFFFu;
        var operations = new List<SurfaceOp>(100);
        void Text(string value, TextRole role, float x, float y, float width, float height, uint color, bool center = false)
        {
            if (!float.IsFinite(x) || !float.IsFinite(y) || !float.IsFinite(width) || !float.IsFinite(height) || width <= 0 || height <= 0) return;
            operations.Add(SurfaceOp.Typography(value, role, x, y, width, height, color, center, locale: "zh-CN"));
        }
        void Card(FloatRect rect) { _ = rect; } // Layer2 shape submission belongs to NativeWidgetHost.SetGlassScene, not a painted Round.

        var weather = layout.Weather;
        Card(weather);
        Text("天气", TextRole.Label, weather.X + 14, weather.Y + 12, weather.Width - 28, 20, weatherInk);
        Ellipse(operations, weather.X + weather.Width / 2 - 25, weather.Y + 66, 20, 20, weatherQuiet);
        Ellipse(operations, weather.X + weather.Width / 2 - 8, weather.Y + 58, 27, 28, weatherQuiet);
        Ellipse(operations, weather.X + weather.Width / 2 + 13, weather.Y + 68, 18, 18, weatherQuiet);
        operations.Add(Round(weather.X + weather.Width / 2 - 24, weather.Y + 73, 50, 17, 8, weatherQuiet));
        Text("—°", TextRole.WidgetValue, weather.X + 16, weather.Y + 101, weather.Width - 32, 48, weatherInk, true);
        Text("暂未接入天气", TextRole.Caption, weather.X + 14, weather.Bottom - 54, weather.Width - 28, 20, weatherInk);
        Text("稍后提供实时天气", TextRole.Caption, weather.X + 14, weather.Bottom - 31, weather.Width - 28, 16, weatherQuiet);
        Text(now.ToString("M月d日", CultureInfo.GetCultureInfo("zh-CN")), TextRole.Caption,
            layout.ClockDate.X, layout.ClockDate.Y, layout.ClockDate.Width, 18, weatherQuiet);
        Text(now.ToString("dddd", CultureInfo.GetCultureInfo("zh-CN")), TextRole.Caption,
            layout.ClockDate.X, layout.ClockDate.Y + 20, layout.ClockDate.Width, 18, weatherQuiet);

        var calendar = layout.Calendar;
        Card(calendar);
        var prev = layout.CalendarPrevious; var next = layout.CalendarNext;
        bool currentMonth = calendarMonth.Year == now.Year && calendarMonth.Month == now.Month;
        uint red = lightInk ? 0xFFFF453Au : 0xFFFF3B30u;
        float titleX = calendar.X + 16;
        float titleW = prev.X - titleX - 2;
        Text(calendarMonth.ToString("yyyy年M月", CultureInfo.GetCultureInfo("zh-CN")),
            TextRole.SectionTitle, titleX, calendar.Y + 14, titleW, 24,
            currentMonth && !highContrast ? red : foreground);
        if (currentMonth)
            Text($"{now.ToString("dddd", CultureInfo.GetCultureInfo("zh-CN"))} · 农历{LunarFull(now.Date)}",
                TextRole.Caption, calendar.X + 16, calendar.Y + 40, calendar.Width - 32, 16, quiet);
        operations.Add(Glyph("\uE76B", prev.X + (prev.Width - 12) / 2, prev.Y + (prev.Height - 12) / 2, 12,
            hoveredAction == WidgetActions.HomeCalendarPrevious ? foreground : quiet));
        operations.Add(Glyph("\uE76C", next.X + (next.Width - 12) / 2, next.Y + (next.Height - 12) / 2, 12,
            hoveredAction == WidgetActions.HomeCalendarNext ? foreground : quiet));
        string[] weekdays = ["一", "二", "三", "四", "五", "六", "日"];
        float side = layout.Standard ? 16 : 14;
        float innerX = calendar.X + side, innerWidth = calendar.Width - side * 2;
        float cellWidth = innerWidth / 7;
        float weekY = calendar.Y + (layout.Standard ? 62 : 58);
        uint weekdayInk = highContrast ? foreground : (quiet & 0x00FFFFFFu) | 0x99000000u;
        for (int column = 0; column < 7; column++)
            Text(weekdays[column], TextRole.Caption, innerX + column * cellWidth, weekY,
                cellWidth, 16, weekdayInk, true);
        var monthStart = new DateTime(calendarMonth.Year, calendarMonth.Month, 1);
        int offset = ((int)monthStart.DayOfWeek + 6) % 7;
        int days = DateTime.DaysInMonth(calendarMonth.Year, calendarMonth.Month);
        int rows = (offset + days + 6) / 7;
        float gridTop = calendar.Y + (layout.Standard ? 84 : 80);
        float available = calendar.Bottom - 12 - gridTop;
        float rowHeight = Math.Min(layout.Standard ? 32 : 30, available / rows);
        for (int day = 1; day <= days; day++)
        {
            int index = offset + day - 1;
            int column = index % 7, row = index / 7;
            float x = innerX + column * cellWidth, y = gridTop + row * rowHeight;
            bool today = currentMonth && day == now.Day;
            float textY = y + (rowHeight - 20) / 2;
            uint dayInk = foreground;
            TextRole dayRole = TextRole.Label;
            if (today)
            {
                float diameter = Math.Min(layout.Standard ? 28 : 26, rowHeight - 2);
                float cx = x + cellWidth / 2, cy = y + rowHeight / 2;
                uint fillToday = highContrast ? accent : red;
                Ellipse(operations, cx - diameter / 2, cy - diameter / 2, diameter, diameter, fillToday);
                dayInk = highContrast ? (_background | 0xFF000000u) : 0xFFFFFFFFu;
                dayRole = TextRole.SectionTitle;
            }
            Text(day.ToString(CultureInfo.InvariantCulture), dayRole, x, textY, cellWidth, 20, dayInk, true);
        }

        var mediaCard = layout.Media;
        Card(mediaCard);
        Text("正在播放", TextRole.Label, mediaCard.X + 16, mediaCard.Y + 10, 170, 20, foreground);
        var cover = layout.MediaCover;
        if (media.Cover is { Length: > 0 } && media.Connected)
            operations.Add(new SurfaceOp { Kind = SurfaceOpKind.Image, X = cover.X, Y = cover.Y,
                Width = cover.Width, Height = cover.Height, Radius = layout.Standard ? 18 : 16, Image = media.Cover });
        else
        {
            uint placeholder = highContrast ? foreground : lightInk ? 0x3CF5F7FAu : 0x18343C45u;
            Ellipse(operations, cover.X + cover.Width * .17f, cover.Y + cover.Height * .17f,
                cover.Width * .66f, cover.Height * .66f, placeholder);
            operations.Add(Glyph("\uE8D6", cover.X + cover.Width * .36f, cover.Y + cover.Height * .36f,
                cover.Width * .28f, foreground));
        }

        var title = layout.MediaTitle; var artist = layout.MediaArtist;
        string titleText = media.Connected
            ? string.IsNullOrWhiteSpace(media.Title) ? "歌曲信息暂不可用" : media.Title
            : "尚未连接音乐";
        Text(titleText, TextRole.Label, title.X, title.Y, title.Width, title.Height, foreground);
        if (media.Connected)
            Text(string.IsNullOrWhiteSpace(media.Artist) ? "歌手信息暂不可用" : media.Artist,
                TextRole.Caption, artist.X, artist.Y, artist.Width, artist.Height, quiet);
        else
            Text("连接网易云音乐后显示播放信息", TextRole.Caption, artist.X, artist.Y, artist.Width, artist.Height, quiet);

        if (media.Connected)
        {
            var controls = layout.MediaControls;
            AddMediaButton(controls.Previous, WidgetActions.HomeMediaPrevious, "\uE892", 18, media.Capabilities.Previous);
            AddMediaButton(controls.Next, WidgetActions.HomeMediaNext, "\uE893", 18, media.Capabilities.Next);
        }
        else
        {
            var open = layout.MediaOpenMusic;
            Text("打开网易云音乐", TextRole.Label, open.X, open.Y + 8, open.Width, 20, foreground, true);
        }

        void AddMediaButton(FloatRect rect, int id, string glyph, float glyphSize, bool enabled)
        {
            uint ink = enabled ? foreground : (foreground & 0x00FFFFFFu) | 0x59000000u;
            float scale = pressed == id ? .96f : hoveredAction == id ? 1.03f : 1;
            glyphSize *= scale;
            operations.Add(Glyph(glyph, rect.X + (rect.Width - glyphSize) / 2,
                rect.Y + (rect.Height - glyphSize) / 2, glyphSize, ink));
        }
        return Sanitize(operations);
    }

    List<SurfaceOp> StatusOperations(HomePresentationLayout layout, DateTimeOffset now,
        HomeSystemStatusSnapshot status, bool dragging, string? volumeNotice, int pressed, int hoveredAction,
        bool highContrast, bool lightInk, uint foreground, uint secondary)
    {
        var operations = new List<SurfaceOp>(32);
        _ = volumeNotice;
        void Text(string value, TextRole role, FloatRect rect, uint color, bool center = false, TextFlags? flags = null)
        {
            if (rect.Width <= 0 || rect.Height <= 0 || rect.X < 0 || rect.Y < 0
                || rect.Right > layout.Width || rect.Bottom > layout.Height) return;
            operations.Add(SurfaceOp.Typography(value, role, rect.X, rect.Y, rect.Width, rect.Height,
                color, center, numericFlags: flags, locale: "zh-CN"));
        }
        bool twelveHour = CultureInfo.CurrentCulture.DateTimeFormat.ShortTimePattern.Contains('h');
        Text(now.ToString(twelveHour ? "h:mm" : "HH:mm", CultureInfo.CurrentCulture),
            TextRole.SectionTitle, layout.Clock, foreground, flags: TextFlags.TabularNumbers);

        var battery = layout.StatusBattery;
        bool hasBattery = status.HasBattery != false;
        if (hasBattery)
        {
            float bx = battery.X, by = battery.Y, bw = 25, bh = 12;
            uint ink = highContrast ? foreground : lightInk ? 0xFFFFFFFFu : 0xFF1C1C1Eu;
            uint body = highContrast ? secondary : (ink & 0x00FFFFFFu) | 0x59000000u;
            operations.Add(Round(bx, by, bw, bh, 3.5f, body));
            operations.Add(Round(bx + bw + 1, by + 3.75f, 1.5f, 4.5f, .75f, (ink & 0x00FFFFFFu) | 0x66000000u));
            if (status.HasBattery == true && status.BatteryPercent0To100 is byte percent && percent <= 100)
            {
                bool charging = status.Charging == true;
                uint level = highContrast ? foreground
                    : charging ? (lightInk ? 0xFF30D158u : 0xFF34C759u)
                    : percent <= 20 ? (lightInk ? 0xFFFF453Au : 0xFFFF3B30u)
                    : ink;
                float levelWidth = (bw - 4) * percent / 100f;
                if (levelWidth >= .5f)
                    operations.Add(Round(bx + 2, by + 2, levelWidth, bh - 4, Math.Min(2f, levelWidth / 2), level));
                if (charging)
                    operations.Add(Glyph("\uE945", bx + (bw - 9) / 2, by + (bh - 9) / 2, 9,
                        lightInk && !highContrast ? 0xFF1C1C1Eu : 0xFFFFFFFFu));
            }
        }

        var bar = layout.VolumeBar;
        bool hot = dragging || hoveredAction == WidgetActions.HomeVolume || pressed == WidgetActions.HomeVolume;
        float barWidth = hot ? 8 : 6;
        float barX = bar.Right - barWidth, radius = barWidth / 2;
        bool usable = status.AudioAvailable && status.HasValidVolume;
        uint track = highContrast ? secondary
            : lightInk ? (usable ? 0x3DFFFFFFu : 0x1FFFFFFFu) : (usable ? 0x2E1C1C1Eu : 0x171C1C1Eu);
        operations.Add(Round(barX, bar.Y, barWidth, bar.Height, radius, track));
        if (usable && status.VolumeScalar is float scalar)
        {
            float value = Math.Clamp(scalar, 0, 1);
            float fillHeight = bar.Height * value;
            uint fill = highContrast ? foreground : lightInk ? 0xF2FFFFFFu : 0xD91C1C1Eu;
            if (status.Muted == true) fill = (fill & 0x00FFFFFFu) | 0x59000000u;
            if (fillHeight >= 0.5f)
                operations.Add(Round(barX, bar.Bottom - fillHeight, barWidth, fillHeight,
                    Math.Min(radius, fillHeight / 2), fill));
        }
        return Sanitize(operations);
    }

    static Vector2[] WifiBand(float inner, float outer)
    {
        const int arcSteps = 14, capSteps = 6;
        var o = new Vector2(9f, 11.8f);
        float a0 = -3f * MathF.PI / 4, a1 = -MathF.PI / 4;
        Vector2 At(float r, float a) => o + new Vector2(MathF.Cos(a), MathF.Sin(a)) * r;
        var p = new List<Vector2>();
        if (inner <= 0)
        {
            p.Add(o);
            for (int i = 0; i <= arcSteps; i++) p.Add(At(outer, a0 + (a1 - a0) * i / arcSteps));
            return p.ToArray();
        }
        float mid = (inner + outer) / 2, half = (outer - inner) / 2;
        for (int i = 0; i <= arcSteps; i++) p.Add(At(outer, a0 + (a1 - a0) * i / arcSteps));
        Vector2 c1 = At(mid, a1), tangent1 = new(-MathF.Sin(a1), MathF.Cos(a1)), radial1 = new(MathF.Cos(a1), MathF.Sin(a1));
        for (int i = 1; i < capSteps; i++)
        {
            float t = MathF.PI * i / capSteps;
            p.Add(c1 + radial1 * (half * MathF.Cos(t)) + tangent1 * (half * MathF.Sin(t)));
        }
        for (int i = 0; i <= arcSteps; i++) p.Add(At(inner, a1 + (a0 - a1) * i / arcSteps));
        Vector2 c0 = At(mid, a0), tangent0 = new(MathF.Sin(a0), -MathF.Cos(a0)), radial0 = new(MathF.Cos(a0), MathF.Sin(a0));
        for (int i = 1; i < capSteps; i++)
        {
            float t = MathF.PI * i / capSteps;
            p.Add(c0 - radial0 * (half * MathF.Cos(t)) + tangent0 * (half * MathF.Sin(t)));
        }
        return p.ToArray();
    }

    GlassShape[] BuildGlassShapes(HomePresentationLayout layout, MediaSnapshot media,
        HomeSystemStatusSnapshot status, bool volumeDragging, bool navExpanded, int selectedFolder,
        HomeFolderPreview? preview, bool hoveredNav, int hoveredApp, int hoveredFolder,
        int hoveredAction, int pressed)
    {
        const uint weatherId = 1, calendarId = 2, mediaCardId = 3, dockId = 4,
            navId = 5, navRailId = 7, popupId = 8;
        const uint calendarPreviousId = 100, calendarNextId = 101,
            mediaPreviousId = 102, mediaToggleId = 103, mediaNextId = 104,
            mediaOpenId = 105, folderId = 108, navButtonId = 109, openFolderId = 110;
        const int maximumGlassShapes = 32;
        var result = new List<GlassShape>(24);

        static bool Inside(FloatRect rect, FloatRect clip) => rect.Width > 0 && rect.Height > 0
            && rect.X >= clip.X && rect.Y >= clip.Y && rect.Right <= clip.Right && rect.Bottom <= clip.Bottom;
        float Feedback(int actionId) => pressed == actionId ? 1f : hoveredAction == actionId ? .35f : 0f;
        void Add(uint id, uint layer, FloatRect rect, float radius, float press = 0, bool enabled = true)
        {
            if (!float.IsFinite(rect.X) || !float.IsFinite(rect.Y) || !float.IsFinite(rect.Width)
                || !float.IsFinite(rect.Height) || rect.Width <= 0 || rect.Height <= 0) return;
            result.Add(new GlassShape(id, 0, layer, 0, rect.X, rect.Y, rect.Width, rect.Height,
                Math.Clamp(radius, 0, Math.Min(rect.Width, rect.Height) / 2), 1,
                Math.Clamp(press, 0, 1), enabled));
        }
        void AddContent(uint id, uint layer, FloatRect rect, float radius, float press = 0)
        {
            FloatRect presented = layout.PresentedContent(rect);
            // The frozen shape ABI has no child scissor. Omit a partially clipped shape
            // rather than extending its glass behind the fixed volume rail or Dock.
            if (Inside(presented, layout.ContentViewport)) Add(id, layer, presented, radius, press);
        }

        if (layout.DashboardCardsVisible)
        {
            AddContent(weatherId, 2, layout.Weather, 24);
            AddContent(calendarId, 2, layout.Calendar, 26);
            AddContent(mediaCardId, 2, layout.Media, 26);
        }

        if (layout.VisibleOverlayTarget(layout.DockPill) is { } dock)
            Add(dockId, 2, dock, dock.Height / 2,
                pressed is >= WidgetActions.HomeFolderFirst and < WidgetActions.HomeFolderFirst + WidgetActions.HomeFolderCount ? 1 : 0);
        if (layout.VisibleOverlayTarget(layout.NavTrigger) is { } nav)
        {
            Add(navId, 2, nav, nav.Width / 2, pressed == WidgetActions.HomeNavToggle ? 1 : 0);
            if (hoveredNav || pressed == WidgetActions.HomeNavToggle || navExpanded)
                Add(navButtonId, 3, nav, nav.Width / 2, Feedback(WidgetActions.HomeNavToggle));
        }
        if (hoveredFolder >= 0 || pressed == WidgetActions.HomeFolderFirst)
            if (layout.VisibleOverlayTarget(layout.DockFolder) is { } folder)
                Add(folderId, 3, folder, folder.Width / 2,
                    pressed == WidgetActions.HomeFolderFirst ? 1 : hoveredFolder >= 0 ? .35f : 0);
        for (int index = 0; index < 2; index++)
        {
            int action = WidgetActions.HomeAppFirst + index;
            if (hoveredApp == index || pressed == action)
                if (layout.VisibleOverlayTarget(layout.DockAppSlot(index)) is { } app)
                    Add(120u + (uint)index, 3, app, app.Width / 2,
                        pressed == action ? 1 : .35f);
        }

        if (navExpanded && layout.VisibleOverlayTarget(layout.NavRail) is { } rail)
        {
            Add(navRailId, 2, rail, rail.Width / 2);
            for (int index = 0; index < 5; index++)
                if (layout.VisibleOverlayTarget(layout.NavRailItem(index)) is { } item)
                    Add(130u + (uint)index, 3, item, item.Width / 2,
                        pressed == WidgetActions.HomeNavFirst + index ? 1 : 0);
        }

        if (layout.VisibleContentTarget(layout.CalendarPrevious) is { } calendarPrevious)
            Add(calendarPreviousId, 3, calendarPrevious, calendarPrevious.Height / 2, Feedback(WidgetActions.HomeCalendarPrevious));
        if (layout.VisibleContentTarget(layout.CalendarNext) is { } calendarNext)
            Add(calendarNextId, 3, calendarNext, calendarNext.Height / 2, Feedback(WidgetActions.HomeCalendarNext));
        if (media.Connected)
        {
            if (layout.VisibleContentTarget(layout.MediaPrevious) is { } mediaPrevious)
                Add(mediaPreviousId, 3, mediaPrevious, mediaPrevious.Width / 2,
                    Feedback(WidgetActions.HomeMediaPrevious), media.Capabilities.Previous);
            if (layout.VisibleContentTarget(layout.MediaToggle) is { } mediaToggle)
                Add(mediaToggleId, 3, mediaToggle, mediaToggle.Width / 2,
                    Feedback(WidgetActions.HomeMediaToggle), media.Capabilities.Toggle);
            if (layout.VisibleContentTarget(layout.MediaNext) is { } mediaNext)
                Add(mediaNextId, 3, mediaNext, mediaNext.Width / 2,
                    Feedback(WidgetActions.HomeMediaNext), media.Capabilities.Next);
        }
        else if (layout.VisibleContentTarget(layout.MediaOpenMusic) is { } openMusic)
            Add(mediaOpenId, 3, openMusic, openMusic.Height / 2, Feedback(WidgetActions.HomeMediaOpen));

        if (selectedFolder >= 0 && preview is not null && layout.CanShowFolderPopover
            && layout.VisibleOverlayTarget(layout.Popover) is { } popup)
        {
            Add(popupId, 2, popup, 20);
            if (preview.Status == HomeFolderStatus.Ready)
            {
                for (int index = 0; index < Math.Min(3, preview.Entries.Count); index++)
                    if (layout.VisibleOverlayTarget(layout.Preview(index)) is { } previewItem)
                        Add(140u + (uint)index, 3, previewItem, 10,
                            pressed == WidgetActions.HomePreviewFirst + index ? 1 : 0,
                            !string.IsNullOrWhiteSpace(preview.Entries[index].Path));
            }
            if (layout.VisibleOverlayTarget(layout.PopoverOpenButton) is { } openFolder)
                Add(openFolderId, 3, openFolder, 12,
                    pressed == WidgetActions.HomeOpenFolder ? 1 : 0,
                    !string.IsNullOrWhiteSpace(preview.Path));
        }

        return result.Count <= maximumGlassShapes
            ? result.ToArray()
            : throw new InvalidOperationException("Home glass scene exceeded the versioned 32-shape ABI limit.");
    }

    static List<SurfaceOp> Sanitize(List<SurfaceOp> operations)
        => operations.Where(operation => float.IsFinite(operation.X) && float.IsFinite(operation.Y)
            && float.IsFinite(operation.Width) && float.IsFinite(operation.Height)
            && operation.Width > 0 && (operation.Kind == SurfaceOpKind.Line
                ? operation.Height >= 0 : operation.Height > 0)
            && float.IsFinite(operation.Radius) && operation.Radius >= 0).ToList();

    static void Ellipse(List<SurfaceOp> operations, float x, float y, float width, float height, uint color)
    {
        if (!float.IsFinite(x) || !float.IsFinite(y) || !float.IsFinite(width) || !float.IsFinite(height)
            || width <= 0 || height <= 0) return;
        operations.Add(new SurfaceOp { Kind = SurfaceOpKind.Ellipse, X = x, Y = y,
            Width = width, Height = height, Color = color });
    }

    static string PlaybackText(PlaybackState state) => state switch
    {
        PlaybackState.Playing => "正在播放",
        PlaybackState.Paused => "已暂停",
        PlaybackState.Changing => "正在切歌",
        PlaybackState.Stopped or PlaybackState.Closed => "已停止",
        _ => "已连接"
    };

    static string LunarCaption(DateTime date)
    {
        try
        {
            var calendar = new ChineseLunisolarCalendar();
            int year = calendar.GetYear(date), month = calendar.GetMonth(date), day = calendar.GetDayOfMonth(date);
            int leapMonth = calendar.GetLeapMonth(year), normalMonth = leapMonth > 0 && month >= leapMonth ? month - 1 : month;
            string[] months = ["正", "二", "三", "四", "五", "六", "七", "八", "九", "十", "冬", "腊"];
            if (day == 1) return (leapMonth > 0 && month == leapMonth ? "闰" : "") + months[Math.Clamp(normalMonth - 1, 0, 11)] + "月";
            string[] days = ["初一", "初二", "初三", "初四", "初五", "初六", "初七", "初八", "初九", "初十",
                "十一", "十二", "十三", "十四", "十五", "十六", "十七", "十八", "十九", "二十",
                "廿一", "廿二", "廿三", "廿四", "廿五", "廿六", "廿七", "廿八", "廿九", "三十"];
            return days[Math.Clamp(day - 1, 0, days.Length - 1)];
        }
        catch (ArgumentOutOfRangeException) { return date.ToString("M/d", CultureInfo.GetCultureInfo("zh-CN")); }
    }

    static string LunarFull(DateTime date)
    {
        try
        {
            var calendar = new ChineseLunisolarCalendar();
            int day = calendar.GetDayOfMonth(date);
            string month = LunarCaption(date.AddDays(1 - day));
            string dayName = day == 1 ? "初一" : LunarCaption(date);
            return month + dayName;
        }
        catch (ArgumentOutOfRangeException) { return LunarCaption(date); }
    }

    void PresentPopup(HomePresentationLayout layout, int selected, HomeFolderPreview? preview,
        int pressed, uint secondary, uint dpi, bool paletteChanged)
    {
        bool open = selected >= 0 && preview is not null && layout.CanShowFolderPopover;
        _popupFace.IsVisible = _highContrast;
        if (open)
        {
            var pop = layout.Popover;
            _popup.Size = new(pop.Width, pop.Height); _popup.CenterPoint = new(pop.Width / 2, pop.Height / 2, 0);
            _popupFace.Size = new(pop.Width, pop.Height);
            var key = HomePopupRenderKey.Create(layout, selected, preview!, pressed,
                _foreground, _background, secondary, dpi, _highContrast);
            if (_popupKey != key || paletteChanged)
            {
                Path(_popupGeometry, PopupPath(pop.Width, pop.Height));
                Colors(_popupFill, _highContrast ? _background | 0xFF000000u : 0x00000000u,
                    _highContrast ? _background | 0xFF000000u : 0x00000000u);
                Colors(_popupRim, _highContrast ? _foreground | 0xFF000000u : 0x00000000u,
                    _highContrast ? _foreground | 0xFF000000u : 0x00000000u);
                _popupInk.Set(new(pop.Width, pop.Height), PopupOperations(layout, preview!, secondary, pressed));
                _popupKey = key;
            }
            _popup.Offset = new(pop.X, pop.Y, 0);
            _popup.Scale = Vector3.One;
            _popup.Opacity = 1;
            _popup.IsVisible = true;
        }
        else
        {
            _popup.Opacity = 0;
            _popup.IsVisible = false;
        }
        _popupOpen = open;
    }

    List<SurfaceOp> PopupOperations(HomePresentationLayout layout, HomeFolderPreview preview, uint secondary, int pressed)
    {
        var pop = layout.Popover; var ops = new List<SurfaceOp>();
        var title = layout.PopoverTitle;
        ops.Add(SurfaceOp.Typography(preview.Name, TextRole.Label, 12, 12, title.Width, 20, _foreground));
        if (preview.Status != HomeFolderStatus.Ready || preview.Entries.Count == 0)
        {
            string message = preview.Status switch { HomeFolderStatus.Loading => "正在读取文件夹…", HomeFolderStatus.Error => preview.Message ?? "无法读取文件夹", _ => "这个文件夹是空的" };
            ops.Add(SurfaceOp.Typography(message, TextRole.Caption, 12, 43, pop.Width - 24, Math.Max(24, pop.Height - 110), secondary, true) with { Kind = SurfaceOpKind.MultilineText });
        }
        else for (int i = 0; i < Math.Min(3, preview.Entries.Count); i++)
        {
            var rect = layout.Preview(i); float x = rect.X - pop.X, y = rect.Y - pop.Y;
            if (pressed == WidgetActions.HomePreviewFirst + i) ops.Add(Round(x, y, rect.Width, rect.Height, 10, 0x183E607Du));
            var entry = preview.Entries[i]; bool compact = rect.Height < 80;
            float iconSize = compact ? 18 : 26, iconY = compact ? 1 : 8;
            ops.Add(Glyph(entry.IsFolder ? "\uE8B7" : "\uE8A5", x + (rect.Width - iconSize) / 2, y + iconY, iconSize,
                _highContrast ? _foreground : entry.IsFolder ? 0xFFD98F07u : secondary));
            ops.Add(SurfaceOp.Typography(entry.Name, TextRole.Caption, x + 3, y + (compact ? 22 : 44),
                rect.Width - 6, 32, _foreground, true) with { Kind = SurfaceOpKind.MultilineText });
        }
        var button = layout.PopoverOpenButton;
        bool canOpen = !string.IsNullOrWhiteSpace(preview.Path);
        ops.Add(Round(button.X - pop.X, button.Y - pop.Y, button.Width, button.Height, 12,
            !canOpen ? 0x063E607Du : pressed == WidgetActions.HomeOpenFolder ? 0x303E607Du : 0x123E607Du));
        ops.Add(SurfaceOp.Typography("打开文件夹", TextRole.Label, 12, button.Y - pop.Y + 12, button.Width, 20,
            canOpen ? _foreground : TypographyCatalog.Ink(InkRole.Disabled), true));
        return ops;
    }

    void Move(Visual visual, Vector3 target, bool immediate, int duration, int delay = 0) => Vector(visual, "Offset", target, immediate, duration, delay);
    void Scale(Visual visual, Vector3 target, bool immediate, int duration) => Vector(visual, "Scale", target, immediate, duration);
    void Vector(Visual visual, string property, Vector3 target, bool immediate, int duration, int delay = 0)
    {
        if (!immediate && _vectorTargets.TryGetValue((visual, property), out var previous) && previous == target) return;
        _vectorTargets[(visual, property)] = target;
        if (!_animated.Contains(visual)) _animated.Add(visual);
        if (property == "Offset") _spring.Move(visual, target, immediate, duration, delay);
        else _spring.Scale(visual, target, immediate, duration);
    }
    void Scalar(Visual visual, string property, float value, bool immediate, int duration)
    {
        _spring.Scalar(visual, property, value, immediate, duration);
    }

    static SurfaceOp Round(float x, float y, float w, float h, float radius, uint ink)
        => new() { Kind = SurfaceOpKind.RoundedRectangle, X = x, Y = y, Width = w, Height = h, Radius = radius, Color = ink };
    static SurfaceOp Glyph(string text, float x, float y, float size, uint ink)
        => new() { Kind = SurfaceOpKind.Icon, Text = text, X = x, Y = y, Width = size, Height = size, FontSize = size, Color = ink, Center = true };
    static Vector2[] ArcPath(float w, float h)
    {
        var p = new List<Vector2>();
        void Arc(float cx, float cy, float rx, float ry, float start)
        { for (int i = 0; i <= 16; i++) { float a = start + MathF.PI / 2 * i / 16; p.Add(new(cx + rx * MathF.Cos(a), cy + ry * MathF.Sin(a))); } }
        p.Add(new(w * .1f, 0)); p.Add(new(w * .87f, 0));
        Arc(w * .87f, h * .12f, w * .13f, h * .12f, -MathF.PI / 2);
        p.Add(new(w, h * .25f)); Arc(w * .58f, h * .25f, w * .42f, h * .75f, 0);
        p.Add(new(0, h)); p.Add(new(0, h * .1f)); Arc(w * .1f, h * .1f, w * .1f, h * .1f, MathF.PI);
        return p.ToArray();
    }
    static Vector2[] PopupPath(float w, float h)
    {
        var p = new List<Vector2>();
        void Corner(float cx, float cy, float start)
        { for (int i = 0; i <= 10; i++) { float a = start + MathF.PI / 2 * i / 10; p.Add(new(cx + 20 * MathF.Cos(a), cy + 20 * MathF.Sin(a))); } }
        p.Add(new(20, 0)); p.Add(new(w - 20, 0)); Corner(w - 20, 20, -MathF.PI / 2);
        p.Add(new(w, h - 20)); Corner(w - 20, h - 20, 0);
        p.Add(new(20, h)); Corner(20, h - 20, MathF.PI / 2);
        p.Add(new(0, 20)); Corner(20, 20, MathF.PI); return p.ToArray();
    }

    sealed class Layer : IDisposable
    {
        readonly NativeWidgetHost _host;
        readonly CompositionSurfaceBrush _brush;
        CompositionDrawingSurface? _surface;
        string? _key;
        public SpriteVisual Visual { get; }
        public Layer(NativeWidgetHost host, ContainerVisual parent)
        { _host = host; Visual = host.Compositor.CreateSpriteVisual(); _brush = host.Compositor.CreateSurfaceBrush(); _brush.Stretch = CompositionStretch.Fill; Visual.Brush = _brush; parent.Children.InsertAtTop(Visual); }
        public void Invalidate() => _key = null;
        public void SetCached(string key, Vector2 size, IReadOnlyList<SurfaceOp> ops) { if (_key == key) return; Set(size, ops); _key = key; }
        public void Set(Vector2 size, IReadOnlyList<SurfaceOp> ops)
        {
            CompositionDrawingSurface replacement;
            try { replacement = _host.RenderSurface(size.X, size.Y, ops); }
            catch (Exception error) { throw new HomeSurfaceRenderException(size, ops, error); }
            var old = _surface;
            _brush.Surface = replacement; _surface = replacement; Visual.Size = size; old?.Dispose();
        }
        public void Dispose() { Visual.StopAnimation("Opacity"); _brush.Surface = null; _surface?.Dispose(); _brush.Dispose(); Visual.Dispose(); }
    }

    sealed class Folder
    {
        readonly HomeVisual _owner;
        readonly ContainerVisual _body;
        readonly ShapeVisual[] _papers = new ShapeVisual[3];
        readonly ShapeVisual _front;
        readonly CompositionLinearGradientBrush _frontFill, _frontRim, _backFill;
        readonly CompositionSpriteShape _frontShape;
        readonly CompositionColorBrush _backEdge;
        readonly CompositionColorBrush[] _paperInk = new CompositionColorBrush[3];
        readonly ShapeVisual[] _shadows = new ShapeVisual[3];
        bool? _open, _pressed;
        bool _reduced, _high;
        uint _foreground, _background;
        public ContainerVisual Visual { get; }
        public Folder(HomeVisual owner)
        {
            _owner = owner; var c = owner._compositor; Visual = owner.Container(); Visual.Size = new(44);
            for (int i = 0; i < 3; i++)
            {
                var shadow = _shadows[i] = owner.Shape(); shadow.Size = new(44);
                var ellipse = owner.Own(c.CreateEllipseGeometry());
                ellipse.Center = new(22, 36.3f); ellipse.Radius = new(15 + i * 1.2f, 1.6f + i * .7f);
                var shape = owner.Own(c.CreateSpriteShape(ellipse)); shape.FillBrush = owner.Ink((uint)(14 - i * 4) << 24);
                shadow.Shapes.Add(shape); Visual.Children.InsertAtBottom(shadow);
            }
            _body = owner.Container(); _body.Size = new(44); _body.CenterPoint = new(22, 22, 0);
            Visual.Children.InsertAtTop(_body);
            var perspective = owner.Container(); perspective.Size = new(44);
            Matrix4x4 projection = Matrix4x4.Identity; projection.M34 = -1f / 120;
            perspective.TransformMatrix = Matrix4x4.CreateTranslation(-22, -22, 0) * projection * Matrix4x4.CreateTranslation(22, 22, 0);
            _body.Children.InsertAtTop(perspective);
            var back = owner.Shape(); back.Size = new(36, 30); back.Offset = new(4, 5, 0);
            var backGeo = owner.Own(c.CreatePathGeometry()); owner.Path(backGeo, FolderPath(true).Select(p => p + new Vector2(1, 5)).ToArray());
            var backShape = owner.Own(c.CreateSpriteShape(backGeo));
            _backFill = owner.Gradient(0xFFE5A322u, 0xFFD88C05u); backShape.FillBrush = _backFill;
            _backEdge = owner.Ink(0); backShape.StrokeBrush = _backEdge; backShape.StrokeThickness = 1;
            back.Shapes.Add(backShape); perspective.Children.InsertAtTop(back);
            uint[] paperColors = [0xFFB6BFCAu, 0xFFD5DCE3u, 0xFFF1F4F7u];
            for (int i = 0; i < 3; i++)
            {
                _paperInk[i] = owner.Ink(paperColors[i]);
                var paper = owner.Rounded(new(30 - i, 21), 2.5f, _paperInk[i]);
                paper.Offset = new(7 + i * .5f, 13, .1f * i); paper.CenterPoint = new((30 - i) / 2f, 21, 0);
                paper.RotationAxis = Vector3.UnitX; paper.Opacity = 0;
                _papers[i] = paper; perspective.Children.InsertAtTop(paper);
            }
            _front = owner.Shape(); _front.Size = new(36, 27); _front.Offset = new(4, 8, .5f);
            _front.CenterPoint = new(18, 26, 0); _front.RotationAxis = Vector3.UnitX;
            var frontGeo = owner.Own(c.CreatePathGeometry()); owner.Path(frontGeo, FolderPath(false).Select(p => p + new Vector2(1, 5)).ToArray());
            _frontShape = owner.Own(c.CreateSpriteShape(frontGeo));
            _frontFill = owner.Gradient(0xFFFFC733u, 0xFFF3A008u); _frontShape.FillBrush = _frontFill;
            _frontRim = owner.Gradient(0xC0FFE49Au, 0x85CB8306u); _frontShape.StrokeBrush = _frontRim; _frontShape.StrokeThickness = .55f;
            _front.Shapes.Add(_frontShape); perspective.Children.InsertAtTop(_front);
        }
        static Vector2[] FolderPath(bool back)
        {
            var p = new List<Vector2>();
            void Q(Vector2 control, Vector2 end)
            {
                Vector2 start = p[^1];
                for (int i = 1; i <= 5; i++) { float t = i / 5f; p.Add((1-t)*(1-t)*start + 2*(1-t)*t*control + t*t*end); }
            }
            if (back)
            {
                p.Add(new(0, 5)); p.Add(new(0, -1)); Q(new(0, -4), new(4, -4)); p.Add(new(12, -4));
                Q(new(15, -4), new(15, 0)); p.Add(new(29, 0));
            }
            else
            {
                p.Add(new(5, 0)); p.Add(new(11, 0)); Q(new(12, 0), new(12, -1));
                Q(new(12, -4), new(15, -4)); p.Add(new(30, -4)); Q(new(34, -4), new(34, 0));
            }
            if (back) Q(new(34, 0), new(34, 5));
            p.Add(new(34, back ? 19 : 16)); Q(new(34, back ? 24 : 21), new(29, back ? 24 : 21));
            p.Add(new(5, back ? 24 : 21)); Q(new(0, back ? 24 : 21), new(0, back ? 19 : 16));
            p.Add(new(0, 5)); if (!back) Q(new(0, 0), new(5, 0));
            return p.ToArray();
        }
        public void Set(FloatRect slot, bool open, bool pressed, bool immediate, bool high, uint foreground, uint background)
        {
            Visual.Offset = new(slot.X, slot.Y, 0);
            if (_open == open && _pressed == pressed && _reduced == immediate && _high == high && _foreground == foreground && _background == background) return;
            _high = high; _foreground = foreground; _background = background;
            bool initial = _open is null; _open = open; _pressed = pressed; _reduced = immediate;
            immediate |= initial;
            _owner.Move(_body, new(0, pressed ? 1 : open ? -2 : 0, 0), immediate, pressed ? 90 : 180);
            _owner.Scale(_body, pressed ? new(.97f, .97f, 1) : Vector3.One, immediate, 120);
            for (int i = 0; i < 3; i++)
            {
                _owner.Scalar(_papers[i], "RotationAngleInDegrees", open ? -(20 + i * 9) : 0, immediate, 300);
                _owner.Scalar(_papers[i], "Opacity", open ? 1 : 0, immediate, open ? 180 : 100);
            }
            _owner.Scalar(_front, "RotationAngleInDegrees", open ? -46 : 0, immediate, 300);
            Colors(_frontFill, high ? background : open ? 0xFFFFD04Du : 0xFFFFC733u, high ? background : 0xFFF3A008u);
            Colors(_frontRim, high ? foreground : 0xC0FFE49Au, high ? foreground : 0x85CB8306u);
            Colors(_backFill, high ? background : 0xFFE5A322u, high ? background : 0xFFD88C05u);
            _backEdge.Color = ColorOf(high ? foreground : 0);
            _frontShape.StrokeThickness = high ? 1.2f : .55f;
            uint[] paper = [0xFFB6BFCAu, 0xFFD5DCE3u, 0xFFF1F4F7u];
            for (int i = 0; i < 3; i++) { _paperInk[i].Color = ColorOf(high ? foreground : paper[i]); _shadows[i].IsVisible = !high; }
        }
    }

    public void Invalidate()
    {
        _helpKey = null; _popupKey = null; _notice.Invalidate();
        _dashboard.Invalidate(); _dashboardStatus.Invalidate(); _dockTriggerIcon.Invalidate(); _help.Invalidate();
        _ethernet.Invalidate();
        foreach (var layer in _appArt) layer.Invalidate();
    }
    public void SetAppIcon(int index, byte[] bytes)
    { _appIcons[index] = bytes; _iconRevision++; }
    internal void SetDockOpticalGlass(bool enabled)
    {
        _dockPlate.SetOpticalGlass(enabled);
        _dockNavPlate.SetOpticalGlass(enabled);
        _navigationPlate.SetOpticalGlass(enabled);
    }
    public void Dispose()
    {
        if (_disposed) return; _disposed = true;
        StopMotion();
        _mediaToggleRoot.Children.Remove(_mediaToggleGlyph.Visual);
        _mediaToggleGlyph.Dispose();
        foreach (var container in _containers) container.Children.RemoveAll();
        foreach (var visual in _shapeVisuals) visual.Shapes.Clear();
        _dashboard.Dispose(); _dashboardStatus.Dispose(); _dockTriggerIcon.Dispose(); _navigationIcons.Dispose(); _help.Dispose(); _popupInk.Dispose(); _notice.Dispose(); _ethernet.Dispose();
        foreach (var layer in _appArt) layer.Dispose();
        _paths.Clear();
        for (int i = _owned.Count - 1; i >= 0; i--) _owned[i].Dispose();
    }
    void StopMotion()
    {
        _spring.Dispose();
        _vectorTargets.Clear();
        foreach (var visual in _animated)
            foreach (string property in new[] { "Offset", "Scale", "Opacity", "RotationAngleInDegrees" }) visual.StopAnimation(property);
    }
}

internal sealed class HomeSurfaceRenderException : Exception
{
    public Vector2 SurfaceSize { get; }
    public int OperationCount { get; }
    public int InvalidOperationIndex { get; }
    public SurfaceOp? InvalidOperation { get; }

    public HomeSurfaceRenderException(Vector2 size, IReadOnlyList<SurfaceOp> operations, Exception inner)
        : base("Home foreground surface rendering failed.", inner)
    {
        SurfaceSize = size; OperationCount = operations.Count;
        InvalidOperationIndex = -1;
        for (int index = 0; index < operations.Count; index++)
        {
            var op = operations[index];
            bool valid = float.IsFinite(op.X) && float.IsFinite(op.Y) && float.IsFinite(op.Width)
                && float.IsFinite(op.Height) && op.Width > 0
                && (op.Kind == SurfaceOpKind.Line ? op.Height >= 0 : op.Height > 0)
                && float.IsFinite(op.Radius) && op.Radius >= 0;
            if (valid) continue;
            InvalidOperationIndex = index; InvalidOperation = op; break;
        }
    }
}


using System.Numerics;
using GlassDesk.Core;
using GlassDesk.NativeBridge;
using Windows.UI;
using Windows.UI.Composition;

namespace GlassDesk.App;

/// <summary>Persistent Home Dock and folder surfaces. Navigation lives in an owned side window.</summary>
internal sealed class HomeVisual : IDisposable
{
    readonly NativeWidgetHost _host;
    readonly Compositor _compositor;
    readonly List<IDisposable> _owned = [];
    readonly List<ContainerVisual> _containers = [];
    readonly List<ShapeVisual> _shapeVisuals = [];
    readonly List<Visual> _animated = [];
    readonly Dictionary<(Visual, string), Vector3> _vectorTargets = [];
    readonly Dictionary<(Visual, string), float> _scalarTargets = [];
    readonly Dictionary<int, byte[]> _appIcons = [];
    int _iconRevision;
    readonly Dictionary<CompositionPathGeometry, CompositionPath> _paths = [];
    readonly ContainerVisual _popup;
    readonly ControlDepthVisual _dockPlate, _dockNavPlate, _navigationPlate;
    readonly Layer _dockTriggerIcon, _navigationIcons, _help, _popupInk, _notice;
    readonly Layer[] _appArt = new Layer[2];
    readonly Folder[] _folders = new Folder[1];
    readonly CompositionPathGeometry _popupGeometry;
    readonly ShapeVisual _popupFace;
    readonly CompositionSpriteShape _popupShape;
    readonly CompositionLinearGradientBrush _popupFill, _popupRim;
    HomePresentationLayout? _layout;
    bool _visible, _popupOpen, _popupPresented, _reduced, _disposed;
    bool _highContrast;
    uint _foreground, _background;
    string? _helpKey;
    HomePopupRenderKey? _popupKey;
    public ContainerVisual Visual { get; }

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
        _host = host; _compositor = host.Compositor;
        Visual = Container();
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
        _popupFill = Gradient(0xFAFFFFFF, 0xF5EEF2F6); _popupRim = Gradient(0xE6FFFFFF, 0x403F5368);
        _popupShape.FillBrush = _popupFill; _popupShape.StrokeBrush = _popupRim; _popupShape.StrokeThickness = .8f;
        _popupFace.Shapes.Add(_popupShape); _popupInk = new(host, _popup);
        _popup.Opacity = 0; Visual.Opacity = 0;
    }

    public void Present(HomePresentationLayout layout, bool visible, bool active, bool hoveredNav, int hoveredApp, int hoveredFolder,
        int selectedFolder, HomeFolderPreview? preview, int pressed, bool reduced, bool highContrast,
        uint foreground, uint background, uint secondary, uint dpi, IReadOnlyList<string> appNames,
        IReadOnlyList<string> appGlyphs, IReadOnlyList<uint> appColors, IReadOnlyList<string> folderNames, string? notice)
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
            _popupOpen = _popupPresented = false; _popup.Opacity = 0; return;
        }
        bool immediate = initial || reduced || geometryChanged;
        _layout = layout;
        Visual.Size = new(layout.Width, layout.Height);
        _dockPlate.SetBounds(layout.DockPill.X, layout.DockPill.Y, layout.DockPill.Width, layout.DockPill.Height, 28);
        _dockPlate.SetAppearance(highContrast ? background : 0xBCF6F8FA, foreground, highContrast);
        bool dockHover = hoveredFolder >= 0 || hoveredApp >= 0;
        bool dockPressed = pressed == WidgetActions.HomeFolderFirst
            || pressed >= WidgetActions.HomeAppFirst && pressed < WidgetActions.HomeAppFirst + WidgetActions.HomeAppCount;
        _dockPlate.SetFeedback(dockHover, dockPressed, reduced);
        var navTrigger = layout.NavTrigger;
        _dockNavPlate.SetBounds(navTrigger.X, navTrigger.Y, navTrigger.Width, navTrigger.Height, navTrigger.Width / 2);
        _dockNavPlate.SetAppearance(highContrast ? background : 0xBCF6F8FA, foreground, highContrast);
        _dockNavPlate.SetFeedback(hoveredNav || active,
            pressed == WidgetActions.HomeNavToggle, reduced);
        string triggerGlyph = active ? "\uE711" : "\uE700";
        _dockTriggerIcon.SetCached($"{triggerGlyph}/{foreground}/{dpi}", new(56),
            [Glyph(triggerGlyph, 18, 18, 20, foreground)]);
        _dockTriggerIcon.Visual.Offset = new(navTrigger.X, navTrigger.Y, 0);
        _dockTriggerIcon.Visual.Size = new(56);
        var rail = layout.NavRail;
        _navigationPlate.SetBounds(rail.X, rail.Y, rail.Width, rail.Height, rail.Width / 2);
        _navigationPlate.SetAppearance(highContrast ? background : 0xBCF6F8FA, foreground, highContrast);
        _navigationPlate.Visual.IsVisible = active && rail.Height > 0;
        _navigationIcons.Visual.IsVisible = active && rail.Height > 0;
        if (active && rail.Height > 0)
        {
            string[] glyphs = ["\uE8D6", "\uE8D6", "\uE8FD", "\uE734", "\uE823"];
            var icons = new List<SurfaceOp>();
            for (int i = 0; i < glyphs.Length; i++)
            {
                var item = layout.NavRailItem(i);
                float x = item.X - rail.X, y = item.Y - rail.Y;
                if (i == 0) icons.Add(Round(x, y, item.Width, item.Height, item.Width / 2,
                    highContrast ? background : 0x99242B35));
                float glyphSize = Math.Min(20, item.Width * .55f);
                icons.Add(Glyph(glyphs[i], x + (item.Width - glyphSize) / 2,
                    y + (item.Height - glyphSize) / 2, glyphSize,
                    highContrast ? foreground : i == 0 ? 0xFFFFFFFF : 0xAAE5EAF1));
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
                ink.Add(new SurfaceOp { Kind = SurfaceOpKind.Image, Width = 44, Height = 44, Radius = 11, Image = artwork });
            else
            {
                ink.Add(Round(0, 0, 44, 44, 11, highContrast ? background : appColors[i]));
                ink.Add(Glyph(appGlyphs[i], 11, 11, 22, highContrast ? foreground : 0xFFFFFFFF));
            }
            _appArt[i].SetCached($"{i}/{_iconRevision}/{foreground}/{background}/{highContrast}/{dpi}", new(44), ink);
            _appArt[i].Visual.CenterPoint = new(22, 22, 0);
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
                [Round(0, 0, width, 22, 9, highContrast ? background : 0xF02A3441),
                 SurfaceOp.Typography(helpName, TextRole.Caption, 4, 3, width - 8, 16,
                    highContrast ? foreground : 0xFFF8FAFC, true)]);
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
            float width = layout.Width - 48;
            _notice.Visual.Offset = new(24, Math.Max(54, layout.Dock.Y - 40), 0);
            _notice.SetCached($"{notice}/{width}/{foreground}/{background}/{highContrast}/{dpi}", new(width, 26),
                [Round(0, 0, width, 26, 11, highContrast ? background : 0xF5FFFFFF),
                Glyph("\uE7BA", 8, 6, 14, highContrast ? foreground : 0xFF9B6700),
                SurfaceOp.Typography(notice!, TextRole.Caption, 30, 5, width - 38, 16, foreground)]);
        }
        PresentPopup(layout, selectedFolder, preview, pressed, secondary, dpi, immediate, paletteChanged);
    }

    void PresentPopup(HomePresentationLayout layout, int selected, HomeFolderPreview? preview,
        int pressed, uint secondary, uint dpi, bool immediate, bool paletteChanged)
    {
        bool open = selected >= 0 && preview is not null;
        if (open)
        {
            var pop = layout.Popover;
            _popup.Size = new(pop.Width, pop.Height + 8); _popup.CenterPoint = new(pop.Width / 2, pop.Height, 0);
            _popupFace.Size = new(pop.Width, pop.Height + 8);
            var key = HomePopupRenderKey.Create(layout, selected, preview!, pressed,
                _foreground, _background, secondary, dpi, _highContrast);
            if (_popupKey != key || paletteChanged)
            {
                float arrow = Math.Clamp(layout.DockFolder.X + 22 - pop.X, 24, pop.Width - 24);
                Path(_popupGeometry, PopupPath(pop.Width, pop.Height, arrow));
                Colors(_popupFill, _highContrast ? _background : 0xFAFFFFFF, _highContrast ? _background : 0xF5EEF3F7);
                Colors(_popupRim, _highContrast ? _foreground : 0xE6FFFFFF, _highContrast ? _foreground : 0x503B5066);
                _popupInk.Set(new(pop.Width, pop.Height + 8), PopupOperations(layout, preview!, secondary, pressed));
                _popupKey = key;
            }
            if (!_popupPresented)
            {
                if (!immediate) { _popup.Opacity = 0; _popup.Scale = new(.96f, .96f, 1); _popup.Offset = new(pop.X, pop.Y + 6, 0); }
                _popupPresented = true;
            }
            Move(_popup, new(pop.X, pop.Y, 0), immediate, 220);
            Scale(_popup, Vector3.One, immediate, 220);
            Scalar(_popup, "Opacity", 1, immediate, 180);
        }
        else if (_popupOpen || immediate)
        {
            Scalar(_popup, "Opacity", 0, immediate, 150);
            Scale(_popup, new(.97f, .97f, 1), immediate, 170);
            if (_layout is not null) Move(_popup, new(layout.Popover.X, layout.Popover.Y + 5, 0), immediate, 170);
            // Retain the small cached last surface during exit. It cannot receive input,
            // and is reused/replaced on the next open instead of disappearing on Down.
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
            if (pressed == WidgetActions.HomePreviewFirst + i) ops.Add(Round(x, y, rect.Width, rect.Height, 10, 0x183E607D));
            var entry = preview.Entries[i]; bool compact = rect.Height < 80;
            float iconSize = compact ? 18 : 26, iconY = compact ? 1 : 8;
            ops.Add(Glyph(entry.IsFolder ? "\uE8B7" : "\uE8A5", x + (rect.Width - iconSize) / 2, y + iconY, iconSize,
                _highContrast ? _foreground : entry.IsFolder ? 0xFFD98F07 : secondary));
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
        if (immediate) { visual.StopAnimation(property); if (property == "Offset") visual.Offset = target; else visual.Scale = target; return; }
        using var animation = _compositor.CreateVector3KeyFrameAnimation();
        using var easing = _compositor.CreateCubicBezierEasingFunction(new(.22f, 1), new(.36f, 1));
        animation.InsertExpressionKeyFrame(0, "this.StartingValue"); animation.InsertKeyFrame(1, target, easing);
        animation.Duration = TimeSpan.FromMilliseconds(duration); animation.DelayTime = TimeSpan.FromMilliseconds(delay);
        animation.DelayBehavior = AnimationDelayBehavior.SetInitialValueBeforeDelay;
        visual.StartAnimation(property, animation);
    }
    void Scalar(Visual visual, string property, float value, bool immediate, int duration)
    {
        if (!immediate && _scalarTargets.TryGetValue((visual, property), out var previous) && previous == value) return;
        _scalarTargets[(visual, property)] = value;
        if (!_animated.Contains(visual)) _animated.Add(visual);
        if (immediate)
        {
            visual.StopAnimation(property);
            if (property == "Opacity") visual.Opacity = value; else visual.RotationAngleInDegrees = value;
            return;
        }
        using var animation = _compositor.CreateScalarKeyFrameAnimation();
        using var easing = _compositor.CreateCubicBezierEasingFunction(new(.22f, 1), new(.36f, 1));
        animation.InsertExpressionKeyFrame(0, "this.StartingValue"); animation.InsertKeyFrame(1, value, easing);
        animation.Duration = TimeSpan.FromMilliseconds(duration); visual.StartAnimation(property, animation);
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
    static Vector2[] PopupPath(float w, float h, float arrow)
    {
        var p = new List<Vector2>();
        void Corner(float cx, float cy, float start)
        { for (int i = 0; i <= 10; i++) { float a = start + MathF.PI / 2 * i / 10; p.Add(new(cx + 20 * MathF.Cos(a), cy + 20 * MathF.Sin(a))); } }
        p.Add(new(20, 0)); p.Add(new(w - 20, 0)); Corner(w - 20, 20, -MathF.PI / 2);
        p.Add(new(w, h - 20)); Corner(w - 20, h - 20, 0);
        p.Add(new(arrow + 7, h)); p.Add(new(arrow, h + 7)); p.Add(new(arrow - 7, h));
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
            var replacement = _host.RenderSurface(size.X, size.Y, ops); var old = _surface;
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
            _backFill = owner.Gradient(0xFFE5A322, 0xFFD88C05); backShape.FillBrush = _backFill;
            _backEdge = owner.Ink(0); backShape.StrokeBrush = _backEdge; backShape.StrokeThickness = 1;
            back.Shapes.Add(backShape); perspective.Children.InsertAtTop(back);
            uint[] paperColors = [0xFFB6BFCA, 0xFFD5DCE3, 0xFFF1F4F7];
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
            _frontFill = owner.Gradient(0xFFFFC733, 0xFFF3A008); _frontShape.FillBrush = _frontFill;
            _frontRim = owner.Gradient(0xC0FFE49A, 0x85CB8306); _frontShape.StrokeBrush = _frontRim; _frontShape.StrokeThickness = .55f;
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
            Colors(_frontFill, high ? background : open ? 0xFFFFD04Du : 0xFFFFC733u, high ? background : 0xFFF3A008);
            Colors(_frontRim, high ? foreground : 0xC0FFE49A, high ? foreground : 0x85CB8306);
            Colors(_backFill, high ? background : 0xFFE5A322, high ? background : 0xFFD88C05);
            _backEdge.Color = ColorOf(high ? foreground : 0);
            _frontShape.StrokeThickness = high ? 1.2f : .55f;
            uint[] paper = [0xFFB6BFCA, 0xFFD5DCE3, 0xFFF1F4F7];
            for (int i = 0; i < 3; i++) { _paperInk[i].Color = ColorOf(high ? foreground : paper[i]); _shadows[i].IsVisible = !high; }
        }
    }

    public void Invalidate()
    {
        _helpKey = null; _popupKey = null; _notice.Invalidate();
        _dockTriggerIcon.Invalidate(); _help.Invalidate();
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
        foreach (var container in _containers) container.Children.RemoveAll();
        foreach (var visual in _shapeVisuals) visual.Shapes.Clear();
        _dockTriggerIcon.Dispose(); _navigationIcons.Dispose(); _help.Dispose(); _popupInk.Dispose(); _notice.Dispose();
        foreach (var layer in _appArt) layer.Dispose();
        _paths.Clear();
        for (int i = _owned.Count - 1; i >= 0; i--) _owned[i].Dispose();
    }
    void StopMotion()
    {
        _vectorTargets.Clear(); _scalarTargets.Clear();
        foreach (var visual in _animated)
            foreach (string property in new[] { "Offset", "Scale", "Opacity", "RotationAngleInDegrees" }) visual.StopAnimation(property);
    }
}


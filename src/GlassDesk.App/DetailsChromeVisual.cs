using System.Globalization;
using System.Numerics;
using GlassDesk.Core;
using GlassDesk.NativeBridge;
using Windows.UI;
using Windows.UI.Composition;
using Windows.UI.ViewManagement;

namespace GlassDesk.App;

/// <summary>
/// Details chrome: the top companion home handle (return to the time capsule) and a
/// legacy bottom five-destination dock, retained internally but hidden by the current
/// Home and Music navigation. All foreground content is a small set of
/// cached pixel surfaces; they are redrawn only when selection, hover, high contrast or
/// DPI changes. Motion is pure composition (no per-frame tick) except the transient
/// preview highlight, which returns to the real page after a short deadline.
/// </summary>
internal sealed class DetailsChromeVisual : IDisposable
{
    const float HomeEyeWidth = 5, HomeEyeHeight = 9, HomeEyeGap = 4;
    const float DockIconSize = 18, ActiveIconSize = 16, ActiveIconGap = 6;
    const float LabelHorizontalPadding = 20;
    const double SelectionDurationMs = 240, HoverDurationMs = 120, PressDurationMs = 90,
        ReleaseDurationMs = 120, PreviewHighlightMs = 1200;

    readonly NativeWidgetHost _host;
    readonly Compositor _compositor;
    readonly AccessibilitySettings _accessibility = new();
    readonly UISettings _uiSettings = new();

    readonly ContainerVisual _visual;
    readonly ContainerVisual _homeGroup;
    readonly ContainerVisual _homeGaze;
    Vector3 _homeGazeTarget;
    readonly ShapeVisual _homeBody;
    readonly CompositionRoundedRectangleGeometry _homeBodyGeometry;
    readonly CompositionColorBrush _homeBodyInk;
    readonly ShapeVisual[] _homeEyes = new ShapeVisual[2];
    readonly CompositionRoundedRectangleGeometry[] _homeEyeGeometry = new CompositionRoundedRectangleGeometry[2];
    readonly CompositionColorBrush _homeEyeInk;
    readonly SpriteVisual _homeLabel;
    readonly CompositionSurfaceBrush _homeLabelBrush;
    CompositionDrawingSurface? _homeLabelSurface;
    (bool HighContrast, uint Dpi, float Width, string Text)? _homeLabelKey;
    bool? _homeLabelShown;

    readonly SpriteVisual _dock;
    readonly CompositionSurfaceBrush _dockBrush;
    CompositionDrawingSurface? _dockSurface;
    (bool HighContrast, uint Dpi, bool LightInk, float Tint)? _dockKey;

    readonly SpriteVisual _dockIcons;
    readonly CompositionSurfaceBrush _dockIconsBrush;
    CompositionDrawingSurface? _dockIconsSurface;
    SystemGlassInkVisual? _adaptiveDockInk;
    (bool HighContrast, uint Dpi, bool LightInk, int Index)? _dockIconsKey;

    readonly SpriteVisual _dockActive;
    readonly CompositionSurfaceBrush _dockActiveBrush;
    CompositionDrawingSurface? _dockActiveSurface;
    (bool HighContrast, uint Dpi, bool LightInk, int Index)? _dockActiveKey;
    float? _dockActiveTargetX;

    readonly SpriteVisual _press;
    readonly CompositionSurfaceBrush _pressBrush;
    CompositionDrawingSurface? _pressSurface;
    bool? _pressKey;

    readonly SpriteVisual _dockLabel;
    readonly CompositionSurfaceBrush _dockLabelBrush;
    CompositionDrawingSurface? _dockLabelSurface;
    (bool HighContrast, uint Dpi, float Width, string Text)? _dockLabelKey;
    bool? _dockLabelShown;

    string _pageId = DetailsPageController.Music;
    int _selectedIndex = DetailsDockItems.Music;
    int _previewIndex = -1;
    double _previewUntil;
    int _hoverIndex = -1;
    bool _hoverHome, _pressedHome, _active, _reducedMotion, _disposed;
    bool _lightInk;
    float _materialTintOpacity;
    // The current Home Dock replaces the legacy five-slot dock. Keep all of its routes
    // hidden from rendering, hit-testing, and accessibility.
    bool _dockVisible = true;
    uint _homeColor = 0xff202329;
    int _pressedIndex = -1;
    float _width = 360, _height = 476;
    bool _suppressSelectionAnimation;
    bool? _paletteHighContrast;
    bool _paletteLightInk;
    uint _paletteHomeColor;

    public ContainerVisual Visual => _visual;
    public bool NeedsTick => _previewIndex >= 0;
    public event Action<string>? PageRequested;
    public FloatRect HomeBounds => DetailsChromeLayout.Home(Size);
    public FloatRect DockBounds => DetailsChromeLayout.Dock(Size);
    Vector2 Size => new(_width, _height);
    bool Reduced => _reducedMotion || !_uiSettings.AnimationsEnabled;
    int SelectionIndex => _previewIndex >= 0 ? _previewIndex : _selectedIndex;
    static string Locale => CultureInfo.CurrentUICulture.Name;

    public DetailsChromeVisual(NativeWidgetHost host)
    {
        _host = host;
        _compositor = host.Compositor;
        _visual = _compositor.CreateContainerVisual();
        _visual.Size = Size;
        _visual.Opacity = 0;

        _homeGroup = _compositor.CreateContainerVisual();
        _homeGroup.Size = new(DetailsChromeLayout.HomeWidth, DetailsChromeLayout.HomeHeight);
        _homeGroup.CenterPoint = new(DetailsChromeLayout.HomeWidth / 2, DetailsChromeLayout.HomeHeight / 2, 0);
        _homeBody = _compositor.CreateShapeVisual();
        _homeBody.Size = new(DetailsChromeLayout.HomeWidth, DetailsChromeLayout.HomeHeight);
        _homeBodyGeometry = _compositor.CreateRoundedRectangleGeometry();
        _homeBodyGeometry.Size = new(DetailsChromeLayout.HomeWidth, DetailsChromeLayout.HomeHeight);
        _homeBodyGeometry.CornerRadius = new(DetailsChromeLayout.HomeRadius);
        _homeBodyInk = _compositor.CreateColorBrush(Color.FromArgb(255, 32, 35, 40));
        AppendShape(_homeBody, _homeBodyGeometry, _homeBodyInk);
        _homeGroup.Children.InsertAtTop(_homeBody);
        _homeGaze = _compositor.CreateContainerVisual();
        _homeGaze.Size = _homeGroup.Size;
        _homeGroup.Children.InsertAtTop(_homeGaze);
        _homeEyeInk = _compositor.CreateColorBrush(Color.FromArgb(255, 250, 251, 252));
        for (int index = 0; index < _homeEyes.Length; index++)
        {
            var eye = _compositor.CreateShapeVisual();
            eye.Size = new(HomeEyeWidth, HomeEyeHeight);
            _homeEyeGeometry[index] = _compositor.CreateRoundedRectangleGeometry();
            _homeEyeGeometry[index].Size = new(HomeEyeWidth, HomeEyeHeight);
            _homeEyeGeometry[index].CornerRadius = new(HomeEyeWidth / 2);
            AppendShape(eye, _homeEyeGeometry[index], _homeEyeInk);
            eye.Offset = new(index == 0
                ? DetailsChromeLayout.HomeWidth / 2 - HomeEyeGap / 2 - HomeEyeWidth
                : DetailsChromeLayout.HomeWidth / 2 + HomeEyeGap / 2,
                (DetailsChromeLayout.HomeHeight - HomeEyeHeight) / 2, 0);
            _homeGaze.Children.InsertAtTop(eye);
            _homeEyes[index] = eye;
        }
        _visual.Children.InsertAtTop(_homeGroup);

        (_homeLabel, _homeLabelBrush) = CreateSurfaceSprite();
        _homeLabel.Opacity = 0;
        _visual.Children.InsertAtTop(_homeLabel);

        (_dock, _dockBrush) = CreateSurfaceSprite();
        _dock.Size = new(DetailsChromeLayout.DockWidth, DetailsChromeLayout.DockHeight);
        _visual.Children.InsertAtTop(_dock);

        (_dockIcons, _dockIconsBrush) = CreateSurfaceSprite();
        _dockIcons.Size = new(DetailsChromeLayout.DockWidth, DetailsChromeLayout.DockHeight);
        _visual.Children.InsertAtTop(_dockIcons);

        (_press, _pressBrush) = CreateSurfaceSprite();
        _press.Size = new(DetailsChromeLayout.DockItemHitSize, DetailsChromeLayout.DockItemHitSize);
        _press.Opacity = 0;
        _visual.Children.InsertAtTop(_press);

        (_dockActive, _dockActiveBrush) = CreateSurfaceSprite();
        _dockActive.Size = new(DetailsChromeLayout.DockActiveWidth, DetailsChromeLayout.DockActiveHeight);
        _dockActive.CenterPoint = new(DetailsChromeLayout.DockActiveWidth / 2, DetailsChromeLayout.DockActiveHeight / 2, 0);
        _visual.Children.InsertAtTop(_dockActive);

        (_dockLabel, _dockLabelBrush) = CreateSurfaceSprite();
        _dockLabel.Opacity = 0;
        _visual.Children.InsertAtTop(_dockLabel);

        Present();
    }

    void AppendShape(ShapeVisual target, CompositionGeometry geometry, CompositionColorBrush fill)
    {
        var shape = _compositor.CreateSpriteShape(geometry);
        shape.FillBrush = fill;
        target.Shapes.Add(shape);
    }

    (SpriteVisual Visual, CompositionSurfaceBrush Brush) CreateSurfaceSprite()
    {
        var sprite = _compositor.CreateSpriteVisual();
        var brush = _compositor.CreateSurfaceBrush();
        brush.Stretch = CompositionStretch.Fill;
        sprite.Brush = brush;
        return (sprite, brush);
    }

    public void SetLayout(float width, float height)
    {
        if (_disposed) return;
        _width = float.IsFinite(width) ? Math.Max(288, width) : DetailsChromeLayout.CardWidth;
        _height = float.IsFinite(height) ? Math.Max(160, height) : 476;
        Vector2 size = Size;
        if (_visual.Size != size) _visual.Size = size;
        Present();
    }

    public void BeginOpen(long sequence, double now, bool reducedMotion)
    {
        if (_disposed) return;
        _ = sequence; _ = now;
        _reducedMotion = reducedMotion;
        _active = true;
        // SetWidgetBoard normally publishes the real page before the open; re-derive the
        // slot here so the capsule can never start on a stale destination.
        _selectedIndex = DetailsDockItems.IndexOf(_pageId);
        _hoverHome = false; _hoverIndex = -1; _previewIndex = -1;
        _pressedHome = false; _pressedIndex = -1; _press.Opacity = 0;
        _homeGroup.StopAnimation("Scale"); _homeGroup.Scale = Vector3.One;
        _dockActive.StopAnimation("Scale"); _dockActive.Scale = Vector3.One;
        _dockActive.StopAnimation("Offset.X");
        _suppressSelectionAnimation = true;
        Present();
        _suppressSelectionAnimation = false;
    }

    public void Close()
    {
        if (_disposed) return;
        _active = false; _hoverHome = false; _hoverIndex = -1; _previewIndex = -1;
        SetHomeGaze(0, 0, false);
        _pressedHome = false; _pressedIndex = -1;
        _homeLabel.StopAnimation("Opacity"); _homeLabel.Opacity = 0; _homeLabelShown = null;
        _dockLabel.StopAnimation("Opacity"); _dockLabel.Opacity = 0; _dockLabelShown = null;
        _dockActive.StopAnimation("Offset.X"); _dockActive.StopAnimation("Scale"); _dockActive.Scale = Vector3.One;
        _homeGroup.StopAnimation("Scale"); _homeGroup.Scale = Vector3.One;
        _press.Opacity = 0;
        _visual.Opacity = 0;
    }

    public void SetReducedMotion(bool reducedMotion, double now)
    {
        if (_disposed) return;
        bool reduced = reducedMotion || !_uiSettings.AnimationsEnabled;
        if (_reducedMotion == reduced) return;
        _ = now;
        _reducedMotion = reduced;
        if (reduced)
        {
            SetHomeGaze(0, 0, false);
            _dockActive.StopAnimation("Offset.X");
            _dockActive.StopAnimation("Scale");
            _dockActive.Scale = Vector3.One;
            _dockActiveTargetX = null;
        }
        Present();
    }

    /// <summary>The two eyes move together; the capsule and its hit target stay still.</summary>
    public void SetHomeGaze(float x, float y, bool enabled)
    {
        if (_disposed) return;
        var home = HomeBounds;
        var target = enabled && !Reduced
            ? new Vector3(Math.Clamp((x - home.X - home.Width / 2) / 65, -2.5f, 2.5f),
                Math.Clamp((y - home.Y - home.Height / 2) / 80, -2, 2), 0)
            : Vector3.Zero;
        if (Reduced) { _homeGaze.StopAnimation("Offset"); _homeGaze.Offset = target; _homeGazeTarget = target; return; }
        if (Vector3.DistanceSquared(target, _homeGazeTarget) < .0025f) return;
        _homeGazeTarget = target;
        using var easing = _compositor.CreateCubicBezierEasingFunction(new(.22f, 1), new(.36f, 1));
        using var motion = _compositor.CreateVector3KeyFrameAnimation();
        motion.InsertExpressionKeyFrame(0, "this.StartingValue");
        motion.InsertKeyFrame(1, target, easing);
        motion.Duration = TimeSpan.FromMilliseconds(200);
        _homeGaze.StartAnimation("Offset", motion);
    }

    /// <summary>Follows the material: light ink means the glass is dark and the dock uses a light film.</summary>
    public void SetLightInk(bool lightInk)
    {
        if (_disposed || _lightInk == lightInk) return;
        _lightInk = lightInk;
        Invalidate();
        Present();
    }

    public void SetMaterialTint(float tint)
    {
        if (_disposed) return;
        float normalized = float.IsFinite(tint) ? Math.Clamp(tint, 0, 1) : 0;
        if (_materialTintOpacity == normalized) return;
        _materialTintOpacity = normalized;
        _dockKey = null;
        Present();
    }

    /// <summary>The top handle is the companion, so it adopts the user's companion colour.</summary>
    public void SetCompanionColor(uint color)
    {
        if (_disposed) return;
        uint normalized = color | 0xff000000u;
        if (normalized == _homeColor) return;
        _homeColor = normalized;
        Invalidate();
        Present();
    }

    public void Invalidate()
    {
        if (_disposed) return;
        _dockKey = null; _dockIconsKey = null; _dockActiveKey = null;
        _homeLabelKey = null; _dockLabelKey = null; _pressKey = null;
    }

    public void PointerMoved(float x, float y, double now)
    {
        if (_disposed || !_active) return;
        _ = now;
        bool homeHover = DetailsChromeLayout.HomeHit(Size).Contains(x, y);
        int hover = -1;
        if (!homeHover && _dockVisible)
            for (int index = 0; index < DetailsDockItems.All.Count; index++)
                if (DetailsChromeLayout.DockItem(Size, index).Contains(x, y)) { hover = index; break; }
        if (homeHover == _hoverHome && hover == _hoverIndex) return;
        _hoverHome = homeHover;
        _hoverIndex = hover;
        Present();
    }

    public void PointerExited(double now)
    {
        if (_disposed || (!_hoverHome && _hoverIndex < 0)) return;
        _ = now;
        _hoverHome = false; _hoverIndex = -1;
        Present();
    }

    /// <summary>Returns <see cref="WidgetActions.Close"/> for the home handle or a dock action ID.</summary>
    public int HitTest(float x, float y)
    {
        if (_disposed || !_active) return -1;
        if (DetailsChromeLayout.HomeHit(Size).Contains(x, y)) return WidgetActions.Close;
        if (!_dockVisible) return -1;
        for (int index = 0; index < DetailsDockItems.All.Count; index++)
            if (DetailsChromeLayout.DockItem(Size, index).Contains(x, y)) return WidgetActions.DockFirst + index;
        return -1;
    }

    public bool Activate(int actionId, double now)
    {
        if (_disposed || !_active) return false;
        // The home handle is routed through WidgetController, which owns form changes.
        if (actionId == WidgetActions.Close) return false;
        int index = actionId - WidgetActions.DockFirst;
        if (index is < 0 or > 4) return false;
        if (!_dockVisible) return false;
        _ = now;
        var entry = DetailsDockItems.All[index];
        if (entry.IsPage)
        {
            if (entry.Id != _pageId) PageRequested?.Invoke(entry.Id);
            return true;
        }
        // A preview destination never switches the real page; it briefly highlights the
        // item and then settles back on the page that is actually shown.
        _previewIndex = index;
        _previewUntil = now + PreviewHighlightMs;
        PresentSelection();
        return true;
    }

    public void SetCurrentPage(string? pageId)
    {
        if (_disposed) return;
        string normalized = DetailsPageController.Normalize(pageId);
        if (normalized == _pageId) return;
        _pageId = normalized;
        _selectedIndex = DetailsDockItems.IndexOf(normalized);
        _previewIndex = -1;
        PresentSelection();
    }

    /// <summary>Shows or hides the legacy five-slot dock without touching the home handle.</summary>
    public void SetDockVisible(bool visible)
    {
        if (_disposed || _dockVisible == visible) return;
        _dockVisible = visible;
        _dock.IsVisible = visible;
        _dockIcons.IsVisible = visible;
        _dockActive.IsVisible = visible;
        _dockLabel.IsVisible = visible;
        _press.IsVisible = visible;
        if (visible) return;
        _hoverIndex = -1;
        _pressedIndex = -1;
        _dockLabel.Opacity = 0;
        _press.Opacity = 0;
        Present();
    }

    public void Tick(double now)
    {
        if (_disposed || _previewIndex < 0 || now < _previewUntil) return;
        _previewIndex = -1;
        PresentSelection();
    }

    /// <summary>Pointer pressed feedback chosen by WidgetScene from the active hit control.</summary>
    public void SetPointerFeedback(int actionId, bool pressed, double now)
    {
        if (_disposed || !_active) return;
        _ = now;
        if (!pressed)
        {
            if (_pressedHome) { _pressedHome = false; AnimateScale(_homeGroup, 1, ReleaseDurationMs); }
            if (_pressedIndex >= 0)
            {
                int index = _pressedIndex;
                _pressedIndex = -1;
                _press.Opacity = 0;
                if (index == SelectionIndex) AnimateScale(_dockActive, 1, ReleaseDurationMs);
            }
            return;
        }
        if (actionId == WidgetActions.Close)
        {
            _pressedHome = true;
            AnimateScale(_homeGroup, .95f, PressDurationMs);
            return;
        }
        int item = actionId - WidgetActions.DockFirst;
        if (item is < 0 or > 4) return;
        _pressedIndex = item;
        PositionPress(item);
        _press.Opacity = 1;
        if (item == SelectionIndex) AnimateScale(_dockActive, .97f, PressDurationMs);
    }

    public IReadOnlyList<AccessibleControl> GetAccessibleControls()
    {
        if (_disposed || !_active) return Array.Empty<AccessibleControl>();
        var controls = new List<AccessibleControl>(1 + DetailsDockItems.All.Count);
        FloatRect home = DetailsChromeLayout.HomeHit(Size);
        controls.Add(new(WidgetActions.Close, "收起为胶囊（陪伴）", home.X, home.Y, home.Width, home.Height));
        if (!_dockVisible) return controls;
        for (int index = 0; index < DetailsDockItems.All.Count; index++)
        {
            var entry = DetailsDockItems.All[index];
            FloatRect item = DetailsChromeLayout.DockItem(Size, index);
            string name = entry.IsPage
                ? entry.Label + (entry.Id == _pageId ? "，当前页面" : "")
                : entry.Label + "（预览）";
            controls.Add(new(entry.ActionId, name, item.X, item.Y, item.Width, item.Height));
        }
        return controls;
    }

    void PresentSelection()
    {
        if (_disposed || !_active) return;
        RefreshSelection(_accessibility.HighContrast, _host.Context.Dpi);
    }

    void Present()
    {
        if (_disposed) return;
        bool highContrast = _accessibility.HighContrast;
        uint dpi = _host.Context.Dpi;
        Vector2 size = Size;
        bool paletteChanged = _paletteHighContrast != highContrast || _paletteLightInk != _lightInk || _paletteHomeColor != _homeColor;
        if (paletteChanged)
        {
            _paletteHighContrast = highContrast;
            _paletteLightInk = _lightInk;
            _paletteHomeColor = _homeColor;
            Color body = highContrast ? _uiSettings.UIElementColor(UIElementType.Window) : ToColor(_homeColor);
            _homeBodyInk.Color = body;
            _homeEyeInk.Color = highContrast ? _uiSettings.UIElementColor(UIElementType.WindowText) : ReadableInk(body);
        }
        FloatRect home = DetailsChromeLayout.Home(size);
        _homeGroup.Offset = new(home.X, home.Y, 0);
        // No text chrome: the home handle and dock communicate by shape alone.
        _homeLabel.Opacity = 0;
        _dockLabel.Opacity = 0;
        RefreshDockSurface(highContrast, dpi);
        RefreshDockIcons(highContrast, dpi);
        RefreshSelection(highContrast, dpi);
        FloatRect dock = DetailsChromeLayout.Dock(size);
        _dock.Offset = new(dock.X, dock.Y, 0);
        _dockIcons.Offset = new(dock.X, dock.Y, 0);
        if (_pressedIndex >= 0) PositionPress(_pressedIndex);
    }

    void RefreshDockSurface(bool highContrast, uint dpi)
    {
        var key = (highContrast, dpi, _lightInk, _materialTintOpacity);
        if (_dockKey == key) return;
        _dockKey = key;
        var operations = new List<SurfaceOp>(2);
        // One material family with the card: a translucent film, never a black bar.
        uint fill = highContrast ? Argb(_uiSettings.UIElementColor(UIElementType.Window))
            : _lightInk ? 0x1CFFFFFFu : 0x1420252Cu;
        // Manual foreground ink does not require an opaque contrast platform.
        // Keep the existing thin navigation film and the system high-contrast
        // fallback, rather than a second readability cloud over the main glass.
        operations.Add(Round(0, 0, DetailsChromeLayout.DockWidth, DetailsChromeLayout.DockHeight,
            DetailsChromeLayout.DockRadius, fill));
        if (!highContrast)
        {
            uint rim = _lightInk ? 0x26FFFFFFu : 0x1AFFFFFFu;
            operations.Add(Line(DetailsChromeLayout.DockRadius, .5f,
                DetailsChromeLayout.DockWidth - DetailsChromeLayout.DockRadius * 2, 0, rim, .75f));
        }
        Replace(ref _dockSurface, _dockBrush,
            _host.RenderSurface(DetailsChromeLayout.DockWidth, DetailsChromeLayout.DockHeight, operations));
    }

    void RefreshDockIcons(bool highContrast, uint dpi)
    {
        var key = (highContrast, dpi, _lightInk, SelectionIndex);
        if (_dockIconsKey == key) return;
        _dockIconsKey = key;
        var operations = new List<SurfaceOp>(DetailsDockItems.All.Count);
        for (int index = 0; index < DetailsDockItems.All.Count; index++)
        {
            if (index == SelectionIndex) continue; // the active icon is painted on the selection tile
            var entry = DetailsDockItems.All[index];
            uint color = highContrast
                ? Argb(_uiSettings.UIElementColor(UIElementType.WindowText))
                : entry.IsPage
                    ? _lightInk ? 0xC6F8FAFCu : 0xB020252Cu
                    : _lightInk ? 0x5CFFFFFFu : 0x5520252Cu;
            if (!highContrast && _host.IsSystemCompositorCandidate && entry.IsPage)
                color = TypographyCatalog.Ink(InkRole.Primary, _lightInk);
            float center = DetailsChromeLayout.DockItemWidth * (index + .5f);
            operations.Add(IconOp(GlyphFor(entry.Id), center - DockIconSize / 2,
                (DetailsChromeLayout.DockHeight - DockIconSize) / 2, DockIconSize, color));
        }
        _adaptiveDockInk?.Dispose();
        Replace(ref _dockIconsSurface, _dockIconsBrush,
            _host.RenderSurface(DetailsChromeLayout.DockWidth, DetailsChromeLayout.DockHeight, operations));
    }

    void RefreshSelection(bool highContrast, uint dpi)
    {
        if (!_active) return;
        int index = SelectionIndex;
        FloatRect target = DetailsChromeLayout.DockActive(Size, index);
        var key = (highContrast, dpi, _lightInk, index);
        if (_dockActiveKey != key)
        {
            _dockActiveKey = key;
            var entry = DetailsDockItems.All[index];
            uint fill = highContrast ? Argb(_uiSettings.UIElementColor(UIElementType.Highlight))
                : _host.IsSystemCompositorCandidate ? _lightInk ? 0x26FFFFFFu : 0x1820252Cu : _lightInk ? 0x3AFFFFFFu : 0x2620252Cu;
            uint ink = highContrast ? Argb(_uiSettings.UIElementColor(UIElementType.HighlightText))
                : _lightInk ? 0xFFF8FAFCu : 0xFF20252Cu;
            var operations = new List<SurfaceOp>(2)
            {
                Round(0, 0, target.Width, target.Height, DetailsChromeLayout.DockActiveRadius, fill)
            };
            operations.Add(IconOp(GlyphFor(entry.Id), (target.Width - ActiveIconSize) / 2,
                (target.Height - ActiveIconSize) / 2, ActiveIconSize, ink));
            Replace(ref _dockActiveSurface, _dockActiveBrush,
                _host.RenderSurface(target.Width, target.Height, operations));
        }
        _dockActive.Size = new(target.Width, target.Height);
        MoveSelectionTo(target);
    }

    void MoveSelectionTo(FloatRect target)
    {
        if (!_active || Reduced || _suppressSelectionAnimation)
        {
            _dockActive.StopAnimation("Offset.X");
            _dockActive.Offset = new(target.X, target.Y, 0);
            _dockActiveTargetX = target.X;
            return;
        }
        if (_dockActiveTargetX is float prior)
        {
            if (Math.Abs(prior - target.X) < .01f)
            {
                if (Math.Abs(_dockActive.Offset.Y - target.Y) > .01f)
                    _dockActive.Offset = new(prior, target.Y, 0);
                return;
            }
            using var easing = _compositor.CreateCubicBezierEasingFunction(new(.22f, 1), new(.36f, 1));
            using var animation = _compositor.CreateScalarKeyFrameAnimation();
            // Interrupt from the presented position, not the previous destination.
            // One small, bounded settle keeps repeated selections continuous.
            animation.InsertExpressionKeyFrame(0, "this.StartingValue");
            animation.InsertKeyFrame(.82f, target.X + MathF.CopySign(1.5f, target.X - prior), easing);
            animation.InsertKeyFrame(1, target.X, easing);
            animation.Duration = TimeSpan.FromMilliseconds(SelectionDurationMs);
            _dockActive.Offset = new(target.X, target.Y, 0);
            _dockActive.StartAnimation("Offset.X", animation);
        }
        else _dockActive.Offset = new(target.X, target.Y, 0);
        _dockActiveTargetX = target.X;
    }

    void RefreshHomeLabel(bool highContrast, uint dpi)
    {
        const string text = "回首页";
        float width = LabelWidth(text);
        var key = (highContrast, dpi, width, text);
        if (_homeLabelKey != key)
        {
            _homeLabelKey = key;
            FloatRect bounds = DetailsChromeLayout.HomeLabel(Size, width);
            _homeLabel.Size = new(bounds.Width, bounds.Height);
            var operations = new List<SurfaceOp>(2)
            {
                Round(0, 0, bounds.Width, bounds.Height, DetailsChromeLayout.LabelRadius, LabelFill(highContrast))
            };
            operations.Add(TextOp(text, 0, 0, bounds.Width, bounds.Height, LabelInk(highContrast)));
            Replace(ref _homeLabelSurface, _homeLabelBrush,
                _host.RenderSurface(bounds.Width, bounds.Height, operations));
        }
        FloatRect placement = DetailsChromeLayout.HomeLabel(Size, width);
        _homeLabel.Offset = new(placement.X, placement.Y, 0);
        Fade(_homeLabel, ref _homeLabelShown, _active && _hoverHome);
    }

    void RefreshDockLabel(bool highContrast, uint dpi)
    {
        bool visible = _active && _hoverIndex >= 0 && !_hoverHome;
        if (!visible)
        {
            Fade(_dockLabel, ref _dockLabelShown, false);
            return;
        }
        var entry = DetailsDockItems.All[_hoverIndex];
        string text = entry.IsPage ? entry.Label : entry.Label + "（预览）";
        float width = LabelWidth(text);
        var key = (highContrast, dpi, width, text);
        if (_dockLabelKey != key)
        {
            _dockLabelKey = key;
            FloatRect bounds = DetailsChromeLayout.DockLabel(Size, _hoverIndex, width);
            _dockLabel.Size = new(bounds.Width, bounds.Height);
            var operations = new List<SurfaceOp>(2)
            {
                Round(0, 0, bounds.Width, bounds.Height, DetailsChromeLayout.LabelRadius, LabelFill(highContrast))
            };
            operations.Add(TextOp(text, 0, 0, bounds.Width, bounds.Height, LabelInk(highContrast)));
            Replace(ref _dockLabelSurface, _dockLabelBrush,
                _host.RenderSurface(bounds.Width, bounds.Height, operations));
        }
        FloatRect placement = DetailsChromeLayout.DockLabel(Size, _hoverIndex, width);
        _dockLabel.Offset = new(placement.X, placement.Y, 0);
        Fade(_dockLabel, ref _dockLabelShown, true);
    }

    void PositionPress(int index)
    {
        FloatRect dock = DetailsChromeLayout.Dock(Size);
        float center = DetailsChromeLayout.DockItemCenter(Size, index);
        _press.Offset = new(center - DetailsChromeLayout.DockItemHitSize / 2,
            dock.Y + (DetailsChromeLayout.DockHeight - DetailsChromeLayout.DockItemHitSize) / 2, 0);
        bool highContrast = _accessibility.HighContrast;
        if (_pressKey != highContrast)
        {
            _pressKey = highContrast;
            uint color = highContrast ? 0x33000000u : 0x24FFFFFFu;
            var operations = new List<SurfaceOp>(1)
            {
                Round(0, 0, DetailsChromeLayout.DockItemHitSize, DetailsChromeLayout.DockItemHitSize,
                    DetailsChromeLayout.DockActiveRadius, color)
            };
            Replace(ref _pressSurface, _pressBrush, _host.RenderSurface(
                DetailsChromeLayout.DockItemHitSize, DetailsChromeLayout.DockItemHitSize, operations));
        }
    }

    void Fade(SpriteVisual visual, ref bool? shown, bool visible)
    {
        if (shown == visible) return;
        shown = visible;
        float target = visible ? 1 : 0;
        if (Reduced)
        {
            visual.StopAnimation("Opacity");
            visual.Opacity = target;
            return;
        }
        using var easing = _compositor.CreateCubicBezierEasingFunction(new(.2f, 0), new(0, 1));
        using var animation = _compositor.CreateScalarKeyFrameAnimation();
        animation.InsertExpressionKeyFrame(0, "this.StartingValue");
        animation.InsertKeyFrame(1, target, easing);
        animation.Duration = TimeSpan.FromMilliseconds(HoverDurationMs);
        visual.StartAnimation("Opacity", animation);
    }

    void AnimateScale(Visual visual, float target, double durationMs)
    {
        if (Reduced)
        {
            visual.StopAnimation("Scale");
            visual.Scale = new(target, target, 1);
            return;
        }
        using var easing = _compositor.CreateCubicBezierEasingFunction(new(.2f, 0), new(0, 1));
        using var animation = _compositor.CreateVector3KeyFrameAnimation();
        animation.InsertExpressionKeyFrame(0, "this.StartingValue");
        animation.InsertKeyFrame(1, new Vector3(target, target, 1), easing);
        animation.Duration = TimeSpan.FromMilliseconds(durationMs);
        visual.StartAnimation("Scale", animation);
    }

    float LabelWidth(string text) =>
        Math.Clamp(MathF.Ceiling(MeasureTextWidth(text, TextRole.NavigationSemantic)) + LabelHorizontalPadding, 40, 260);

    float MeasureTextWidth(string text, TextRole role)
    {
        var operation = SurfaceOp.Typography(text, role, 0, 0, 512, 20, 0xFF000000, false, null, Locale);
        return _host.MeasureText(operation).Advance;
    }

    uint LabelFill(bool highContrast) =>
        highContrast ? Argb(_uiSettings.UIElementColor(UIElementType.Window)) : 0xFFF8FAFC;

    uint LabelInk(bool highContrast) =>
        highContrast ? Argb(_uiSettings.UIElementColor(UIElementType.WindowText)) : 0xFF20252C;

    static string GlyphFor(string id) => id switch
    {
        "music" => "\uE8D6",
        "widgets" => "\uE71D",
        "clock" => "\uE917",
        "focus" => "\uE916",
        _ => "\uE70B"
    };

    static SurfaceOp IconOp(string glyph, float x, float y, float size, uint color) => new()
    {
        Kind = SurfaceOpKind.Icon, Text = glyph, X = x, Y = y, Width = size, Height = size,
        FontSize = size, Color = color, Center = true
    };

    static SurfaceOp TextOp(string text, float x, float y, float width, float height, uint color) =>
        SurfaceOp.Typography(text, TextRole.NavigationSemantic, x, y, width, height, color, center: true, locale: Locale);

    static SurfaceOp Round(float x, float y, float width, float height, float radius, uint color) => new()
    {
        Kind = SurfaceOpKind.RoundedRectangle, X = x, Y = y, Width = width, Height = height, Radius = radius, Color = color
    };

    static SurfaceOp Line(float x, float y, float width, float height, uint color, float stroke) => new()
    {
        Kind = SurfaceOpKind.Line, X = x, Y = y, Width = width, Height = height, Radius = stroke, Color = color
    };

    static uint Argb(Color color) => (uint)color.A << 24 | (uint)color.R << 16 | (uint)color.G << 8 | color.B;
    static Color ToColor(uint c) => Color.FromArgb(255, (byte)(c >> 16), (byte)(c >> 8), (byte)c);
    static Color ReadableInk(Color body)
    {
        double r = body.R / 255d, g = body.G / 255d, b = body.B / 255d;
        return .2126 * r * r + .7152 * g * g + .0722 * b * b > .55
            ? Color.FromArgb(255, 32, 37, 44) : Color.FromArgb(255, 250, 251, 252);
    }

    static void Replace(ref CompositionDrawingSurface? owned, CompositionSurfaceBrush brush,
        CompositionDrawingSurface replacement)
    {
        var old = owned;
        brush.Surface = replacement;
        owned = replacement;
        old?.Dispose();
    }

    public void ApplySystemInk(bool enabled)
    {
        if(!_host.IsSystemCompositorCandidate)return;
        _adaptiveDockInk??=new(_host,_dockIcons,_dockIconsBrush);
        var offset=_visual.Offset+_dockIcons.Offset;
        _adaptiveDockInk.Update(_dockIconsSurface,new(offset.X,offset.Y),_dockIcons.Size,
            enabled&&_dockVisible&&!_accessibility.HighContrast);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _adaptiveDockInk?.Dispose();_adaptiveDockInk=null;
        PageRequested = null;
        _visual.Children.RemoveAll();
        _homeLabelBrush.Surface = _dockBrush.Surface = _dockIconsBrush.Surface = _dockActiveBrush.Surface
            = _pressBrush.Surface = _dockLabelBrush.Surface = null;
        _homeLabelSurface?.Dispose(); _dockSurface?.Dispose(); _dockIconsSurface?.Dispose();
        _dockActiveSurface?.Dispose(); _pressSurface?.Dispose(); _dockLabelSurface?.Dispose();
        _homeLabelBrush.Dispose(); _dockBrush.Dispose(); _dockIconsBrush.Dispose(); _dockActiveBrush.Dispose();
        _pressBrush.Dispose(); _dockLabelBrush.Dispose();
        _homeLabel.Dispose(); _dock.Dispose(); _dockIcons.Dispose(); _dockActive.Dispose();
        _press.Dispose(); _dockLabel.Dispose();
        foreach (var eye in _homeEyes)
        {
            foreach (var shape in eye.Shapes) shape.Dispose();
            eye.Shapes.Clear();
            eye.Dispose();
        }
        foreach (var geometry in _homeEyeGeometry) geometry.Dispose();
        foreach (var shape in _homeBody.Shapes) shape.Dispose();
        _homeBody.Shapes.Clear();
        _homeBody.Dispose(); _homeBodyGeometry.Dispose(); _homeBodyInk.Dispose(); _homeEyeInk.Dispose();
        _homeGaze.StopAnimation("Offset"); _homeGaze.Children.RemoveAll(); _homeGaze.Dispose();
        _homeGroup.Dispose();
        _visual.Dispose();
    }
}

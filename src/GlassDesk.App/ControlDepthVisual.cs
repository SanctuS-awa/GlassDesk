using System.Numerics;
using Windows.UI;
using Windows.UI.Composition;

namespace GlassDesk.App;

internal enum ControlDepthKind { Plate, InsetTrack, Thumb, SoftBar }

/// <summary>
/// Clear, compositor-only depth beneath foreground ink. Owns no input, surface,
/// capture, timer, or layout state; the caller retains the original hit bounds.
/// </summary>
internal sealed class ControlDepthVisual : IDisposable
{
    readonly Compositor _compositor;
    readonly ControlDepthKind _kind;
    readonly ContainerVisual _relief;
    readonly ShapeVisual _face;
    readonly CompositionRoundedRectangleGeometry _geometry;
    readonly CompositionSpriteShape _shape;
    readonly CompositionLinearGradientBrush _fill, _rim;
    readonly CompositionColorGradientStop _fillTop, _fillBottom, _rimTop, _rimBottom;
    readonly ShapeVisual _contact;
    readonly CompositionRoundedRectangleGeometry[] _contactGeometry = new CompositionRoundedRectangleGeometry[3];
    readonly CompositionSpriteShape[] _contactShapes = new CompositionSpriteShape[3];
    readonly CompositionColorBrush[] _contactBrushes = new CompositionColorBrush[3];
    Vector2 _size;
    float _radius;
    uint _fillArgb, _inkArgb;
    bool _opticalGlass;
    bool _appearanceSet, _highContrast, _hovered, _pressed, _reducedMotion, _feedbackSet, _disposed;

    public ContainerVisual Visual { get; }

    public ControlDepthVisual(Compositor compositor, ControlDepthKind kind = ControlDepthKind.Plate)
    {
        _compositor = compositor; _kind = kind;
        Visual = compositor.CreateContainerVisual();
        _relief = compositor.CreateContainerVisual();
        _contact = compositor.CreateShapeVisual();
        _face = compositor.CreateShapeVisual();
        _geometry = compositor.CreateRoundedRectangleGeometry();
        _shape = compositor.CreateSpriteShape(_geometry);
        _fill = compositor.CreateLinearGradientBrush();
        _rim = compositor.CreateLinearGradientBrush();
        _fill.StartPoint = _rim.StartPoint = Vector2.Zero;
        _fill.EndPoint = _rim.EndPoint = new(0, 1);
        _fillTop = compositor.CreateColorGradientStop(0, Color.FromArgb(0, 255, 255, 255));
        _fillBottom = compositor.CreateColorGradientStop(1, Color.FromArgb(0, 255, 255, 255));
        _rimTop = compositor.CreateColorGradientStop(0, Color.FromArgb(0, 255, 255, 255));
        _rimBottom = compositor.CreateColorGradientStop(1, Color.FromArgb(0, 0, 0, 0));
        _fill.ColorStops.Add(_fillTop); _fill.ColorStops.Add(_fillBottom);
        _rim.ColorStops.Add(_rimTop); _rim.ColorStops.Add(_rimBottom);
        _shape.FillBrush = _fill; _shape.StrokeBrush = _rim; _shape.StrokeThickness = .75f;
        _face.Shapes.Add(_shape);
        // Three low-alpha silhouettes approximate a short soft contact shadow
        // without a drawing surface, large blur, or a per-frame update.
        for (int index = 0; index < _contactShapes.Length; index++)
        {
            _contactGeometry[index] = compositor.CreateRoundedRectangleGeometry();
            _contactBrushes[index] = compositor.CreateColorBrush();
            _contactShapes[index] = compositor.CreateSpriteShape(_contactGeometry[index]);
            _contactShapes[index].FillBrush = _contactBrushes[index];
            _contact.Shapes.Add(_contactShapes[index]);
        }
        _relief.Children.InsertAtTop(_contact); _relief.Children.InsertAtTop(_face);
        Visual.Children.InsertAtTop(_relief);
        SetAppearance(0x81FFFFFF, 0xFF182338);
    }

    /// <summary>All geometry is in DIP. Position/size are never animated here.</summary>
    public void SetBounds(float x, float y, float width, float height, float radius)
    {
        width = float.IsFinite(width) ? Math.Max(0, width) : 0;
        height = float.IsFinite(height) ? Math.Max(0, height) : 0;
        radius = float.IsFinite(radius) ? Math.Clamp(radius, 0, Math.Min(width, height) / 2) : 0;
        Visual.Offset = new(float.IsFinite(x) ? x : 0, float.IsFinite(y) ? y : 0, 0);
        Vector2 size = new(width, height);
        if (_size == size && _radius == radius) return;
        _size = size; _radius = radius;
        Visual.Size = _relief.Size = _face.Size = _contact.Size = size;
        _relief.CenterPoint = new(width / 2, height / 2, 0);
        const float inset = .5f;
        _geometry.Offset = new(inset);
        _geometry.Size = Vector2.Max(Vector2.Zero, size - new Vector2(inset * 2));
        _geometry.CornerRadius = new(Math.Max(0, radius - inset));
        float lift = _kind == ControlDepthKind.Thumb ? 1.25f : _kind == ControlDepthKind.SoftBar ? .45f : 1;
        for (int index = 0; index < _contactGeometry.Length; index++)
        {
            float spread = (2 - index) * .8f;
            _contactGeometry[index].Offset = new(-spread, lift - spread);
            _contactGeometry[index].Size = size + new Vector2(spread * 2);
            _contactGeometry[index].CornerRadius = new(radius + spread);
        }
    }

    /// <summary>Pass the active palette; high contrast flattens relief and keeps a crisp border.</summary>
    public void SetAppearance(uint fillArgb, uint inkArgb, bool highContrast = false)
    {
        if (_appearanceSet && _fillArgb == fillArgb && _inkArgb == inkArgb && _highContrast == highContrast) return;
        _appearanceSet = true; _fillArgb = fillArgb; _inkArgb = inkArgb; _highContrast = highContrast;
        Color fill = ToColor(fillArgb), ink = ToColor(inkArgb);
        if (highContrast)
        {
            fill.A = 255; _fillTop.Color = _fillBottom.Color = fill;
            _rimTop.Color = _rimBottom.Color = ink; _shape.StrokeThickness = 1;
        }
        else if(_opticalGlass)
        {
            // Contrast belongs to the two-tone vector glyph, not an opaque
            // keycap that hides the second lens. Preserve clear transmission.
            bool primary = fill.A >= 0xD0;
            _fillTop.Color=Color.FromArgb(primary?(byte)26:(byte)20,255,255,255);
            _fillBottom.Color=Color.FromArgb(primary?(byte)10:(byte)5,255,255,255);
            _rimTop.Color=Color.FromArgb(150,255,255,255);_rimBottom.Color=Color.FromArgb(40,24,35,48);_shape.StrokeThickness=.7f;
        }
        else
        {
            bool inset = _kind == ControlDepthKind.InsetTrack;
            float top = inset ? -.12f : _kind == ControlDepthKind.SoftBar ? .16f : .08f;
            float bottom = inset ? -.035f : _kind == ControlDepthKind.SoftBar ? -.075f : -.04f;
            _fillTop.Color = Shade(fill, top); _fillBottom.Color = Shade(fill, bottom);
            _rimTop.Color = inset ? WithAlpha(ink, 44) : Color.FromArgb(94, 255, 255, 255);
            _rimBottom.Color = inset ? Color.FromArgb(100, 255, 255, 255) : WithAlpha(ink, 30);
            _shape.StrokeThickness = _kind == ControlDepthKind.SoftBar ? .45f : .75f;
        }
        for (int index = 0; index < _contactBrushes.Length; index++)
            _contactBrushes[index].Color = WithAlpha(ink, (byte)(_kind == ControlDepthKind.SoftBar ? 4 + index * 4 : 4 + index * 6));
        _contact.IsVisible = !highContrast && _kind != ControlDepthKind.InsetTrack;
        ApplyFeedback(false);
    }

    public void SetOpticalGlass(bool value)
    {
        if(_opticalGlass==value)return;_opticalGlass=value;_appearanceSet=false;
        SetAppearance(_fillArgb,_inkArgb,_highContrast);
    }
    /// <summary>Only the empty plate changes. Text/icons and the hit target stay outside this visual.</summary>
    public void SetFeedback(bool hovered, bool pressed, bool reducedMotion)
    {
        if (_feedbackSet && _hovered == hovered && _pressed == pressed && _reducedMotion == reducedMotion) return;
        _feedbackSet = true; _hovered = hovered; _pressed = pressed; _reducedMotion = reducedMotion;
        ApplyFeedback(!reducedMotion && !_highContrast);
    }

    void ApplyFeedback(bool animate)
    {
        bool raised = _kind is ControlDepthKind.Plate or ControlDepthKind.Thumb;
        float sink = raised && _pressed && !_highContrast && !_reducedMotion ? .65f : 0;
        float shadow = _pressed ? .34f : _hovered ? 1 : .78f;
        Animate(_face, "Offset.Y", sink, animate);
        Animate(_contact, "Opacity", shadow, animate);
    }

    void Animate(Visual target, string property, float value, bool animate)
    {
        if (!animate)
        {
            target.StopAnimation(property);
            if (property == "Offset.Y") target.Offset = new(0, value, 0); else target.Opacity = value;
            return;
        }
        using var easing = _compositor.CreateCubicBezierEasingFunction(new(.22f, 1), new(.36f, 1));
        using var animation = _compositor.CreateScalarKeyFrameAnimation();
        animation.InsertExpressionKeyFrame(0, "this.StartingValue"); animation.InsertKeyFrame(1, value, easing);
        animation.Duration = TimeSpan.FromMilliseconds(_pressed ? 80 : 120);
        target.StartAnimation(property, animation);
    }

    static Color ToColor(uint argb) => Color.FromArgb((byte)(argb >> 24), (byte)(argb >> 16), (byte)(argb >> 8), (byte)argb);
    static Color WithAlpha(Color color, byte alpha) => Color.FromArgb(alpha, color.R, color.G, color.B);
    static Color Shade(Color color, float amount)
    {
        byte Channel(byte value) => (byte)Math.Clamp(MathF.Round(amount >= 0 ? value + (255 - value) * amount : value * (1 + amount)), 0, 255);
        return Color.FromArgb(color.A, Channel(color.R), Channel(color.G), Channel(color.B));
    }

    public void Dispose()
    {
        if (_disposed) return; _disposed = true;
        _face.StopAnimation("Offset.Y"); _contact.StopAnimation("Opacity");
        Visual.Children.RemoveAll(); _relief.Children.RemoveAll();
        _face.Shapes.Clear(); _contact.Shapes.Clear();
        _shape.Dispose(); _geometry.Dispose();
        for (int index = 0; index < _contactShapes.Length; index++)
        {
            _contactShapes[index].Dispose(); _contactGeometry[index].Dispose(); _contactBrushes[index].Dispose();
        }
        _fill.ColorStops.Clear(); _rim.ColorStops.Clear();
        _fillTop.Dispose(); _fillBottom.Dispose(); _rimTop.Dispose(); _rimBottom.Dispose();
        _fill.Dispose(); _rim.Dispose(); _face.Dispose(); _contact.Dispose(); _relief.Dispose(); Visual.Dispose();
    }
}

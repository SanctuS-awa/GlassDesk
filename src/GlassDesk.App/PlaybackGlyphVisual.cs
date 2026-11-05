using System.Numerics;
using GlassDesk.NativeBridge;
using Windows.UI;
using Windows.UI.Composition;

namespace GlassDesk.App;

/// <summary>Matched vector contours morph the play triangle into two rounded pause bars.</summary>
internal sealed class PlaybackGlyphVisual : IDisposable
{
    readonly Compositor _compositor;
    readonly CompositionPath _playLeft, _playRight, _pauseLeft, _pauseRight;
    readonly CompositionPathGeometry _left, _right;
    readonly CompositionSpriteShape _leftShape, _rightShape;
    readonly CompositionColorBrush _leftInk, _rightInk;
    bool _opticalGlass;
    bool? _playing;
    uint _color;
    public ShapeVisual Visual { get; }

    public PlaybackGlyphVisual(NativeWidgetHost host)
    {
        _compositor = host.Compositor;
        _playLeft = host.CreatePolygonPath([new(7, 4), new(19, 12), new(19, 12), new(7, 20)], .7f);
        _playRight = host.CreatePolygonPath([new(19, 12), new(19, 12), new(19, 12), new(19, 12)], .7f);
        _pauseLeft = host.CreatePolygonPath([new(7, 4), new(11, 4), new(11, 20), new(7, 20)], .8f);
        _pauseRight = host.CreatePolygonPath([new(14, 4), new(18, 4), new(18, 20), new(14, 20)], .8f);
        Visual = _compositor.CreateShapeVisual(); Visual.Size = new(24, 24);
        _left = _compositor.CreatePathGeometry(); _right = _compositor.CreatePathGeometry();
        _leftInk = _compositor.CreateColorBrush(); _rightInk = _compositor.CreateColorBrush();
        _leftInk.Color = _rightInk.Color = Color.FromArgb(0, 0, 0, 0);
        _leftShape = _compositor.CreateSpriteShape(_left); _rightShape = _compositor.CreateSpriteShape(_right);
        _leftShape.FillBrush = _rightShape.FillBrush = null;
        _leftShape.StrokeBrush = _leftInk; _rightShape.StrokeBrush = _rightInk;
        _leftShape.StrokeThickness = _rightShape.StrokeThickness = 1.6f;
        _leftShape.StrokeLineJoin = _rightShape.StrokeLineJoin = CompositionStrokeLineJoin.Round;
        Visual.Shapes.Add(_leftShape); Visual.Shapes.Add(_rightShape);
    }

    // Switching the path style leaves the caller-colored ink brushes and any
    // in-flight play/pause path or right-bar animation untouched.
    public void SetOpticalGlass(bool value)
    {
        if(_opticalGlass==value)return;_opticalGlass=value;
        ApplyRenderingMode();
    }

    Color SymbolInk(uint color)=>Color.FromArgb((byte)(color>>24),(byte)(color>>16),(byte)(color>>8),(byte)color);
    void ApplyRenderingMode()
    {
        _leftShape.FillBrush = _opticalGlass ? _leftInk : null;
        _rightShape.FillBrush = _opticalGlass ? _rightInk : null;
        _leftShape.StrokeBrush = _opticalGlass ? null : _leftInk;
        _rightShape.StrokeBrush = _opticalGlass ? null : _rightInk;
        _leftShape.StrokeThickness = _rightShape.StrokeThickness = _opticalGlass ? 0 : 1.6f;
    }
    void SetInk(uint color,bool playing)
    {
        Color ink=SymbolInk(color);
        _leftInk.Color=ink;_rightInk.StopAnimation("Color");_rightInk.Color=ink with {A=playing?ink.A:(byte)0};
    }

    public void Update(bool playing, uint color, float buttonSize, float iconSize, bool reducedMotion)
    {
        Visual.Offset = new((buttonSize - iconSize) / 2, (buttonSize - iconSize) / 2, 0);
        Visual.Scale = new(iconSize / 24, iconSize / 24, 1);
        var ink = SymbolInk(color);
        bool initial = _playing is null, changed = _playing != playing;
        if (initial || _color != color)
        {
            SetInk(color,playing);
            _color = color;
        }
        _playing = playing;
        var left = playing ? _pauseLeft : _playLeft; var right = playing ? _pauseRight : _playRight;
        if (initial || reducedMotion)
        {
            _left.StopAnimation("Path"); _right.StopAnimation("Path"); _rightInk.StopAnimation("Color");
            _left.Path = left; _right.Path = right;
            _rightInk.Color = ink with { A = playing ? ink.A : (byte)0 };
            return;
        }
        if (!changed) return;
        using var easing = _compositor.CreateCubicBezierEasingFunction(new(.2f, 0), new(.2f, 1));
        Morph(_left, left, easing); Morph(_right, right, easing);
        using var fade = _compositor.CreateColorKeyFrameAnimation();
        fade.InsertExpressionKeyFrame(0, "this.StartingValue");
        fade.InsertKeyFrame(1, ink with { A = playing ? ink.A : (byte)0 }, easing);
        fade.Duration = TimeSpan.FromMilliseconds(220); _rightInk.StartAnimation("Color", fade);
    }

    void Morph(CompositionPathGeometry geometry, CompositionPath target, CompositionEasingFunction easing)
    {
        using var animation = _compositor.CreatePathKeyFrameAnimation();
        animation.InsertExpressionKeyFrame(0, "this.StartingValue");
        animation.InsertKeyFrame(1, target, easing); animation.Duration = TimeSpan.FromMilliseconds(220);
        geometry.StartAnimation("Path", animation);
    }

    public void Dispose()
    {
        _left.StopAnimation("Path"); _right.StopAnimation("Path"); _rightInk.StopAnimation("Color");
        Visual.Shapes.Clear(); _leftShape.Dispose(); _rightShape.Dispose();
        _left.Dispose(); _right.Dispose(); _leftInk.Dispose(); _rightInk.Dispose(); Visual.Dispose();
    }
}

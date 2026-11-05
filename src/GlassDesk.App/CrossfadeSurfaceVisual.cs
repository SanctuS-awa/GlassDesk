using System.Numerics;
using System.Diagnostics;
using Windows.UI.Composition;
using GlassDesk.NativeBridge;

namespace GlassDesk.App;

/// <summary>Blends an outgoing opaque ink surface over its replacement, without delaying data updates.</summary>
internal sealed class CrossfadeSurfaceVisual : IDisposable
{
    readonly Compositor _compositor;
    readonly SpriteVisual _incoming, _outgoing;
    readonly CompositionSurfaceBrush _brush, _outgoingBrush;
    CompositionDrawingSurface? _surface, _outgoingSurface;
    long _transitionStarted;
    public bool IsTransitioning => _transitionStarted != 0
        && Stopwatch.GetElapsedTime(_transitionStarted).TotalMilliseconds < 280;
    LiquidGlassInkVisual? _incomingInk,_outgoingInk;
    public SpriteVisual Visual { get; }

    public CrossfadeSurfaceVisual(Compositor compositor)
    {
        _compositor = compositor;
        Visual = compositor.CreateSpriteVisual();
        _incoming = compositor.CreateSpriteVisual();
        _outgoing = compositor.CreateSpriteVisual();
        _brush = compositor.CreateSurfaceBrush(); _outgoingBrush = compositor.CreateSurfaceBrush();
        _brush.Stretch = _outgoingBrush.Stretch = CompositionStretch.Fill;
        _incoming.Brush = _brush; _outgoing.Brush = _outgoingBrush;
        Visual.Children.InsertAtTop(_incoming);
        Visual.Children.InsertAtTop(_outgoing); _outgoing.Opacity = 0;
    }

    public void Show(CompositionDrawingSurface replacement, Vector2 size, bool animate, int direction = 0)
    {
        _incomingInk?.Dispose();_outgoingInk?.Dispose();
        var obsolete = _outgoingSurface;
        _outgoingBrush.Surface = _surface;
        _brush.Surface = replacement;
        _outgoingSurface = _surface; _surface = replacement;
        obsolete?.Dispose();
        Visual.Size = _incoming.Size = _outgoing.Size = size;
        _incoming.StopAnimation("Opacity"); _incoming.Opacity = 1;
        _outgoing.StopAnimation("Opacity");
        if (!animate || _outgoingSurface is null) { Finish(); return; }
        _transitionStarted = Stopwatch.GetTimestamp();
        _outgoing.Opacity = 1;
        // Both text/icon surfaces contain transparent surroundings. Fade the
        // new ink in too, so unrelated glyph strokes do not briefly double up.
        _incoming.Opacity = 0;
        using var easing = _compositor.CreateCubicBezierEasingFunction(new(.2f, 0), new(.2f, 1));
        using var incoming = _compositor.CreateScalarKeyFrameAnimation();
        incoming.InsertKeyFrame(0, 0); incoming.InsertKeyFrame(1, 1, easing);
        incoming.Duration = TimeSpan.FromMilliseconds(240);
        _incoming.StartAnimation("Opacity", incoming);
        using var outgoing = _compositor.CreateScalarKeyFrameAnimation();
        outgoing.InsertKeyFrame(0, 1); outgoing.InsertKeyFrame(1, 0, easing);
        outgoing.Duration = TimeSpan.FromMilliseconds(140);
        _outgoing.StartAnimation("Opacity", outgoing);
        Slide(_incoming, direction * 18, 0, 280);
        Slide(_outgoing, 0, -direction * 18, 180);
    }

    void Slide(Visual visual, float from, float to, int duration)
    {
        visual.StopAnimation("Offset.X"); visual.Offset = new(from, 0, 0);
        using var easing = _compositor.CreateCubicBezierEasingFunction(new(.18f, .85f), new(.25f, 1));
        using var animation = _compositor.CreateScalarKeyFrameAnimation();
        animation.InsertKeyFrame(0, from); animation.InsertKeyFrame(1, to, easing);
        animation.Duration = TimeSpan.FromMilliseconds(duration); visual.StartAnimation("Offset.X", animation);
    }

    public void Finish()
    {
        _transitionStarted = 0;
        _incoming.StopAnimation("Opacity"); _incoming.Opacity = 1;
        _outgoing.StopAnimation("Opacity"); _outgoing.Opacity = 0;
        _incoming.StopAnimation("Offset.X"); _incoming.Offset = Vector3.Zero;
        _outgoing.StopAnimation("Offset.X"); _outgoing.Offset = Vector3.Zero;
    }

    public void ApplyLiquidGlassInk(NativeWidgetHost host,Vector2 parentOffset,bool enabled)
    {
        if(!host.IsLiquidGlassCapture)return;
        _incomingInk??=new(host,_incoming,_brush);
        _outgoingInk??=new(host,_outgoing,_outgoingBrush);
        var position=parentOffset+new Vector2(Visual.Offset.X,Visual.Offset.Y);
        _incomingInk.Update(_surface,position+new Vector2(_incoming.Offset.X,_incoming.Offset.Y),Visual.Size,enabled);
        _outgoingInk.Update(_outgoingSurface,position+new Vector2(_outgoing.Offset.X,_outgoing.Offset.Y),Visual.Size,enabled);
    }

    public void Dispose()
    {
        _incomingInk?.Dispose();_outgoingInk?.Dispose();
        Visual.Children.RemoveAll(); _brush.Surface = _outgoingBrush.Surface = null;
        _surface?.Dispose(); _outgoingSurface?.Dispose();
        Visual.Dispose(); _incoming.Dispose(); _outgoing.Dispose(); _brush.Dispose(); _outgoingBrush.Dispose();
    }
}

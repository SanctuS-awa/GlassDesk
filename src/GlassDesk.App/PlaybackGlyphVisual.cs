using System.Diagnostics;
using System.Numerics;
using GlassDesk.Core;
using GlassDesk.NativeBridge;
using Microsoft.UI.Dispatching;
using Windows.UI;
using Windows.UI.Composition;

namespace GlassDesk.App;

/// <summary>Solid play and pause glyphs crossfade while retaining interruptible spring motion.</summary>
internal sealed class PlaybackGlyphVisual : IDisposable
{
    static double Now => Stopwatch.GetTimestamp() * 1000d / Stopwatch.Frequency;

    readonly Compositor _compositor;
    readonly DispatcherQueueTimer _timer;
    readonly ContainerVisual VisualContainer;
    readonly ShapeVisual _playVisual, _pauseVisual;
    readonly CompositionPath _playPath;
    readonly CompositionPathGeometry _playGeometry;
    readonly CompositionRoundedRectangleGeometry _pauseLeftGeometry, _pauseRightGeometry;
    readonly CompositionSpriteShape _playShape, _pauseLeftShape, _pauseRightShape;
    readonly CompositionColorBrush _ink;
    readonly SpringValue _mix = new(0), _iconScale = new(1);
    bool? _playing;
    uint _color;
    double _scaleReturnAt;
    bool _scaleReturnPending, _timerRunning, _visible = true, _disposed;
    bool _opticalGlass;

    public ContainerVisual Visual => VisualContainer;

    public PlaybackGlyphVisual(NativeWidgetHost host)
    {
        _compositor = host.Compositor;
        var queue = DispatcherQueue.GetForCurrentThread()
            ?? throw new InvalidOperationException("播放图标必须在 UI DispatcherQueue 上创建。");
        _timer = queue.CreateTimer();
        _timer.Interval = TimeSpan.FromMilliseconds(16);
        _timer.IsRepeating = true;
        _timer.Tick += OnFrame;

        VisualContainer = _compositor.CreateContainerVisual();
        VisualContainer.Size = new(24, 24);
        _playVisual = _compositor.CreateShapeVisual();
        _pauseVisual = _compositor.CreateShapeVisual();
        _playVisual.Size = _pauseVisual.Size = new(24, 24);
        _playVisual.CenterPoint = _pauseVisual.CenterPoint = new(12, 12, 0);
        _playPath = host.CreatePolygonPath([new(7, 4), new(20, 12), new(7, 20)], .8f);
        _playGeometry = _compositor.CreatePathGeometry();
        _playGeometry.Path = _playPath;
        _playShape = _compositor.CreateSpriteShape(_playGeometry);
        _pauseLeftGeometry = _compositor.CreateRoundedRectangleGeometry();
        _pauseLeftGeometry.Size = new(4, 16);
        _pauseLeftGeometry.CornerRadius = new(1, 1);
        _pauseRightGeometry = _compositor.CreateRoundedRectangleGeometry();
        _pauseRightGeometry.Size = new(4, 16);
        _pauseRightGeometry.CornerRadius = new(1, 1);
        _pauseLeftShape = _compositor.CreateSpriteShape(_pauseLeftGeometry);
        _pauseLeftShape.Offset = new(7, 4);
        _pauseRightShape = _compositor.CreateSpriteShape(_pauseRightGeometry);
        _pauseRightShape.Offset = new(14, 4);
        _ink = _compositor.CreateColorBrush(Color.FromArgb(0, 0, 0, 0));
        foreach (var shape in new[] { _playShape, _pauseLeftShape, _pauseRightShape })
        {
            shape.FillBrush = _ink;
            shape.StrokeBrush = null;
            shape.StrokeThickness = 0;
        }
        _playVisual.Shapes.Add(_playShape);
        _pauseVisual.Shapes.Add(_pauseLeftShape);
        _pauseVisual.Shapes.Add(_pauseRightShape);
        VisualContainer.Children.InsertAtTop(_playVisual);
        VisualContainer.Children.InsertAtTop(_pauseVisual);
        _playVisual.Opacity = 1;
        _pauseVisual.Opacity = 0;
    }

    // Kept for existing transport callers. The glyph remains solid in both modes.
    public void SetOpticalGlass(bool value)
    {
        if (_opticalGlass == value) return;
        _opticalGlass = value;
    }

    public void SetVisible(bool visible)
    {
        if (_disposed || _visible == visible) return;
        _visible = visible;
        if (!visible)
        {
            _scaleReturnPending = false;
            if (_playing is bool playing) _mix.Jump(playing ? 1 : 0);
            _iconScale.Jump(1);
            ApplyValues(Now);
            StopTimer();
        }
        else WakeTimer(Now);
    }

    public void Update(bool playing, uint color, float buttonSize, float iconSize, bool reducedMotion)
    {
        if (_disposed) return;
        VisualContainer.Offset = new((buttonSize - iconSize) / 2, (buttonSize - iconSize) / 2, 0);
        VisualContainer.Scale = new(iconSize / 24, iconSize / 24, 1);

        if (_color != color || _playing is null)
        {
            _ink.Color = Color.FromArgb((byte)(color >> 24), (byte)(color >> 16), (byte)(color >> 8), (byte)color);
            _color = color;
        }

        double now = Now;
        bool initial = _playing is null;
        bool changed = _playing != playing;
        _playing = playing;
        if (initial || reducedMotion || !_visible)
        {
            _mix.Jump(playing ? 1 : 0);
            _iconScale.Jump(1);
            _scaleReturnPending = false;
            ApplyValues(now);
            StopTimer();
            return;
        }

        if (changed)
        {
            _ = _mix.Sample(now);
            _mix.Retarget(playing ? 1 : 0, now, 240);
            _ = _iconScale.Sample(now);
            _iconScale.Retarget(.94, now, 90);
            _scaleReturnAt = now + 90;
            _scaleReturnPending = true;
        }

        ApplyValues(now);
        WakeTimer(now);
    }

    void OnFrame(DispatcherQueueTimer sender, object args)
    {
        if (_disposed || !_visible)
        {
            StopTimer();
            return;
        }

        double now = Now;
        if (_scaleReturnPending && now >= _scaleReturnAt)
        {
            _ = _iconScale.Sample(now);
            _iconScale.Retarget(1, now, 230);
            _scaleReturnPending = false;
        }
        ApplyValues(now);
        if (!HasMotion(now)) StopTimer();
    }

    void ApplyValues(double now)
    {
        float mix = (float)Math.Clamp(_mix.Sample(now).Position, 0, 1);
        double sampledScale = _iconScale.Sample(now).Position;
        float scale = double.IsFinite(sampledScale) ? (float)sampledScale : 1;
        _pauseVisual.Opacity = mix;
        _playVisual.Opacity = 1 - mix;
        _pauseVisual.Scale = _playVisual.Scale = new(scale, scale, 1);
    }

    bool HasMotion(double now) => _mix.Active(now) || _iconScale.Active(now) || _scaleReturnPending;

    void WakeTimer(double now)
    {
        if (!_visible || !HasMotion(now))
        {
            StopTimer();
            return;
        }
        if (_timerRunning) return;
        _timerRunning = true;
        _timer.Start();
    }

    void StopTimer()
    {
        if (!_timerRunning) return;
        _timer.Stop();
        _timerRunning = false;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _timer.Tick -= OnFrame;
        StopTimer();
        VisualContainer.Children.RemoveAll();
        _playVisual.Shapes.Clear();
        _pauseVisual.Shapes.Clear();
        _playShape.Dispose();
        _pauseLeftShape.Dispose(); _pauseRightShape.Dispose();
        _playGeometry.Dispose();
        _pauseLeftGeometry.Dispose(); _pauseRightGeometry.Dispose();
        _ink.Dispose();
        _playVisual.Dispose(); _pauseVisual.Dispose(); VisualContainer.Dispose();
    }
}

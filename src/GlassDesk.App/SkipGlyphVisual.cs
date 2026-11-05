// SPDX-License-Identifier: Apache-2.0
// Previous/next contour adapted from MoFeng2223/BrimDeck, MusicVisuals.cs, commit
// e3765b1b51c5f3fb7d05feee7c1f6f4ba082eff3 (Apache-2.0).
// Modified for GlassDesk: sampled quadratic curves and retained WUC vector rendering,
// replacing WPF Geometry.Parse/Viewbox. See licenses/BrimDeck/NOTICE.md and LICENSE.
using System.Numerics;
using GlassDesk.NativeBridge;
using Windows.UI;
using Windows.UI.Composition;

namespace GlassDesk.App;

internal sealed class SkipGlyphVisual : IDisposable
{
    readonly CompositionColorBrush _ink;
    readonly CompositionPathGeometry _bar, _triangle;
    public ShapeVisual Visual { get; }

    public SkipGlyphVisual(NativeWidgetHost host)
    {
        var compositor = host.Compositor;
        Visual = compositor.CreateShapeVisual(); Visual.Size = new(24);
        _ink = compositor.CreateColorBrush();
        _ink.Color = Color.FromArgb(0, 0, 0, 0);
        _bar = compositor.CreatePathGeometry(); _triangle = compositor.CreatePathGeometry();
        _bar.Path = host.CreatePolygonPath([new(3, 4), new(5, 4), new(5, 20), new(3, 20)], .25f);
        var points = new List<Vector2> { new(20, 4) };
        Curve(points, new(20, 4), new(21, 3.4f), new(21, 5));
        points.Add(new(21, 19)); Curve(points, new(21, 19), new(21, 20.6f), new(20, 20));
        points.Add(new(7, 13)); Curve(points, new(7, 13), new(5.5f, 12), new(7, 11));
        _triangle.Path = host.CreatePolygonPath(points.ToArray(), .15f);
        foreach (var geometry in new[] { _bar, _triangle })
        {
            var shape = compositor.CreateSpriteShape(geometry); shape.FillBrush = _ink;
            shape.StrokeBrush = null; shape.StrokeThickness = 0;
            Visual.Shapes.Add(shape);
        }
    }

    static void Curve(List<Vector2> points, Vector2 from, Vector2 control, Vector2 to)
    {
        for (int i = 1; i <= 8; i++)
        {
            float t = i / 8f, u = 1 - t; points.Add(u * u * from + 2 * u * t * control + t * t * to);
        }
    }

    public void Update(bool next, uint color, float buttonSize, float iconSize)
    {
        float scale = iconSize / 24, inset = (buttonSize - iconSize) / 2;
        Visual.Offset = new(inset + (next ? iconSize : 0), inset, 0);
        Visual.Scale = new(next ? -scale : scale, scale, 1);
        _ink.Color = Color.FromArgb((byte)(color >> 24), (byte)(color >> 16), (byte)(color >> 8), (byte)color);
    }

    // Skip contours use the caller-provided solid ink in both rendering modes.
    public void SetOpticalGlass(bool value) { }

    public void Dispose()
    {
        foreach (var shape in Visual.Shapes) shape.Dispose(); Visual.Shapes.Clear();
        _bar.Dispose(); _triangle.Dispose(); _ink.Dispose(); Visual.Dispose();
    }
}

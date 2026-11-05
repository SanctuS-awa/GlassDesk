using System.Numerics;
using GlassDesk.NativeBridge;
using Windows.UI.Composition;

namespace GlassDesk.App;

/// <summary>Applies the GPU-estimated glass ink color through the existing text alpha mask.</summary>
internal sealed class LiquidGlassInkVisual : IDisposable
{
    readonly NativeWidgetHost _host;
    readonly SpriteVisual _visual;
    readonly CompositionSurfaceBrush _original;
    readonly CompositionMaskBrush _masked;
    public LiquidGlassInkVisual(NativeWidgetHost host, SpriteVisual visual, CompositionSurfaceBrush original)
    {
        _host=host;_visual=visual;_original=original;_masked=host.Compositor.CreateMaskBrush();
        _visual.Brush=_original;
    }
    public void Update(CompositionDrawingSurface? mask, Vector2 position, Vector2 size, bool enabled)
    {
        _=position;_ =size;
        if(!enabled||mask is null){_visual.Brush=_original;return;}
        var ink=_host.GetLiquidGlassInkColorBrush();
        if(ink is null){_visual.Brush=_original;return;}
        if(!ReferenceEquals(_original.Surface,mask))_original.Surface=mask;
        _masked.Source=ink;_masked.Mask=_original;_visual.Brush=_masked;
    }
    public void Dispose() { _visual.Brush = _original;_masked.Dispose(); }
}

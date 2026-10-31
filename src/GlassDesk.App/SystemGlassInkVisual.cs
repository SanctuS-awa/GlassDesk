using System.Numerics;
using GlassDesk.NativeBridge;
using Windows.UI.Composition;

namespace GlassDesk.App;

/// <summary>Adaptive compositor ink on one own-alpha foreground, with no new surface.</summary>
internal sealed class SystemGlassInkVisual : IDisposable
{
    readonly NativeWidgetHost _host;
    readonly SpriteVisual _visual;
    readonly CompositionSurfaceBrush _original;
    CompositionDrawingSurface? _mask;
    CompositionBrush? _ink;
    Vector4 _bounds;
    public SystemGlassInkVisual(NativeWidgetHost host,SpriteVisual visual,CompositionSurfaceBrush original)
    {_host=host;_visual=visual;_original=original;}
    public void Update(CompositionDrawingSurface? mask,Vector2 position,Vector2 size,bool enabled)
    {
        if(!enabled||mask is null||size.X<=0||size.Y<=0){Reset();return;}
        var bounds=new Vector4(position,size.X,size.Y);
        if(!ReferenceEquals(mask,_mask))Reset();
        if(_ink is null){
            _ink=_host.CreateSystemGlassInk(mask,bounds);
            if(_ink is null)return; // Hidden first render precedes source initialization.
            _mask=mask;_bounds=bounds;_visual.Brush=_ink;
        }else if(_bounds!=bounds){_host.UpdateSystemGlassInk(_ink,bounds);_bounds=bounds;}
    }
    void Reset()
    {
        _visual.Brush=_original;
        var ink=_ink;_ink=null;_mask=null;
        if(ink is null)return;
        try{
            // Native first-fault shutdown has already detached every binding.
            if(_host.MaterialSession?.IsExperimentTripped!=true)_host.ReleaseSystemGlassInk(ink);
        }finally{ink.Dispose();}
    }
    public void Dispose()
    {
        try{Reset();}
        catch(Exception error){
            // Do not abort the rest of Scene.Dispose over one failed brush.
            // Persist the failure so a later successful host cleanup cannot
            // relabel this session clean.
            _host.MaterialSession?.MarkFault($"adaptive ink cleanup: 0x{error.HResult:X8}");
        }
    }
}

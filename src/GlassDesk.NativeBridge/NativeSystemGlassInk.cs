using System.Numerics;
using System.Runtime.InteropServices;
using Windows.UI.Composition;

namespace GlassDesk.NativeBridge;

public sealed partial class NativeWidgetHost
{
    // Quarantined after r18/m3: marker-interface CPU tests do not cover a real
    // DrawingSurface/effect factory. Keep the established optical path usable
    // without silently entering this unverified extension.
    public bool SystemAdaptiveInkAvailable => false;
    // Only compositor objects cross this ABI, never a desktop bitmap, texture
    // handle, sample value or readback. The mask is our own foreground surface.
    public CompositionBrush? CreateSystemGlassInk(CompositionDrawingSurface mask, Vector4 bounds)
    {
        CheckThread();ArgumentNullException.ThrowIfNull(mask);
        if(!SystemAdaptiveInkAvailable||!IsSystemCompositorCandidate||MaterialSession!.IsExperimentTripped)return null;
        nint source=0,result=0;
        try{
            // The ABI accepts IInspectable, not a runtime-class interface IID.
            try{source=WinRT.MarshalInspectable<object>.FromManaged(mask);}
            catch(Exception error){throw new COMException("Adaptive ink: marshal own foreground mask.",error.HResult);}
            int hr=GDCreateSystemGlassInk(native,source,bounds.X,bounds.Y,bounds.Z,bounds.W,out result);
            if(hr<0){
                uint stage=0;
                int diagnosticHr=GDGetSystemGlassInkStage(native,out stage);
                // Never turn an unavailable diagnostic into a fictitious stage 0,
                // or replace the original graphics HRESULT with the lookup error.
                var location=diagnosticHr==0?$"stage {stage}":$"stage unavailable (0x{diagnosticHr:X8})";
                var error=new COMException($"Adaptive ink: create compositor effect brush ({location}).",hr);
                if(diagnosticHr==0)error.Data["SystemGlassInkStage"]=stage;
                else error.Data["SystemGlassInkStageHResult"]=unchecked((uint)diagnosticHr);
                throw error;
            }
            try{return result==0?null:WinRT.MarshalInterface<CompositionBrush>.FromAbi(result);}
            catch(Exception error){throw new COMException("Adaptive ink: project compositor brush.",error.HResult);}
        }finally{if(result!=0)Marshal.Release(result);if(source!=0)Marshal.Release(source);}
    }
    public void UpdateSystemGlassInk(CompositionBrush brush,Vector4 bounds)
    {
        CheckThread();nint pointer=0;
        try{
            pointer=WinRT.MarshalInspectable<object>.FromManaged(brush);
            Marshal.ThrowExceptionForHR(GDUpdateSystemGlassInk(native,pointer,bounds.X,bounds.Y,bounds.Z,bounds.W));
        }finally{if(pointer!=0)Marshal.Release(pointer);}
    }
    public void ReleaseSystemGlassInk(CompositionBrush brush)
    {
        CheckThread();nint pointer=0;
        try{
            pointer=WinRT.MarshalInspectable<object>.FromManaged(brush);
            Marshal.ThrowExceptionForHR(GDReleaseSystemGlassInk(native,pointer));
        }finally{if(pointer!=0)Marshal.Release(pointer);}
    }
    [DllImport("GlassDesk.Native.dll",ExactSpelling=true,CallingConvention=CallingConvention.StdCall)]
    static extern int GDCreateSystemGlassInk(nint host,nint mask,float x,float y,float width,float height,out nint result);
    [DllImport("GlassDesk.Native.dll",ExactSpelling=true,CallingConvention=CallingConvention.StdCall)]
    static extern int GDUpdateSystemGlassInk(nint host,nint brush,float x,float y,float width,float height);
    [DllImport("GlassDesk.Native.dll",ExactSpelling=true,CallingConvention=CallingConvention.StdCall)]
    static extern int GDReleaseSystemGlassInk(nint host,nint brush);
    [DllImport("GlassDesk.Native.dll",ExactSpelling=true,CallingConvention=CallingConvention.StdCall)]
    static extern int GDGetSystemGlassInkStage(nint host,out uint stage);
}

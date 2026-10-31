using System.Runtime.InteropServices;
using GlassDesk.Core;

namespace GlassDesk.NativeBridge;

public readonly record struct OwnedGlassLens(float CenterX,float CenterY,float Width,float Height,
    float Radius,float Opacity,float Emphasis,float LightInk);

public sealed partial class NativeWidgetHost
{
    public DesktopMaterialSession? MaterialSession {get;}
    public bool IsSystemCompositorCandidate=>MaterialSession?.Backend==DesktopMaterialBackend.SystemCompositorCandidate;
    public bool IsOwnedImageCandidate=>MaterialSession?.Backend==DesktopMaterialBackend.OwnedImageCandidate;
    public bool IsExperimentalDesktopMaterialCandidate=>MaterialSession?.IsExperimentalCandidate==true;
    public bool OwnedImageAvailable {get;private set;}
    public bool OwnedImagePresented {get;private set;}
    ulong ownedFrameSequence;
    public void InvalidateOwnedWallpaper()
    {
        CheckThread();OwnedImageAvailable=false;OwnedImagePresented=false;
        if(IsOwnedImageCandidate&&!MaterialSession!.IsExperimentTripped)PresentOwnedMaterial(false,[]);
    }

    [StructLayout(LayoutKind.Sequential)]
    readonly struct DesktopCreation
    {
        readonly uint size,version,backend,reserved;
        readonly ulong low,high;
        public DesktopCreation(DesktopMaterialBackend value,Guid session,bool incidentHostShortTest,bool systemCompositorPreview)
        {
            bool candidate=value is DesktopMaterialBackend.OwnedImageCandidate or DesktopMaterialBackend.SystemCompositorCandidate;
            bool systemCandidate=value==DesktopMaterialBackend.SystemCompositorCandidate;
            if(incidentHostShortTest&&systemCompositorPreview)
                throw new InvalidOperationException("Short-test and persistent preview creation modes are mutually exclusive.");
            if(incidentHostShortTest&&!candidate)
                throw new InvalidOperationException("Short-test flag requires an authorized candidate session.");
            if(systemCompositorPreview&&!systemCandidate)
                throw new InvalidOperationException("Persistent preview ABI requires the system-compositor candidate.");
            if(systemCandidate&&!incidentHostShortTest&&!systemCompositorPreview)
                throw new InvalidOperationException("System compositor candidate requires an explicit short-test or persistent-preview mode.");
            if(candidate&&session==Guid.Empty)
                throw new InvalidOperationException("Candidate host options require a one-start session ID.");
            size=32;version=systemCompositorPreview?2u:1u;backend=(uint)value;
            reserved=incidentHostShortTest?1u:0u;
            byte[] bytes=session.ToByteArray();low=BitConverter.ToUInt64(bytes,0);high=BitConverter.ToUInt64(bytes,8);
        }
    }
    [StructLayout(LayoutKind.Sequential)]
    readonly record struct ImageMapping(uint Size,uint Version,float X,float Y,float Width,float Height,
        int MonitorX,int MonitorY,int MonitorWidth,int MonitorHeight,uint Background,uint Reserved);
    [StructLayout(LayoutKind.Sequential)]
    readonly record struct GlassLens(float X,float Y,float Width,float Height,float Radius,float Opacity,float Emphasis,float LightInk);

    public unsafe void SetOwnedWallpaper(byte[] pixels,int width,int height,DesktopWallpaperMapping mapping,uint backgroundArgb)
    {
        CheckThread();ArgumentNullException.ThrowIfNull(pixels);ArgumentNullException.ThrowIfNull(mapping);
        if(!IsOwnedImageCandidate||MaterialSession!.IsExperimentTripped)throw new InvalidOperationException("Owned material is not authorized.");
        if(width<=0||height<=0||width>1600||height>1600||pixels.LongLength!=(long)width*height*4)
            throw new ArgumentException("Invalid bounded wallpaper image.");
        var monitor=mapping.Monitor;
        var nativeMapping=new ImageMapping(48,1,(float)mapping.DestinationX,(float)mapping.DestinationY,
            (float)mapping.DestinationWidth,(float)mapping.DestinationHeight,
            checked((int)monitor.X),checked((int)monitor.Y),checked((int)monitor.Width),checked((int)monitor.Height),backgroundArgb,0);
        try{
            fixed(byte* data=pixels)Marshal.ThrowExceptionForHR(GDSetOwnedGlassImage(native,data,(uint)width,(uint)height,(uint)pixels.Length,in nativeMapping));
            OwnedImageAvailable=true;
        }catch(Exception error){TripOwnedMaterial($"wallpaper upload: 0x{error.HResult:X8}");throw;}
    }
    public unsafe void PresentOwnedMaterial(bool eligible,ReadOnlySpan<OwnedGlassLens> lenses)
    {
        CheckThread();if(!IsOwnedImageCandidate||MaterialSession!.IsExperimentTripped)return;
        if(lenses.Length>3)throw new ArgumentOutOfRangeException(nameof(lenses));
        Span<GlassLens> values=stackalloc GlassLens[lenses.Length];
        for(int i=0;i<lenses.Length;i++){
            var l=lenses[i];values[i]=new(l.CenterX,l.CenterY,l.Width,l.Height,l.Radius,l.Opacity,l.Emphasis,l.LightInk);
        }
        try{
            int result;
            fixed(GlassLens* data=values)result=GDPresentOwnedGlass(native,eligible?1:0,data,(uint)values.Length,++ownedFrameSequence);
            Marshal.ThrowExceptionForHR(result);OwnedImagePresented=result==0&&eligible&&OwnedImageAvailable;
        }catch(Exception error){TripOwnedMaterial($"material present: 0x{error.HResult:X8}");throw;}
    }
    // Geometry only. This call never accepts an image, texture or screen pixels.
    public unsafe void SetSystemGlassLenses(bool eligible,ReadOnlySpan<OwnedGlassLens> lenses)
    {
        CheckThread();if(!IsSystemCompositorCandidate||MaterialSession!.IsExperimentTripped)return;
        if(lenses.Length>3)throw new ArgumentOutOfRangeException(nameof(lenses));
        Span<GlassLens> values=stackalloc GlassLens[lenses.Length];
        for(int i=0;i<lenses.Length;i++){
            var l=lenses[i];values[i]=new(l.CenterX,l.CenterY,l.Width,l.Height,l.Radius,eligible?l.Opacity:0,l.Emphasis,l.LightInk);
        }
        try{fixed(GlassLens* data=values)Marshal.ThrowExceptionForHR(GDSetSystemGlassLenses(native,data,(uint)values.Length));}
        catch(Exception error){TripDesktopMaterial($"system lens geometry: 0x{error.HResult:X8}");throw;}
    }
    [DllImport("GlassDesk.Native.dll",ExactSpelling=true,CallingConvention=CallingConvention.StdCall)]
    static extern unsafe int GDSetSystemGlassLenses(nint context,GlassLens* lenses,uint count);
    public void TripDesktopMaterial(string reason)
    {
        if(!IsExperimentalDesktopMaterialCandidate)return;
        // Persist first, but a persistence IO failure must not allow the native
        // dispatch to continue submitting graphics before queued teardown.
        try{MaterialSession!.MarkFault(reason);}
        finally
        {
            OwnedImagePresented=false;
            if(native!=0&&Environment.CurrentManagedThreadId==ownerThread)GDTripOwnedGlass(native);
        }
    }
    public void TripOwnedMaterial(string reason)
    {
        if(IsOwnedImageCandidate)TripDesktopMaterial(reason);
    }
    [DllImport("GlassDesk.Native.dll",ExactSpelling=true,CallingConvention=CallingConvention.StdCall)]
    static extern int GDCreateV2(NativeMethods.Callback callback,nint user,in DesktopCreation options,out nint context,out nint window);
    [DllImport("GlassDesk.Native.dll",ExactSpelling=true,CallingConvention=CallingConvention.StdCall)]
    static extern unsafe int GDSetOwnedGlassImage(nint context,byte* pixels,uint width,uint height,uint bytes,in ImageMapping mapping);
    [DllImport("GlassDesk.Native.dll",ExactSpelling=true,CallingConvention=CallingConvention.StdCall)]
    static extern unsafe int GDPresentOwnedGlass(nint context,int eligible,GlassLens* lenses,uint count,ulong sequence);
    [DllImport("GlassDesk.Native.dll",ExactSpelling=true,CallingConvention=CallingConvention.StdCall)]
    static extern int GDTripOwnedGlass(nint context);
}

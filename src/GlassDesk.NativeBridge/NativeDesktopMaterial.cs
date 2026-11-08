using System.Runtime.InteropServices;
using GlassDesk.Core;
using Windows.UI.Composition;

namespace GlassDesk.NativeBridge;

public readonly record struct OwnedGlassLens(float CenterX,float CenterY,float Width,float Height,
    float Radius,float Opacity,float Emphasis,float LightInk);

public sealed partial class NativeWidgetHost
{
    CompositionSurfaceBrush? liquidGlassInkColorBrush;
    ulong liquidGlassInkSurfaceGeneration;
    public DesktopMaterialSession? MaterialSession {get;}
    public bool IsLiquidGlassCapture=>MaterialSession?.Backend==DesktopMaterialBackend.LiquidGlassCapture;
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
        public DesktopCreation(DesktopMaterialBackend value,Guid session,bool incidentHostShortTest,bool liquidGlassCapturePreview)
        {
            bool candidate=value is DesktopMaterialBackend.OwnedImageCandidate or DesktopMaterialBackend.LiquidGlassCapture;
            bool systemCandidate=value==DesktopMaterialBackend.LiquidGlassCapture;
            if(incidentHostShortTest&&liquidGlassCapturePreview)
                throw new InvalidOperationException("Short-test and persistent preview creation modes are mutually exclusive.");
            if(incidentHostShortTest&&!candidate)
                throw new InvalidOperationException("Short-test flag requires an authorized candidate session.");
            if(liquidGlassCapturePreview&&!systemCandidate)
                throw new InvalidOperationException("Persistent preview ABI requires the liquid-glass-capture candidate.");
            if(systemCandidate&&!incidentHostShortTest&&!liquidGlassCapturePreview)
                throw new InvalidOperationException("Liquid glass capture candidate requires an explicit short-test or persistent-preview mode.");
            if(candidate&&session==Guid.Empty)
                throw new InvalidOperationException("Candidate host options require a one-start session ID.");
            size=32;version=liquidGlassCapturePreview?2u:1u;backend=(uint)value;
            reserved=incidentHostShortTest?1u:0u;
            byte[] bytes=session.ToByteArray();low=BitConverter.ToUInt64(bytes,0);high=BitConverter.ToUInt64(bytes,8);
        }
    }
    [StructLayout(LayoutKind.Sequential)]
    readonly record struct ImageMapping(uint Size,uint Version,float X,float Y,float Width,float Height,
        int MonitorX,int MonitorY,int MonitorWidth,int MonitorHeight,uint Background,uint Reserved);
    [StructLayout(LayoutKind.Sequential)]
    readonly record struct GlassLens(float X,float Y,float Width,float Height,float Radius,float Opacity,float Emphasis,float LightInk);

    [StructLayout(LayoutKind.Sequential)]
    struct NativeGlassMaterialSettings
    {
        public uint TintArgb;
        public float Opacity,Thin,Dense,Blur,Saturation,Brightness,Contrast,RimLight,Bevel,Shadow;
    }
    [StructLayout(LayoutKind.Sequential)]
    struct NativeLiquidGlassSettings
    {
        public uint Size,Version;
        public int Theme,Ink;
        public uint AutoDensity,LensEnabled;
        public NativeGlassMaterialSettings Light,Dark;
        public float RimMaxPixels,Edge,Power,Dispersion,CenterDispersion,Density,Threshold;
    }

    public void SetLiquidGlassSettings(LiquidGlassPreferences preferences)
    {
        CheckThread();
        if(!IsLiquidGlassCapture)return;
        var value=(preferences??new()).Validated();
        static NativeGlassMaterialSettings Material(LiquidGlassMaterialPreferences source)=>new()
        {
            TintArgb=0xff000000u|uint.Parse(source.Tint.AsSpan(1),System.Globalization.NumberStyles.HexNumber,System.Globalization.CultureInfo.InvariantCulture),
            Opacity=source.Opacity,Thin=source.Thin,Dense=source.Dense,Blur=source.Blur,Saturation=source.Saturation,
            Brightness=source.Brightness,Contrast=source.Contrast,RimLight=source.RimLight,Bevel=source.Bevel,Shadow=source.Shadow
        };
        var nativeSettings=new NativeLiquidGlassSettings
        {
            Size=(uint)Marshal.SizeOf<NativeLiquidGlassSettings>(),Version=1,
            Theme=(int)value.Theme,Ink=(int)value.Ink,AutoDensity=value.AutoDensity?1u:0u,
            LensEnabled=value.LensEnabled?1u:0u,Light=Material(value.Light),Dark=Material(value.Dark),
            RimMaxPixels=value.RimMaxPixels,Edge=value.Edge,Power=value.Power,Dispersion=value.Dispersion,
            CenterDispersion=value.CenterDispersion,Density=value.Density,Threshold=value.Threshold
        };
        Marshal.ThrowExceptionForHR(GDSetLiquidGlassSettings(native,in nativeSettings));
    }

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
    // New capture route (0.2.1.3beta, Liquid Glass plugin 0.3). The old lens-geometry export is gone:
    // the capture object refracts the whole widget rectangle itself, so the
    // scene no longer submits per-control lens regions to native code.
    public bool LiquidGlassInkAvailable
    {
        get{CheckThread();if(!IsLiquidGlassCapture)return false;Marshal.ThrowExceptionForHR(GDGetLiquidGlassInk(native,out int available,out _));return available!=0;}
    }
    public bool LiquidGlassCaptureInkIsDark
    {
        get{CheckThread();if(!IsLiquidGlassCapture)return false;Marshal.ThrowExceptionForHR(GDGetLiquidGlassInk(native,out _,out int dark));return dark!=0;}
    }
    public CompositionSurfaceBrush? GetLiquidGlassInkColorBrush()
    {
        CheckThread();if(!IsLiquidGlassCapture||native==0)return null;
        int result=GDGetLiquidGlassInkColorSurface(native,out nint surface,out ulong generation);
        if(result==1||surface==0)return null;
        Marshal.ThrowExceptionForHR(result);
        if(liquidGlassInkColorBrush is not null&&liquidGlassInkSurfaceGeneration==generation)
        {
            Marshal.Release(surface);
            return liquidGlassInkColorBrush;
        }
        try
        {
            var compositionSurface=WinRT.MarshalInterface<ICompositionSurface>.FromAbi(surface);
            var replacement=Compositor.CreateSurfaceBrush(compositionSurface);
            replacement.Stretch=CompositionStretch.Fill;
            liquidGlassInkColorBrush?.Dispose();
            liquidGlassInkColorBrush=replacement;
            liquidGlassInkSurfaceGeneration=generation;
            return replacement;
        }
        finally{Marshal.Release(surface);}
    }
    public void StartLiquidGlassCapture()
    {
        CheckThread();if(!IsLiquidGlassCapture||MaterialSession!.IsExperimentTripped)return;
        Marshal.ThrowExceptionForHR(GDUpdateLiquidGlassCapture(native));
    }
    public void UpdateLiquidGlassCapture()
    {
        CheckThread();if(!IsLiquidGlassCapture||MaterialSession!.IsExperimentTripped)return;
        Marshal.ThrowExceptionForHR(GDUpdateLiquidGlassCapture(native));
    }
    public void ResetLiquidGlassCapture()
    {
        CheckThread();if(!IsLiquidGlassCapture)return;
        // Reset the native device-error fuse. If the capture object itself
        // was tripped, drop it so the next Start creates a fresh one.
        // Deliberately does NOT clear DesktopMaterialSession's experiment
        // fuse: that fuse is one-start by design and cannot be re-armed by
        // a button. If the experiment fuse tripped, a restart with a new
        // session authorization is required; this button alone cannot do it.
        Marshal.ThrowExceptionForHR(GDResetLiquidGlassCapture(native));
    }
    public void StopLiquidGlassCapture()
    {
        CheckThread();if(native!=0)Marshal.ThrowExceptionForHR(GDStopLiquidGlassCapture(native));
    }
    void ReleaseLiquidGlassInkColorBrush()
    {
        liquidGlassInkColorBrush?.Dispose();liquidGlassInkColorBrush=null;liquidGlassInkSurfaceGeneration=0;
    }
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
    static extern int GDSetLiquidGlassSettings(nint context,in NativeLiquidGlassSettings settings);
    [DllImport("GlassDesk.Native.dll",ExactSpelling=true,CallingConvention=CallingConvention.StdCall)]
    static extern unsafe int GDPresentOwnedGlass(nint context,int eligible,GlassLens* lenses,uint count,ulong sequence);
    [DllImport("GlassDesk.Native.dll",ExactSpelling=true,CallingConvention=CallingConvention.StdCall)]
    static extern int GDTripOwnedGlass(nint context);
    [DllImport("GlassDesk.Native.dll",ExactSpelling=true,CallingConvention=CallingConvention.StdCall)]
    static extern int GDUpdateLiquidGlassCapture(nint context);
    [DllImport("GlassDesk.Native.dll",ExactSpelling=true,CallingConvention=CallingConvention.StdCall)]
    static extern int GDResetLiquidGlassCapture(nint context);
    [DllImport("GlassDesk.Native.dll",ExactSpelling=true,CallingConvention=CallingConvention.StdCall)]
    static extern int GDStopLiquidGlassCapture(nint context);
    [DllImport("GlassDesk.Native.dll",ExactSpelling=true,CallingConvention=CallingConvention.StdCall)]
    static extern int GDGetLiquidGlassInk(nint context,out int available,out int isDark);
    [DllImport("GlassDesk.Native.dll",ExactSpelling=true,CallingConvention=CallingConvention.StdCall)]
    static extern int GDGetLiquidGlassInkColorSurface(nint context,out nint surface,out ulong generation);
}

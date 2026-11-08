using System.Runtime.InteropServices;
using System.Numerics;
using GlassDesk.Core;
using Windows.UI.Composition;

namespace GlassDesk.NativeBridge;

/// <summary>
/// Owns the original Win32/WUC host on the existing UI STA. ContentRoot uses DIP
/// coordinates; native DPI scaling and pixel surface allocation are internal.
/// Dispose scenes and platform message hooks before disposing this host.
/// </summary>
public sealed partial class NativeWidgetHost : IWidgetHost
{
    [DllImport("user32.dll",CharSet=CharSet.Unicode,SetLastError=true)]
    [return:MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowTextW(nint window,string value);
    [DllImport("user32.dll",SetLastError=true)]
    [return:MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(nint window,nint after,int x,int y,int width,int height,uint flags);
    [DllImport("user32.dll")]
    [return:MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(nint window);
    public void SetTitle(string title){CheckThread();if(!SetWindowTextW(Handle,title))Marshal.ThrowExceptionForHR(Marshal.GetHRForLastWin32Error());}
    private readonly int ownerThread=Environment.CurrentManagedThreadId;
    private readonly NativeMethods.Callback callback;
    private nint native;
    private Compositor? compositor;
    private ContainerVisual? contentRoot;
    private HostContext context;
    private bool disposed;
    private bool handlingCallbackFailure;

    public event Action<HostPointerEvent>? PointerEvent;
    public event Action<HostContext>? WindowContextChanged;
    public event Action<string>? NativeError;
    public event Action? GraphicsDeviceRestored;
    public HostContext Context=>context;
    public NativeTypographyDescription TypographyAbi {get;}
    public FontProfile TypographyProfile {get;private set;}=FontProfile.SystemA;
    public int TypographyRevision=>TypographyCatalog.Revision;
    public nint Handle {get; private set;}
    public Compositor Compositor=>compositor??throw new ObjectDisposedException(nameof(NativeWidgetHost));
    public ContainerVisual ContentRoot=>contentRoot??throw new ObjectDisposedException(nameof(NativeWidgetHost));
    /// <summary>Live renderer status; enablement intent is retained on fallback.</summary>
    public unsafe string LiquidGlassStatus
    {
        get
        {
            CheckThread();Span<char> buffer=stackalloc char[512];
            fixed(char* pointer=buffer)
                Marshal.ThrowExceptionForHR(NativeMethods.GDGetLiquidGlassStatus(native,pointer,(uint)buffer.Length));
            var end=buffer.IndexOf('\0');return new string(end<0?buffer:buffer[..end]);
        }
    }

    /// <summary>Explicit QA window state, including affinity restoration readback and restart outcome.</summary>
    public unsafe string DiagnosticSnapshotStatus
    {
        get
        {
            CheckThread();Span<char> buffer=stackalloc char[512];
            fixed(char* pointer=buffer)
                Marshal.ThrowExceptionForHR(NativeMethods.GDGetDiagnosticSnapshotStatus(native,pointer,(uint)buffer.Length));
            var end=buffer.IndexOf('\0');return new string(end<0?buffer:buffer[..end]);
        }
    }

    public NativeWidgetHost(DesktopMaterialSession? materialSession=null,bool incidentHostShortTest=false,bool liquidGlassCapturePreview=false)
    {
        if(Thread.CurrentThread.GetApartmentState()!=ApartmentState.STA)
            throw new InvalidOperationException("NativeWidgetHost must be created on the application UI STA.");
        MaterialSession=materialSession;
        TypographyAbi=NativeTypographyAbi.ReadAndValidate();
        callback=OnNativeEvent;
        nint window;
        try{
            if(materialSession is null)Marshal.ThrowExceptionForHR(NativeMethods.GDCreate(callback,0,out native,out window));
            else{
                if(liquidGlassCapturePreview!=materialSession.HostOptions.LiquidGlassCapturePreview)
                    throw new InvalidOperationException("Native creation mode does not match the authorized desktop-material session.");
                var creation=new DesktopCreation(materialSession.Backend,materialSession.SessionId,incidentHostShortTest,liquidGlassCapturePreview);
                Marshal.ThrowExceptionForHR(GDCreateV2(callback,0,in creation,out native,out window));
            }
        }catch(Exception error){materialSession?.MarkFault($"native creation: 0x{error.HResult:X8}");throw;}
        Handle=window;
        nint compositorPointer=0,contentPointer=0;
        try
        {
            Marshal.ThrowExceptionForHR(NativeMethods.GDGetComposition(native,out compositorPointer,out contentPointer));
            compositor=WinRT.MarshalInterface<Compositor>.FromAbi(compositorPointer);
            contentRoot=WinRT.MarshalInterface<ContainerVisual>.FromAbi(contentPointer);
            RefreshContext();
            Marshal.ThrowExceptionForHR(NativeMethods.GDConfigureTypographyV2(native,(uint)FontProfile.SystemA,TypographyCatalog.Revision,null));
        }
        catch
        {
            materialSession?.MarkFault("native composition initialization failed");
            NativeMethods.GDDestroy(native);native=0;Handle=0;throw;
        }
        finally
        {
            if(compositorPointer!=0)Marshal.Release(compositorPointer);
            if(contentPointer!=0)Marshal.Release(contentPointer);
        }
    }

    private void CheckThread()
    {
        ObjectDisposedException.ThrowIf(disposed,this);
        if(Environment.CurrentManagedThreadId!=ownerThread)
            throw new InvalidOperationException("Composition and host operations must run on the owning UI thread.");
    }
    private void RefreshContext()
    {
        Marshal.ThrowExceptionForHR(NativeMethods.GDGetContext(native,out var value));
        var next=new HostContext(value.X,value.Y,value.Width,value.Height,value.Dpi);
        if(next==context)return;
        context=next;WindowContextChanged?.Invoke(next);
    }
    private void OnNativeEvent(nint unused,nint pointer)
    {
        if(disposed||native==0)return;
        try
        {
            var value=Marshal.PtrToStructure<NativeMethods.Pointer>(pointer);
            if(value.Kind==100||value.Kind==(int)HostEventKind.DpiChanged)
                RefreshContext();
            if(value.Kind==100)return;
            if(value.Kind==(int)HostEventKind.SystemSettingsChanged&&value.ControlId==-3){
                string reason=$"native experimental desktop material tripped: 0x{unchecked((uint)value.Value):X8}";
                MaterialSession?.MarkFault(reason);
                NativeError?.Invoke(reason);return;
            }
            if(value.Kind==(int)HostEventKind.SystemSettingsChanged&&value.ControlId==-2)
                NativeError?.Invoke($"native-window: 0x{unchecked((uint)value.Value):X8}");
            if(value.Kind==(int)HostEventKind.SystemSettingsChanged&&value.ControlId==-1)
                GraphicsDeviceRestored?.Invoke();
            PointerEvent?.Invoke(new((HostEventKind)value.Kind,value.X,value.Y,value.ScreenX,value.ScreenY,value.ControlId,value.Value));
        }
        catch(Exception error)
        {
            // A managed subscriber must not unwind into a Win32 WndProc.
            if(handlingCallbackFailure)return;
            handlingCallbackFailure=true;
            try
            {
                string reason=$"native-callback: {error.GetType().Name} (0x{error.HResult:X8})";
                // A queued coordinator teardown is too late for the remainder
                // of the synchronous window dispatch. Trip the native gate now.
                try{if(IsExperimentalDesktopMaterialCandidate)TripDesktopMaterial(reason);}catch{ }
                try{NativeError?.Invoke(reason);}catch{ }
            }
            finally{handlingCallbackFailure=false;}
        }
    }

    public void ApplyFrame(HostFrame frame)
    {
        CheckThread();
        if(pendingGlassScene is { Shapes.Length: >0 })
            throw new InvalidOperationException("A pending glass scene requires a matching positioned-frame commit.");
        pendingGlassScene=null;
        var value=new NativeMethods.Frame(frame.Width,frame.Height,frame.Radius,frame.Blur,frame.TintOpacity,frame.Solid?1:0);
        Marshal.ThrowExceptionForHR(NativeMethods.GDApplyFrameWithTension(native,in value,frame.SurfaceTension));RefreshContext();
    }
    /// <summary>Submits the DIP frame and physical screen position together.</summary>
    public void ApplyPositionedFrame(HostFrame frame,int screenX,int screenY)
        => ApplyContinuousPositionedFrame(frame,screenX,screenY);
    public void ApplyContinuousPositionedFrame(HostFrame frame,float screenX,float screenY)
    {
        CheckThread();
        if(pendingGlassScene is { Shapes.Length: >0 })
            throw new InvalidOperationException("Use the sequence overload when a glass shape snapshot is pending.");
        pendingGlassScene=null;
        var value=new NativeMethods.Frame(frame.Width,frame.Height,frame.Radius,frame.Blur,frame.TintOpacity,frame.Solid?1:0);
        int result;
        try {result=NativeMethods.GDApplyContinuousPositionedFrameWithTension(native,in value,frame.SurfaceTension,screenX,screenY);}
        catch(EntryPointNotFoundException error)
        {
            throw new InvalidOperationException("GlassDesk.Native.dll requires atomic frame placement; use the DLL from this candidate build.",error);
        }
        Marshal.ThrowExceptionForHR(result);
        RefreshContext();
    }
    public void SetPlacement(int screenX,int screenY)
    {CheckThread();Marshal.ThrowExceptionForHR(NativeMethods.GDSetPlacement(native,screenX,screenY));RefreshContext();}
    public void SetVisible(bool visible)
    {CheckThread();Marshal.ThrowExceptionForHR(NativeMethods.GDSetVisible(native,visible?1:0));}
    /// <summary>Reassert the visible widget's topmost band after a foreground change, without activation or showing a hidden window.</summary>
    public void EnsureTopmost()
    {
        CheckThread();
        if(!IsWindowVisible(Handle))return;
        const uint flags=0x0001|0x0002|0x0010|0x0200; // No size/move/activation/owner reordering.
        if(!SetWindowPos(Handle,new nint(-1),0,0,0,0,flags))Marshal.ThrowExceptionForHR(Marshal.GetHRForLastWin32Error());
    }
    public void SetLiquidGlassMotionActive(bool active)
    {
        CheckThread();
        Marshal.ThrowExceptionForHR(NativeMethods.GDSetLiquidGlassMotionActive(native,active?1:0));
    }
    public void SetLiquidGlassEnabled(bool enabled)
    {CheckThread();Marshal.ThrowExceptionForHR(NativeMethods.GDSetLiquidGlassEnabled(native,enabled?1:0));}

    /// <summary>Creates a rounded polygon with stable path topology for composition morphing.</summary>
    public unsafe CompositionPath CreatePolygonPath(ReadOnlySpan<Vector2> points,float rounding=.8f)
    {
        CheckThread();nint result=0;
        try
        {
            fixed(Vector2* pointer=points)
                Marshal.ThrowExceptionForHR(NativeMethods.GDCreateCompositionPath(native,pointer,(uint)points.Length,rounding,out result));
            return WinRT.MarshalInterface<CompositionPath>.FromAbi(result);
        }
        finally{if(result!=0)Marshal.Release(result);}
    }

    /// <summary>
    /// Explicit single-frame proof of the GPU material only, before content.
    /// Normal rendering does not read back pixels. Throws when no capture frame
    /// is ready; the diagnostic does not alter capture affinity or enablement.
    /// </summary>
    public void SaveLiquidGlassDiagnosticFrame(string path)
    {
        CheckThread();ArgumentException.ThrowIfNullOrWhiteSpace(path);
        Marshal.ThrowExceptionForHR(NativeMethods.GDSaveLiquidGlassDiagnosticFrame(native,Path.GetFullPath(path)));
    }

    /// <summary>
    /// Explicit QA only. Freezes an already-ready liquid material frame and stops
    /// its worker before allowing an external final-UI screenshot for 500–5000 ms.
    /// An independent watchdog restores both HWND affinities with readback; the
    /// existing UI tick alone resumes capture with a new shared texture. Does not
    /// save an image, freeze foreground animations, or change enablement intent.
    /// </summary>
    public void BeginDiagnosticSnapshot(int durationMilliseconds=3000)
    {
        CheckThread();
        if(durationMilliseconds is <500 or >5000)throw new ArgumentOutOfRangeException(nameof(durationMilliseconds));
        Marshal.ThrowExceptionForHR(NativeMethods.GDBeginDiagnosticSnapshot(native,(uint)durationMilliseconds));
    }

    /// <summary>Ends the QA window early. Safe when no window is active; throws on failed restoration.</summary>
    public void EndDiagnosticSnapshot()
    {
        CheckThread();Marshal.ThrowExceptionForHR(NativeMethods.GDEndDiagnosticSnapshot(native));
    }

    public unsafe void SetAccessibleControls(IReadOnlyList<AccessibleControl> controls,string name)
    {
        CheckThread();ArgumentNullException.ThrowIfNull(controls);ArgumentNullException.ThrowIfNull(name);
        var allocations=new List<nint>(controls.Count);
        var values=new NativeMethods.Accessible[controls.Count];
        try
        {
            for(int index=0;index<controls.Count;++index)
            {
                var value=controls[index];var text=Marshal.StringToHGlobalUni(value.Name);allocations.Add(text);
                values[index]=new(value.Id,text,value.X,value.Y,value.Width,value.Height,value.Enabled?1:0,value.IsRange?1:0,value.Value,value.Minimum,value.Maximum);
            }
            fixed(NativeMethods.Accessible* pointer=values)
                Marshal.ThrowExceptionForHR(NativeMethods.GDSetAccessible(native,name,pointer,(uint)values.Length));
        }
        finally{foreach(var allocation in allocations)Marshal.FreeHGlobal(allocation);}
    }

    /// <summary>
    /// Creates a caller-owned pixel surface rendered at the current monitor DPI.
    /// Use a Stretch.Fill surface brush on a visual whose Size is width/height DIP.
    /// Recreate when WindowContextChanged reports a new DPI.
    /// </summary>
    public unsafe CompositionDrawingSurface RenderSurface(float widthDip,float heightDip,IReadOnlyList<SurfaceOp> operations)
    {
        CheckThread();ArgumentNullException.ThrowIfNull(operations);
        var strings=new List<nint>();var handles=new List<GCHandle>();
        var values=new NativeMethods.OpV2[operations.Count];nint result=0;
        try
        {
            for(int index=0;index<operations.Count;++index)
            {
                var op=operations[index];nint text=0,image=0,locale=0;
                if(op.Text is not null){text=Marshal.StringToHGlobalUni(op.Text);strings.Add(text);}
                if(op.Image is {Length:>0}){var handle=GCHandle.Alloc(op.Image,GCHandleType.Pinned);handles.Add(handle);image=handle.AddrOfPinnedObject();}
                if(op.LocaleName is not null){locale=Marshal.StringToHGlobalUni(op.LocaleName);strings.Add(locale);}
                values[index]=new(op,text,image,locale);
            }
            fixed(NativeMethods.OpV2* pointer=values)
                Marshal.ThrowExceptionForHR(NativeMethods.GDRenderSurfaceV2(native,widthDip,heightDip,pointer,(uint)values.Length,96,out result));
            return WinRT.MarshalInterface<CompositionDrawingSurface>.FromAbi(result);
        }
        catch(Exception error){if(IsExperimentalDesktopMaterialCandidate)TripDesktopMaterial($"foreground surface: 0x{error.HResult:X8}");throw;}
        finally
        {
            if(result!=0)Marshal.Release(result);
            foreach(var text in strings)Marshal.FreeHGlobal(text);
            foreach(var handle in handles)handle.Free();
        }
    }

    public NativeTextMetrics MeasureText(SurfaceOp operation)
    {
        CheckThread();ArgumentNullException.ThrowIfNull(operation);
        nint text=0,locale=0;
        try
        {
            if(operation.Text is not null)text=Marshal.StringToHGlobalUni(operation.Text);
            if(operation.LocaleName is not null)locale=Marshal.StringToHGlobalUni(operation.LocaleName);
            var op=new NativeMethods.OpV2(operation,text,0,locale);
            Marshal.ThrowExceptionForHR(NativeMethods.GDMeasureTextV2(native,in op,96,out var metrics));
            return new(metrics.Advance,metrics.TrailingWidth,metrics.Height,metrics.Baseline,metrics.LineHeight,metrics.LineCount);
        }
        finally{if(text!=0)Marshal.FreeHGlobal(text);if(locale!=0)Marshal.FreeHGlobal(locale);}
    }

    public NativeInkBounds? MeasureTextInkBounds(SurfaceOp operation)
    {
        CheckThread();ArgumentNullException.ThrowIfNull(operation);
        string? textValue=operation.Text;
        if(operation.Kind is not (SurfaceOpKind.Text or SurfaceOpKind.MultilineText)||textValue is null||operation.TextRole==TextRole.Legacy)
            throw new ArgumentException("Ink bounds require a non-legacy text operation with text.",nameof(operation));
        if((TypographyAbi.Capabilities&NativeTypographyAbi.InkBoundsCapability)==0)return null;

        nint text=0,locale=0;
        try
        {
            text=Marshal.StringToHGlobalUni(textValue);
            if(operation.LocaleName is not null)locale=Marshal.StringToHGlobalUni(operation.LocaleName);
            var op=new NativeMethods.OpV2(operation,text,0,locale);
            Marshal.ThrowExceptionForHR(NativeMethods.GDMeasureTextInkBoundsV2(native,in op,96,out var bounds));
            if(!float.IsFinite(bounds.Left)||!float.IsFinite(bounds.Top)||!float.IsFinite(bounds.Right)||!float.IsFinite(bounds.Bottom)||
                bounds.Right<bounds.Left||bounds.Bottom<bounds.Top)
                throw new InvalidOperationException("GlassDesk.Native.dll returned invalid text ink bounds.");
            return new(bounds.Left,bounds.Top,bounds.Right,bounds.Bottom);
        }
        finally{if(text!=0)Marshal.FreeHGlobal(text);if(locale!=0)Marshal.FreeHGlobal(locale);}
    }

    public TypographyCacheStats TextCacheStats
    {
        get
        {
            CheckThread();Marshal.ThrowExceptionForHR(NativeMethods.GDGetTypographyCacheStats(native,out var stats));
            return new(stats.Formats,stats.Layouts,stats.FormatHits,stats.LayoutHits,stats.LayoutsCreated);
        }
    }

    public void Dispose()
    {
        if(disposed)return;CheckThread();disposed=true;
        pendingGlassScene=null;
        try{StopLiquidGlassCapture();}catch{}
        try{Marshal.ThrowExceptionForHR(NativeMethods.GDDestroy(native));}
        finally
        {
            ReleaseLiquidGlassInkColorBrush();
            native=0;Handle=0;compositor=null;contentRoot=null;
            PointerEvent=null;WindowContextChanged=null;NativeError=null;GraphicsDeviceRestored=null;
            GC.KeepAlive(callback);
        }
        GC.SuppressFinalize(this);
    }
}

internal static partial class NativeMethods
{
    private const string Library="GlassDesk.Native.dll";
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] internal delegate void Callback(nint user,nint pointer);
    [StructLayout(LayoutKind.Sequential)]internal readonly record struct Frame(float Width,float Height,float Radius,float Blur,float Tint,int Solid);
    [StructLayout(LayoutKind.Sequential)]internal readonly record struct Context(int X,int Y,float Width,float Height,uint Dpi);
    [StructLayout(LayoutKind.Sequential)]internal readonly record struct Pointer(int Kind,float X,float Y,int ScreenX,int ScreenY,int ControlId,double Value);
    [StructLayout(LayoutKind.Sequential)]internal readonly record struct Op(int Kind,float X,float Y,float Width,float Height,float Radius,float FontSize,uint Color,int Bold,int Center,nint Text,nint Image,uint ImageSize);
    [StructLayout(LayoutKind.Sequential)]internal readonly record struct Accessible(int Id,nint Name,float X,float Y,float Width,float Height,int Enabled,int IsRange,double Value,double Minimum,double Maximum);
    [DllImport(Library,ExactSpelling=true,CallingConvention=CallingConvention.StdCall)]internal static extern int GDCreate(Callback callback,nint user,out nint context,out nint window);
    [DllImport(Library,ExactSpelling=true,CallingConvention=CallingConvention.StdCall)]internal static extern int GDDestroy(nint context);
    [DllImport(Library,ExactSpelling=true,CallingConvention=CallingConvention.StdCall)]internal static extern int GDGetContext(nint context,out Context value);
    [DllImport(Library,ExactSpelling=true,CallingConvention=CallingConvention.StdCall)]internal static extern int GDGetComposition(nint context,out nint compositor,out nint content);
    [DllImport(Library,ExactSpelling=true,CallingConvention=CallingConvention.StdCall)]internal static extern unsafe int GDCreateCompositionPath(nint context,Vector2* points,uint count,float rounding,out nint path);
    [DllImport(Library,ExactSpelling=true,CallingConvention=CallingConvention.StdCall)]internal static extern int GDApplyFrame(nint context,in Frame frame);
    [DllImport(Library,ExactSpelling=true,CallingConvention=CallingConvention.StdCall)]internal static extern int GDApplyFrameWithTension(nint context,in Frame frame,float tension);
    [DllImport(Library,ExactSpelling=true,CallingConvention=CallingConvention.StdCall)]internal static extern int GDApplyContinuousPositionedFrameWithTension(nint context,in Frame frame,float tension,float screenX,float screenY);
    [DllImport(Library,ExactSpelling=true,CallingConvention=CallingConvention.StdCall)]internal static extern int GDApplyPositionedFrameWithTension(nint context,in Frame frame,float tension,int screenX,int screenY);
    [DllImport(Library,ExactSpelling=true,CallingConvention=CallingConvention.StdCall)]internal static extern int GDSetPlacement(nint context,int x,int y);
    [DllImport(Library,ExactSpelling=true,CallingConvention=CallingConvention.StdCall)]internal static extern int GDSetVisible(nint context,int visible);
    [DllImport(Library,ExactSpelling=true,CallingConvention=CallingConvention.StdCall)]internal static extern int GDSetLiquidGlassMotionActive(nint context,int active);
    [DllImport(Library,ExactSpelling=true,CallingConvention=CallingConvention.StdCall)]internal static extern int GDSetLiquidGlassEnabled(nint context,int enabled);
    [DllImport(Library,ExactSpelling=true,CallingConvention=CallingConvention.StdCall)]internal static extern unsafe int GDGetLiquidGlassStatus(nint context,char* text,uint capacity);
    [DllImport(Library,ExactSpelling=true,CallingConvention=CallingConvention.StdCall,CharSet=CharSet.Unicode)]internal static extern int GDSaveLiquidGlassDiagnosticFrame(nint context,string path);
    [DllImport(Library,ExactSpelling=true,CallingConvention=CallingConvention.StdCall)]internal static extern int GDBeginDiagnosticSnapshot(nint context,uint durationMilliseconds);
    [DllImport(Library,ExactSpelling=true,CallingConvention=CallingConvention.StdCall)]internal static extern int GDEndDiagnosticSnapshot(nint context);
    [DllImport(Library,ExactSpelling=true,CallingConvention=CallingConvention.StdCall)]internal static extern unsafe int GDGetDiagnosticSnapshotStatus(nint context,char* text,uint capacity);
    [DllImport(Library,ExactSpelling=true,CallingConvention=CallingConvention.StdCall,CharSet=CharSet.Unicode)]internal static extern unsafe int GDSetAccessible(nint context,string name,Accessible* items,uint count);
    [DllImport(Library,ExactSpelling=true,CallingConvention=CallingConvention.StdCall)]internal static extern unsafe int GDRenderSurface(nint context,float width,float height,Op* operations,uint count,out nint surface);
}

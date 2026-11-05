using System.Runtime.InteropServices;

namespace GlassDesk.NativeBridge;

/// <summary>Settings-owned procedural scene. No desktop input or pixel readback.</summary>
public sealed class NativeLiquidScene : IDisposable
{
    readonly int _thread = Environment.CurrentManagedThreadId;
    nint _context;
    public NativeLiquidScene(nint panel)
        => Marshal.ThrowExceptionForHR(GDCreateLocalGlass(panel, out _context));
    public unsafe void Render(ReadOnlySpan<float> frame)
    {
        if (_thread != Environment.CurrentManagedThreadId) throw new InvalidOperationException("Local glass is UI-thread owned.");
        ObjectDisposedException.ThrowIf(_context == 0, this);
        if (frame.Length != 64) throw new ArgumentException("The local scene frame has 64 floats.", nameof(frame));
        fixed (float* data = frame) Marshal.ThrowExceptionForHR(GDRenderLocalGlass(_context, data, 64));
    }
    public unsafe void SetImage(byte[]? pixels,int width,int height)
    {
        if(_thread!=Environment.CurrentManagedThreadId)throw new InvalidOperationException("Local glass is UI-thread owned.");
        ObjectDisposedException.ThrowIf(_context==0,this);
        if(pixels is not null&&(width<1||height<1||width>1600||height>1600||pixels.Length!=width*height*4))throw new ArgumentException("Invalid local image.");
        fixed(byte* p=pixels)Marshal.ThrowExceptionForHR(GDSetLocalGlassImage(_context,p,(uint)width,(uint)height,(uint)(pixels?.Length??0)));
    }
    [DllImport("GlassDesk.Native.dll",ExactSpelling=true,CallingConvention=CallingConvention.StdCall)]
    static extern unsafe int GDSetLocalGlassImage(nint context,byte* pixels,uint width,uint height,uint bytes);
    public void Dispose()
    { if (_context == 0) return; if (_thread != Environment.CurrentManagedThreadId) throw new InvalidOperationException("Dispose on the owning UI thread."); GDDestroyLocalGlass(_context); _context = 0; }
    [DllImport("GlassDesk.Native.dll", ExactSpelling = true, CallingConvention = CallingConvention.StdCall)]
    static extern int GDCreateLocalGlass(nint panel, out nint context);
    [DllImport("GlassDesk.Native.dll", ExactSpelling = true, CallingConvention = CallingConvention.StdCall)]
    static extern unsafe int GDRenderLocalGlass(nint context, float* frame, uint count);
    [DllImport("GlassDesk.Native.dll", ExactSpelling = true, CallingConvention = CallingConvention.StdCall)]
    static extern void GDDestroyLocalGlass(nint context);
}

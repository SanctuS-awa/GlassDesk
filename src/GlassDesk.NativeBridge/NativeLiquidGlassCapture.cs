using System.Runtime.InteropServices;

namespace GlassDesk.NativeBridge;

/// <summary>
/// Managed wrapper for the rewritten capture-based liquid glass plugin
/// (native/GlassDesk.Native/LiquidGlassCapture.cpp). The capture object is
/// independent of the old DWM shared-surface route: it captures only the
/// rectangle behind the widget with the platform monitor-capture API, keeps every
/// frame on the GPU, and applies the refraction shader in the native DLL.
/// When the native object reports a tripped fuse (or is disabled), the caller
/// falls back to the existing ordinary frosted material; this class never
/// throws for a tripped fuse, it just reports <see cref="IsTripped"/>.
/// </summary>
public sealed class NativeLiquidGlassCapture : IDisposable
{
    nint handle;
    bool disposed;

    public NativeLiquidGlassCapture(nint widgetWindow, bool enabled)
    {
        ArgumentOutOfRangeException.ThrowIfZero(widgetWindow);
        Marshal.ThrowExceptionForHR(GdLiquidGlass_Create(widgetWindow, enabled ? 1 : 0, out handle));
        if (handle == 0) throw new InvalidOperationException("Liquid glass capture was not created.");
    }

    /// <summary>True when the capture shader currently wants dark text ink.</summary>
    public bool InkIsDark
    {
        get
        {
            if (handle == 0) return false;
            Marshal.ThrowExceptionForHR(GdLiquidGlass_GetInkIsDark(handle, out int dark));
            return dark != 0;
        }
    }

    public void SetEnabled(bool enabled)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        Marshal.ThrowExceptionForHR(GdLiquidGlass_SetEnabled(handle, enabled ? 1 : 0));
    }

    public void Update(int screenX, int screenY, int widthPixels, int heightPixels, float radiusPixels, float dpiScale)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        Marshal.ThrowExceptionForHR(GdLiquidGlass_Update(handle, screenX, screenY, widthPixels, heightPixels, radiusPixels, dpiScale));
    }

    public void ResetFuse()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        Marshal.ThrowExceptionForHR(GdLiquidGlass_ResetFuse(handle));
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        if (handle != 0) { GdLiquidGlass_Destroy(handle); handle = 0; }
    }

    [DllImport("GlassDesk.Native.dll", ExactSpelling = true, CallingConvention = CallingConvention.StdCall)]
    static extern int GdLiquidGlass_Create(nint widgetWindow, int enabled, out nint handle);
    [DllImport("GlassDesk.Native.dll", ExactSpelling = true, CallingConvention = CallingConvention.StdCall)]
    static extern int GdLiquidGlass_Update(nint handle, int screenX, int screenY, int widthPixels, int heightPixels, float radiusPixels, float dpiScale);
    [DllImport("GlassDesk.Native.dll", ExactSpelling = true, CallingConvention = CallingConvention.StdCall)]
    static extern int GdLiquidGlass_SetEnabled(nint handle, int enabled);
    [DllImport("GlassDesk.Native.dll", ExactSpelling = true, CallingConvention = CallingConvention.StdCall)]
    static extern int GdLiquidGlass_Destroy(nint handle);
    [DllImport("GlassDesk.Native.dll", ExactSpelling = true, CallingConvention = CallingConvention.StdCall)]
    static extern int GdLiquidGlass_ResetFuse(nint handle);
    [DllImport("GlassDesk.Native.dll", ExactSpelling = true, CallingConvention = CallingConvention.StdCall)]
    static extern int GdLiquidGlass_GetInkIsDark(nint handle, out int isDark);
}

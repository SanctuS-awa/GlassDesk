using GlassDesk.NativeBridge;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using System.Runtime.InteropServices;

namespace GlassDesk.App;

/// <summary>One local GPU scene underneath independent, crisp XAML content.</summary>
internal sealed class SettingsGlassSurface : SwapChainPanel, IDisposable
{
    NativeLiquidScene? _scene;
    float[]? _frame;
    SettingsPreviewImage? _image;
    bool _active = true, _disposed, _failed;
    public event Action<string>? Failed;
    public SettingsGlassSurface()
    {
        IsHitTestVisible = false;
        Loaded += (_, _) => Draw();
        Unloaded += (_, _) => Release();
        CompositionScaleChanged += (_, _) => Draw();
        SizeChanged += (_, _) => Draw();
    }
    public void SetImage(SettingsPreviewImage? image) { if(ReferenceEquals(_image,image))return; _image=image; try { if(_scene is not null)_scene.SetImage(image?.Pixels,image?.Width??0,image?.Height??0); Draw(); } catch(Exception error) { _failed=true;Release();Failed?.Invoke($"本地图片透镜暂不可用（0x{error.HResult:X8}）。"); } }
    public void Present(float[] frame) { if (_frame is not null && frame.AsSpan(3).SequenceEqual(_frame.AsSpan(3))) return; _frame = frame; Draw(); }
    public void SetActive(bool active)
    { _active = active; if (active) { _failed = false; Draw(); } else Release(); }
    void Draw()
    {
        if (_disposed || _failed || !_active || !IsLoaded || _frame is null || ActualWidth < 8 || ActualHeight < 8) return;
        try
        {
            if (_scene is null)
            {
                nint pointer = WinRT.MarshalInspectable<SwapChainPanel>.FromManaged(this);
                try { _scene = new NativeLiquidScene(pointer); if(_image is not null)_scene.SetImage(_image.Pixels,_image.Width,_image.Height); }
                finally { if (pointer != 0) Marshal.Release(pointer); }
            }
            _frame[0] = (float)ActualWidth; _frame[1] = (float)ActualHeight;
            _frame[2] = Math.Clamp(CompositionScaleX, .5f, 3f);
            _scene.Render(_frame);
        }
        catch (Exception error)
        { _failed = true; Release(); Failed?.Invoke($"本地透镜暂不可用（0x{error.HResult:X8}）。"); }
    }
    void Release() { _scene?.Dispose(); _scene = null; }
    public void Dispose() { if (_disposed) return; _disposed = true; Release(); }
}

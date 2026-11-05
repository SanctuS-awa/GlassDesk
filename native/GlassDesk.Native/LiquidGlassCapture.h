#pragma once

// LiquidGlassCapture: rewritten capture-based liquid glass plugin (0.2.1.1beta).
//
// Replaces the deleted DWM private-ordinal shared-surface route entirely.
// It captures only the rectangle behind the widget with Windows.Graphics.
// Capture (IGraphicsCaptureItemInterop::CreateForMonitor), keeps every frame
// on the GPU (ID3D11Texture2D + CopySubresourceRegion), and refracts it with
// an HLSL shader compiled at runtime with D3DCompile. No CPU readback:
// no Map(READ), no STAGING texture (the old STAGING path caused NVIDIA
// black-screen restarts, and is banned here by design).
//
// Presentation uses a BGRA premultiplied-alpha Composition swap chain on the
// host D3D11 device. Capture, refraction, and presentation remain GPU-only.
#define NOMINMAX
#include <windows.h>
#include <cstdint>

struct ID3D11Device;
struct ID3D11DeviceContext;

namespace gd::liquidglass
{

struct CaptureRect
{
    int screenX{};
    int screenY{};
    int widthPixels{};
    int heightPixels{};
    float radiusPixels{};
    float dpiScale{1.0f};
};

class LiquidGlassCapture final
{
public:
    LiquidGlassCapture(HWND widgetWindow, bool enabled, ::ID3D11Device* sharedDevice = nullptr,
                       ::ID3D11DeviceContext* sharedContext = nullptr);
    LiquidGlassCapture(HWND widgetWindow, HWND contentWindow, HWND shadowWindow, bool enabled,
                       ::ID3D11Device* sharedDevice, ::ID3D11DeviceContext* sharedContext);
    ~LiquidGlassCapture();
    LiquidGlassCapture(LiquidGlassCapture const&) = delete;
    LiquidGlassCapture& operator=(LiquidGlassCapture const&) = delete;

    // Returns S_OK on success. S_FALSE means "not active right now" (disabled,
    // fullscreen, fully occluded, minimized, or fuse tripped): the caller must
    // show ordinary frosted material in that case. Never throws.
    HRESULT Update(CaptureRect const& rect) noexcept;
    HRESULT SetEnabled(bool enabled) noexcept;
    // Clears a tripped fuse (device-error counter). A crash does not trip the
    // fuse because the counter lives only in memory for this process run.
    HRESULT ResetFuse() noexcept;
    bool InkIsDark() const noexcept;
    bool FuseTripped() const noexcept;
    // Borrowed Composition swap chains (GPU only), null before the first frame.
    void* OutputSwapChain() const noexcept;
    void* RefractionSwapChain() const noexcept;
    void* TintSwapChain() const noexcept;
    void* InkColorSwapChain() const noexcept;
    bool HasPresentedFrame() const noexcept;

private:
    struct Impl;
    Impl* impl_{nullptr};
};

} // namespace gd::liquidglass

// Standalone C ABI used by GlassDesk.NativeBridge (NativeLiquidGlassCapture.cs).
extern "C" __declspec(dllexport) HRESULT __stdcall GdLiquidGlass_Create(HWND widgetWindow, int enabled, void** handle);
extern "C" __declspec(dllexport) HRESULT __stdcall GdLiquidGlass_Update(void* handle, int screenX, int screenY, int widthPixels, int heightPixels, float radiusPixels, float dpiScale);
extern "C" __declspec(dllexport) HRESULT __stdcall GdLiquidGlass_SetEnabled(void* handle, int enabled);
extern "C" __declspec(dllexport) HRESULT __stdcall GdLiquidGlass_Destroy(void* handle);
extern "C" __declspec(dllexport) HRESULT __stdcall GdLiquidGlass_ResetFuse(void* handle);
extern "C" __declspec(dllexport) HRESULT __stdcall GdLiquidGlass_GetInkIsDark(void* handle, int* isDark);

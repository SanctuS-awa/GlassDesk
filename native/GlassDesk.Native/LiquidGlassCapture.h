#pragma once

// LiquidGlassCapture: GPU-only WGC liquid glass material (0.2.1.3beta, plugin 0.3).
//
// Replaces the deleted DWM private-ordinal shared-surface route entirely.
// It captures only the rectangle behind the widget with Windows.Graphics.
// Capture (IGraphicsCaptureItemInterop::CreateForMonitor), keeps every frame
// on the GPU (ID3D11Texture2D + shader crop), and refracts it with
// an HLSL shader compiled at runtime with D3DCompile. No CPU readback:
// no Map(READ), no STAGING texture (the old STAGING path caused NVIDIA
// black-screen restarts, and is banned here by design).
//
// Presentation uses a BGRA premultiplied-alpha Composition swap chain on the
// host D3D11 device. Capture, refraction, and presentation remain GPU-only.
#define NOMINMAX
#include <windows.h>
#include <cstdint>
#include "NativeApi.h"

struct ID3D11Device;
struct ID3D11DeviceContext;

struct LiquidGlassMaterialSettings
{
    uint32_t tintArgb{0xffffffffu};
    float opacity{1.0f};
    float thin{0.06f};
    float dense{0.22f};
    float blur{2.6f};
    float saturation{1.08f};
    float brightness{1.0f};
    float contrast{1.0f};
    float rimLight{1.0f};
    float bevel{1.0f};
    float shadow{1.0f};
};

struct GDLiquidGlassSettings
{
    uint32_t size{};
    uint32_t version{1};
    int32_t theme{2}; // light=0, dark=1, follow system=2
    int32_t ink{0};   // follow theme=0, dark=1, light=2
    uint32_t autoDensity{1};
    uint32_t lensEnabled{1};
    LiquidGlassMaterialSettings light{};
    LiquidGlassMaterialSettings dark{0xff1e1e22u,1.0f,0.035f,0.14f,1.4f,1.03f,1.0f,1.0f,1.0f,1.0f,1.0f};
    float rimMaxPixels{56.0f};
    float edge{1.55f};
    float power{3.2f};
    float dispersion{0.030f};
    float centerDispersion{0.0f};
    float density{0.35f};
    float threshold{0.5f};
};
static_assert(sizeof(LiquidGlassMaterialSettings) == 44);
static_assert(sizeof(GDLiquidGlassSettings) == 140);

namespace gd::liquidglass
{

struct CaptureRect
{
    float screenX{};
    float screenY{};
    int widthPixels{};
    int heightPixels{};
    float radiusPixels{};
    float dpiScale{1.0f};
    float shapeWidthPixels{}, shapeHeightPixels{}, surfaceTension{};
    bool motionActive{};
    uint64_t sequence{};
    GDGlassShapeV1 const* glassShapes{};
    uint32_t glassShapeCount{};
};

struct CaptureDiagnosticSnapshot
{
    uint64_t captureGeneration{};
    uint32_t sourceFormat{};
    uint32_t sourceWidth{},sourceHeight{},contentWidth{},contentHeight{};
    uint32_t activeWidth{},activeHeight{},capacityWidth{},capacityHeight{};
    uint32_t monitorWidth{},monitorHeight{};
    int32_t paddingPixels{};
    bool presented{};
};

enum class CaptureFailureStage : uint8_t
{
    None,
    ValidateGeometry,
    ReadWindowBounds,
    StartSession,
    AcquireFrame,
    ReadCaptureSurface,
    ValidateCaptureGeometry,
    CreateCaptureTexture,
    ReadMonitorBounds,
    CropCapture,
    ResizeOutput,
    PrepareShader,
    DrawMetrics,
    PresentFrame,
    Other
};

constexpr wchar_t const* CaptureFailureStageName(CaptureFailureStage stage) noexcept
{
    switch (stage)
    {
    case CaptureFailureStage::None: return L"none";
    case CaptureFailureStage::ValidateGeometry: return L"validate-geometry";
    case CaptureFailureStage::ReadWindowBounds: return L"read-window-bounds";
    case CaptureFailureStage::StartSession: return L"start-session";
    case CaptureFailureStage::AcquireFrame: return L"acquire-frame";
    case CaptureFailureStage::ReadCaptureSurface: return L"read-capture-surface";
    case CaptureFailureStage::ValidateCaptureGeometry: return L"validate-capture-geometry";
    case CaptureFailureStage::CreateCaptureTexture: return L"create-capture-texture";
    case CaptureFailureStage::ReadMonitorBounds: return L"read-monitor-bounds";
    case CaptureFailureStage::CropCapture: return L"crop-capture";
    case CaptureFailureStage::ResizeOutput: return L"resize-output";
    case CaptureFailureStage::PrepareShader: return L"prepare-shader";
    case CaptureFailureStage::DrawMetrics: return L"draw-metrics";
    case CaptureFailureStage::PresentFrame: return L"present-frame";
    default: return L"other";
    }
}

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
    // fullscreen, fully occluded, minimized, or fuse tripped). Geometry
    // validation failures retain the last GPU output for a synchronized retry;
    // the caller must not turn them into a permanent backend failure. Never throws.
    HRESULT Update(CaptureRect const& rect) noexcept;
    CaptureFailureStage LastFailureStage() const noexcept;
    CaptureDiagnosticSnapshot GetDiagnostics() const noexcept;
    bool IsRecoverableGeometryFailure(HRESULT failure) const noexcept;
    HRESULT SetEnabled(bool enabled) noexcept;
    HRESULT SetSettings(GDLiquidGlassSettings const& settings) noexcept;
    // Clears a tripped fuse (device-error counter). A crash does not trip the
    // fuse because the counter lives only in memory for this process run.
    HRESULT ResetFuse() noexcept;
    bool InkIsDark() const noexcept;
    float ShadowScale() const noexcept;
    float DarkThemeWeight() const noexcept;
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
extern "C" __declspec(dllexport) HRESULT __stdcall GdLiquidGlass_SetSettings(void* handle, GDLiquidGlassSettings const* settings);
extern "C" __declspec(dllexport) HRESULT __stdcall GdLiquidGlass_Destroy(void* handle);
extern "C" __declspec(dllexport) HRESULT __stdcall GdLiquidGlass_ResetFuse(void* handle);
extern "C" __declspec(dllexport) HRESULT __stdcall GdLiquidGlass_GetInkIsDark(void* handle, int* isDark);

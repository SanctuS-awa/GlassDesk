#define NOMINMAX
#include "LiquidGlassCapture.h"

#include <windows.h>
#include <dwmapi.h>
#include <shellapi.h>
#include <shobjidl.h>
#include <d3d11.h>
#include <d3dcompiler.h>
#include <dxgi.h>
#include <dxgi1_2.h>
#include <winrt/Windows.Foundation.h>
#include <winrt/Windows.Foundation.Collections.h>
#include <winrt/Windows.Security.Authorization.AppCapabilityAccess.h>
#include <winrt/Windows.Graphics.Capture.h>
#include <winrt/Windows.Graphics.DirectX.h>
#include <winrt/Windows.Graphics.DirectX.Direct3D11.h>
#include <winrt/Windows.UI.h>
#include <windows.graphics.capture.interop.h>
#include <windows.graphics.directx.direct3d11.interop.h>
#include <windows.ui.interop.h>

#include <algorithm>
#include <array>
#include <atomic>
#include <cmath>
#include <cstdint>
#include <cstring>
#include <memory>
#include <string>
#include <vector>

#include <wrl/client.h>

namespace gd::liquidglass
{
namespace
{
using Microsoft::WRL::ComPtr;
using namespace winrt::Windows::Graphics::Capture;
using namespace winrt::Windows::Graphics::DirectX;
using namespace winrt::Windows::Graphics::DirectX::Direct3D11;

constexpr float kRimDip = 44.0f;      // refraction band width (scaled by DPI)
constexpr float kMaxRefractionScale = 0.35f;
constexpr float kMaxDispersionDip = 2.5f;
constexpr float kMaxBlurRadiusPx = 2.5f;
constexpr int kFuseLimit = 3;
constexpr double kFrameIntervalMs = 1000.0 / 60.0;

using AppCapabilityAccessStatus = winrt::Windows::Security::Authorization::AppCapabilityAccess::AppCapabilityAccessStatus;

enum class BorderlessAccessState : int
{
    NotRequested,
    Pending,
    Allowed,
    Denied,
    Failed
};

struct BorderlessAccessResult
{
    std::atomic<BorderlessAccessState> state{BorderlessAccessState::NotRequested};
    std::atomic<HRESULT> error{E_PENDING};
};

bool IsDeviceError(HRESULT hr) noexcept
{
    return hr == DXGI_ERROR_DEVICE_REMOVED || hr == DXGI_ERROR_DEVICE_RESET ||
           hr == DXGI_ERROR_DEVICE_HUNG || hr == DXGI_ERROR_DRIVER_INTERNAL_ERROR;
}

bool GetPhysicalWindowBounds(HWND window, RECT& bounds) noexcept
{
    if (window == nullptr) return false;
    const HRESULT frameResult = DwmGetWindowAttribute(window, DWMWA_EXTENDED_FRAME_BOUNDS,
        &bounds, sizeof(bounds));
    return SUCCEEDED(frameResult) || GetWindowRect(window, &bounds) != FALSE;
}

struct OcclusionState
{
    HWND widget{};
    HWND content{};
    HWND shadow{};
    RECT widgetRect{};
    bool allocationFailed{};
    // Only windows enumerated *before* the widget (i.e. above it in z-order)
    // can cover it. Windows below it are the background to refract, not
    // occluders. Returning FALSE at the widget stops EnumWindows there.
    std::vector<RECT> occluders{};
};

BOOL CALLBACK EnumOcclusion(HWND window, LPARAM param) noexcept
{
    auto* state = reinterpret_cast<OcclusionState*>(param);
    if (window == state->widget) return FALSE;
    if (window == state->content || window == state->shadow) return TRUE;
    if (!IsWindowVisible(window)) return TRUE;
    WINDOWPLACEMENT placement{};
    placement.length = sizeof(placement);
    if (GetWindowPlacement(window, &placement) && placement.showCmd == SW_SHOWMINIMIZED) return TRUE;
    UINT cloaked = 0;
    if (SUCCEEDED(DwmGetWindowAttribute(window, DWMWA_CLOAKED, &cloaked, sizeof(cloaked))) && cloaked != 0) return TRUE;
    RECT bounds{};
    if (FAILED(DwmGetWindowAttribute(window, DWMWA_EXTENDED_FRAME_BOUNDS, &bounds, sizeof(bounds)))) return TRUE;
    RECT intersection{};
    if (!IntersectRect(&intersection, &bounds, &state->widgetRect)) return TRUE;
    try
    {
        state->occluders.push_back(intersection);
    }
    catch (...)
    {
        state->allocationFailed = true;
        return FALSE;
    }
    return TRUE;
}

float OcclusionRatio(HWND widget, HWND content, HWND shadow, RECT const& widgetRect) noexcept
{
    try
    {
        OcclusionState state{};
        state.widget = widget;
        state.content = content;
        state.shadow = shadow;
        state.widgetRect = widgetRect;
        EnumWindows(EnumOcclusion, reinterpret_cast<LPARAM>(&state));
        if (state.allocationFailed) return 1.0f;
        const uint64_t width = static_cast<uint64_t>(std::max<int64_t>(0,
            static_cast<int64_t>(widgetRect.right) - static_cast<int64_t>(widgetRect.left)));
        const uint64_t height = static_cast<uint64_t>(std::max<int64_t>(0,
            static_cast<int64_t>(widgetRect.bottom) - static_cast<int64_t>(widgetRect.top)));
        const uint64_t total = width * height;
        if (total == 0 || state.occluders.empty()) return 0.0f;
        // Union area, not a sum: overlapping occluders must not double-count.
        // Coordinate compression over the widget rect and all occluder edges.
        std::vector<LONG> xs{widgetRect.left, widgetRect.right};
        std::vector<LONG> ys{widgetRect.top, widgetRect.bottom};
        for (auto const& r : state.occluders) { xs.push_back(r.left); xs.push_back(r.right); ys.push_back(r.top); ys.push_back(r.bottom); }
        std::sort(xs.begin(), xs.end());
        std::sort(ys.begin(), ys.end());
        xs.erase(std::unique(xs.begin(), xs.end()), xs.end());
        ys.erase(std::unique(ys.begin(), ys.end()), ys.end());
        uint64_t covered = 0;
        for (size_t xi = 0; xi + 1 < xs.size(); ++xi)
        {
            for (size_t yi = 0; yi + 1 < ys.size(); ++yi)
            {
                const LONG cx = xs[xi];
                const LONG cy = ys[yi];
                const uint64_t cellWidth = static_cast<uint64_t>(
                    static_cast<int64_t>(xs[xi + 1]) - static_cast<int64_t>(xs[xi]));
                const uint64_t cellHeight = static_cast<uint64_t>(
                    static_cast<int64_t>(ys[yi + 1]) - static_cast<int64_t>(ys[yi]));
                bool hit = false;
                for (auto const& r : state.occluders)
                {
                    if (cx >= r.left && cx < r.right && cy >= r.top && cy < r.bottom) { hit = true; break; }
                }
                if (hit) covered += cellWidth * cellHeight;
            }
        }
        const double ratio = static_cast<double>(covered) / static_cast<double>(total);
        return static_cast<float>(std::clamp(ratio, 0.0, 1.0));
    }
    catch (...)
    {
        return 1.0f;
    }
}

bool IsFullscreenForeground(HWND widget, HWND content) noexcept
{
    const HWND foreground = GetForegroundWindow();
    if (foreground == nullptr || foreground == widget || foreground == content) return false;
    wchar_t className[256]{};
    GetClassNameW(foreground, className, 256);
    if (lstrcmpW(className, L"Progman") == 0 || lstrcmpW(className, L"WorkerW") == 0 ||
        lstrcmpW(className, L"Shell_TrayWnd") == 0) return false;
    // Notification state is a real input, not a discarded hint: a fullscreen
    // app (D3D fullscreen / presentation / busy) counts even if its reported
    // rect is a pixel short, and geometry covering the monitor counts even
    // when the notification state is ordinary (borderless windowed games).
    QUERY_USER_NOTIFICATION_STATE notificationState = QUNS_NOT_PRESENT;
    const bool notificationFullscreen =
        SUCCEEDED(SHQueryUserNotificationState(&notificationState)) &&
        (notificationState == QUNS_BUSY || notificationState == QUNS_RUNNING_D3D_FULL_SCREEN ||
         notificationState == QUNS_PRESENTATION_MODE);
    if (notificationFullscreen) return true;
    RECT windowRect{};
    if (!GetWindowRect(foreground, &windowRect)) return false;
    const HMONITOR monitor = MonitorFromWindow(foreground, MONITOR_DEFAULTTONEAREST);
    MONITORINFO info{};
    info.cbSize = sizeof(info);
    if (!GetMonitorInfoW(monitor, &info)) return false;
    const RECT& screen = info.rcMonitor;
    return windowRect.left <= screen.left && windowRect.top <= screen.top &&
           windowRect.right >= screen.right && windowRect.bottom >= screen.bottom;
}

// The capture crop, adaptive material metrics, refraction, tint, and ink are
// separate GPU passes. Native composition supplies shadow and the existing
// animated rim-light/content visuals as independent layers.
char const* kVertexShader = R"HLSL(
struct VSOut { float4 pos : SV_POSITION; };
VSOut VS(uint id : SV_VertexID) {
    VSOut o;
    float2 p = float2((id << 1) & 2, id & 2);
    o.pos = float4(p * 2.0 - 1.0, 0.0, 1.0);
    return o;
}
)HLSL";

char const* kCaptureShader = R"HLSL(
Texture2D<float4> displayTex : register(t0);
SamplerState samp : register(s0);
cbuffer CropParams : register(b0) {
    float2 cropScreenOrigin;
    float2 monitorOrigin;
    float2 monitorSize;
    float2 contentSize;
    float2 sourceTextureSize;
    float2 paramsPadding;
};
float4 CropPS(float4 pos : SV_POSITION) : SV_TARGET {
    float2 screen = cropScreenOrigin + pos.xy;
    float2 minimum = monitorOrigin + 0.5;
    float2 maximum = monitorOrigin + monitorSize - 0.5;
    screen = clamp(screen, minimum, maximum);
    float2 sourcePixel = (screen - monitorOrigin) * contentSize / monitorSize;
    sourcePixel = clamp(sourcePixel, 0.5, contentSize - 0.5);
    float2 uv = sourcePixel / sourceTextureSize;
    return displayTex.SampleLevel(samp, uv, 0);
}
)HLSL";

char const* kMetricsShader = R"HLSL(
Texture2D<float4> captureTex : register(t0);
Texture2D<float4> previousStats : register(t1);
SamplerState samp : register(s0);
cbuffer MetricsParams : register(b0) {
    float2 widgetOrigin;
    float2 widgetSize;
    float2 captureSize;
    float smoothing;
    float initialized;
};
struct MetricsOut { float4 stats : SV_TARGET0; float4 ink : SV_TARGET1; };
MetricsOut MetricsPS(float4 pos : SV_POSITION) {
    float lum[9];
    float valid[9];
    float3 sumRgb = 0.0;
    float sumLum = 0.0;
    float sumLumSquared = 0.0;
    float validCount = 0.0;
    [unroll]
    for (int y = 0; y < 3; ++y) {
        [unroll]
        for (int x = 0; x < 3; ++x) {
            int index = y * 3 + x;
            float2 local = widgetSize * (float2((float)x, (float)y) + 0.5) / 3.0;
            float2 uv = clamp((widgetOrigin + local) / captureSize, 0.5 / captureSize, 1.0 - 0.5 / captureSize);
            float4 sample = captureTex.SampleLevel(samp, uv, 0);
            float coverage = saturate(sample.a);
            valid[index] = coverage > 0.0001 ? 1.0 : 0.0;
            float3 color = valid[index] > 0.5 ? sample.rgb / coverage : 0.0;
            lum[index] = dot(color, float3(0.2126, 0.7152, 0.0722));
            sumRgb += color * valid[index];
            sumLum += lum[index] * valid[index];
            sumLumSquared += lum[index] * lum[index] * valid[index];
            validCount += valid[index];
        }
    }
    float edgeDelta = 0.0;
    float edgeCount = 0.0;
    [unroll]
    for (int row = 0; row < 3; ++row) {
        [unroll]
        for (int column = 0; column < 3; ++column) {
            int index = row * 3 + column;
            if (column < 2) {
                if (valid[index] > 0.5 && valid[index + 1] > 0.5) {
                    edgeDelta += abs(lum[index] - lum[index + 1]);
                    edgeCount += 1.0;
                }
            }
            if (row < 2) {
                if (valid[index] > 0.5 && valid[index + 3] > 0.5) {
                    edgeDelta += abs(lum[index] - lum[index + 3]);
                    edgeCount += 1.0;
                }
            }
        }
    }
    float sampleCount = max(validCount, 1.0);
    float meanLum = sumLum / sampleCount;
    float variance = max(0.0, sumLumSquared / sampleCount - meanLum * meanLum);
    float meanEdgeDelta = edgeCount > 0.0 ? edgeDelta / edgeCount : 0.0;
    float complexity = saturate(variance * 7.0 + meanEdgeDelta * 1.5);
    float4 current = float4(sumRgb / sampleCount, complexity);
    float4 previous = previousStats.SampleLevel(samp, float2(0.5, 0.5), 0);
    float4 smoothed = initialized > 0.5 ? lerp(previous, current, smoothing) : current;
    float luma = dot(smoothed.rgb, float3(0.2126, 0.7152, 0.0722));
    float lightInkWeight = 1.0 - smoothstep(0.42, 0.58, luma);
    float3 darkInk = saturate(smoothed.rgb * 0.20 + float3(0.015, 0.020, 0.030));
    float3 lightInk = saturate(float3(0.90, 0.93, 1.0) * 0.78 + smoothed.rgb * 0.22);
    MetricsOut output;
    output.stats = smoothed;
    output.ink = float4(lerp(darkInk, lightInk, lightInkWeight), 1.0);
    return output;
}
)HLSL";

char const* kPixelShader = R"HLSL(
Texture2D<float4> captureTex : register(t0);
Texture2D<float4> metricsTex : register(t1);
SamplerState samp : register(s0);
cbuffer Params : register(b0) {
    float2 widgetOrigin;
    float2 widgetSize;
    float2 captureScale;
    float radius;
    float rim;
    float2 captureMinUv;
    float2 captureMaxUv;
    float dpiScale;
    float3 paramsPadding;
};
float sdRoundBox(float2 p, float2 halfSize, float r) {
    r = clamp(r, 0.0, min(halfSize.x, halfSize.y));
    float2 q = abs(p) - halfSize + r;
    return length(max(q, 0.0)) + min(max(q.x, q.y), 0.0) - r;
}
float3 sampleRgb(float2 uv, float2 offsetPx, float2 captureSize) {
    float2 stepX = float2(offsetPx.x / captureSize.x, 0.0);
    float2 stepY = float2(0.0, offsetPx.y / captureSize.y);
    float2 offsets[5] = { float2(0,0), stepX, -stepX, stepY, -stepY };
    float3 color = 0.0;
    float validSampleCount = 0.0;
    [unroll]
    for (int i = 0; i < 5; ++i) {
        float2 sampleUv = clamp(uv + offsets[i], captureMinUv, captureMaxUv);
        float4 sample = captureTex.SampleLevel(samp, sampleUv, 0);
        float coverage = saturate(sample.a);
        if (coverage > 0.0001) {
            color += sample.rgb / coverage;
            validSampleCount += 1.0;
        }
    }
    return validSampleCount > 0.0 ? color / validSampleCount : 0.0;
}
float3 sampleRawRgb(float2 uv) {
    float4 sample = captureTex.SampleLevel(samp, clamp(uv, captureMinUv, captureMaxUv), 0);
    float coverage = saturate(sample.a);
    return coverage > 0.0001 ? sample.rgb / coverage : 0.0;
}
float sampleCoverage(float2 uv) {
    return saturate(captureTex.SampleLevel(samp, clamp(uv, captureMinUv, captureMaxUv), 0).a);
}
float2 sourceUv(float2 localPos, float2 captureSize) {
    return clamp((widgetOrigin + localPos * captureScale) / captureSize, captureMinUv, captureMaxUv);
}
float3 adjustBackground(float2 uv, float2 captureSize) {
    float complexity = metricsTex.SampleLevel(samp, float2(0.5, 0.5), 0).a;
    float blurRadius = smoothstep(0.04, 0.55, complexity) * 2.5;
    float2 blur = blurRadius * captureScale;
    float3 color = sampleRgb(uv, float2(blur.x, blur.y), captureSize);
    float luma = dot(color, float3(0.2126, 0.7152, 0.0722));
    color = (color - luma) * (1.20 + complexity * 0.20) + luma;
    return saturate(color * (1.02 + complexity * 0.06));
}
float2 getNormal(float2 local, float2 halfSize, float cornerRadius) {
    float2 q = abs(local) - (halfSize - cornerRadius);
    float2 outsideQ = max(q, 0.0);
    float qlen = length(outsideQ);
    if (qlen > 0.0001) return sign(local) * outsideQ / qlen;
    if (q.x > q.y) return float2(sign(local.x), 0.0);
    return float2(0.0, sign(local.y));
}
float2 getDispersionDirection(float2 local, float2 halfSize, float cornerRadius, float edgeDist, float shortRadius) {
    float2 normal = getNormal(local, halfSize, cornerRadius);
    float radialLength = length(local);
    float2 radial = radialLength > 0.0001 ? local / radialLength : float2(1.0, 0.0);
    float t = saturate(1.0 - edgeDist / max(shortRadius, 0.0001));
    return normalize(lerp(radial, normal, smoothstep(0.0, 0.2, t)));
}
float dispersionOffsetPx(float edgeDist, float shortRadius) {
    float t = saturate(1.0 - edgeDist / max(shortRadius, 0.0001));
    return lerp(0.4, 2.5, pow(t, 1.8)) * dpiScale;
}
float4 BasePS(float4 pos : SV_POSITION) : SV_TARGET {
    uint width; uint height; captureTex.GetDimensions(width, height);
    float2 size = float2((float)width, (float)height);
    float2 localPos = pos.xy;
    float2 local = localPos - widgetSize * 0.5;
    float2 halfSize = widgetSize * 0.5;
    float cornerRadius = clamp(radius, 0.0, min(halfSize.x, halfSize.y));
    float sd = sdRoundBox(local, halfSize, cornerRadius);
    if (sd > 0.0) return 0.0;
    float edgeDist = -sd;
    float shortRadius = min(halfSize.x, halfSize.y);
    float2 direction = getDispersionDirection(local, halfSize, cornerRadius, edgeDist, shortRadius);
    float2 uv = sourceUv(localPos, size);
    float2 dispersionUv = direction * dispersionOffsetPx(edgeDist, shortRadius) * captureScale / size;
    float3 redSample = adjustBackground(uv + dispersionUv, size);
    float3 greenSample = adjustBackground(uv, size);
    float3 blueSample = adjustBackground(uv - dispersionUv, size);
    float coverage = min(sampleCoverage(uv + dispersionUv),
        min(sampleCoverage(uv), sampleCoverage(uv - dispersionUv)));
    float3 color = float3(redSample.r, greenSample.g, blueSample.b);
    return float4(color * coverage, coverage);
}
float4 LensPS(float4 pos : SV_POSITION) : SV_TARGET {
    uint width; uint height; captureTex.GetDimensions(width, height);
    float2 size = float2((float)width, (float)height);
    float2 localPos = pos.xy;
    float2 local = localPos - widgetSize * 0.5;
    float2 halfSize = widgetSize * 0.5;
    float cornerRadius = clamp(radius, 0.0, min(halfSize.x, halfSize.y));
    float sd = sdRoundBox(local, halfSize, cornerRadius);
    float edgeDist = -sd;
    if (sd > 0.0 || edgeDist >= rim) return 0.0;
    float2 normal = getNormal(local, halfSize, cornerRadius);
    float t = saturate(edgeDist / max(rim, 0.0001));
    float offset = 0.35 * rim * pow(1.0 - t, 2.6);
    float2 uv = sourceUv(localPos, size);
    float2 refractedUv = uv - normal * offset * captureScale / size;
    float2 dispersionUv = normal * dispersionOffsetPx(edgeDist, min(halfSize.x, halfSize.y)) * captureScale / size;
    float3 redSample = sampleRawRgb(refractedUv + dispersionUv);
    float3 greenSample = sampleRawRgb(refractedUv);
    float3 blueSample = sampleRawRgb(refractedUv - dispersionUv);
    float3 clearColor = float3(redSample.r, greenSample.g, blueSample.b);
    // Match the processed base toward the inside of the rim, while retaining
    // the clearer refracted edge. Apply dispersion to each processed sample.
    float3 softRed = adjustBackground(refractedUv + dispersionUv, size);
    float3 softGreen = adjustBackground(refractedUv, size);
    float3 softBlue = adjustBackground(refractedUv - dispersionUv, size);
    float3 softColor = float3(softRed.r, softGreen.g, softBlue.b);
    float3 refracted = lerp(clearColor, softColor, smoothstep(0.0, 1.0, t));
    float coverage = min(sampleCoverage(refractedUv + dispersionUv),
        min(sampleCoverage(refractedUv), sampleCoverage(refractedUv - dispersionUv)));
    // The lens and base use different background processing. Fade the
    // premultiplied lens layer into the base before the rim ends so their
    // color difference cannot create a hard inset rectangle.
    float lensOpacity = 1.0 - smoothstep(0.0, 1.0, t);
    float alpha = coverage * lensOpacity;
    return float4(saturate(refracted) * alpha, alpha);
}
float4 TintPS(float4 pos : SV_POSITION) : SV_TARGET {
    float2 local = pos.xy - widgetSize * 0.5;
    float2 halfSize = widgetSize * 0.5;
    float cornerRadius = clamp(radius, 0.0, min(halfSize.x, halfSize.y));
    if (sdRoundBox(local, halfSize, cornerRadius) > 0.0) return 0.0;
    uint width; uint height; captureTex.GetDimensions(width, height);
    float2 captureSize = float2((float)width, (float)height);
    float2 uv = sourceUv(pos.xy, captureSize);
    float complexity = metricsTex.SampleLevel(samp, float2(0.5, 0.5), 0).a;
    float alpha = lerp(0.06, 0.22, complexity) * sampleCoverage(uv);
    return float4(alpha, alpha, alpha, alpha);
}
)HLSL";

// Every cbuffer is 16-byte aligned and mirrors the HLSL field order.
struct ShaderParams
{
    float widgetOriginX{}, widgetOriginY{};
    float widgetWidth{}, widgetHeight{};
    float captureScaleX{1.0f}, captureScaleY{1.0f};
    float radius{}, rim{};
    float captureMinU{}, captureMinV{}, captureMaxU{}, captureMaxV{};
    float dpiScale{1.0f};
    float paddingX{}, paddingY{}, paddingZ{};
};
static_assert(sizeof(ShaderParams) == 64, "ShaderParams must be 64 bytes");

struct CropParams
{
    float screenOriginX{}, screenOriginY{};
    float monitorOriginX{}, monitorOriginY{};
    float monitorWidth{}, monitorHeight{};
    float contentWidth{}, contentHeight{};
    float sourceTextureWidth{}, sourceTextureHeight{};
    float paddingX{}, paddingY{};
};
static_assert(sizeof(CropParams) == 48, "CropParams must be 48 bytes");

struct MetricsParams
{
    float widgetOriginX{}, widgetOriginY{};
    float widgetWidth{}, widgetHeight{};
    float captureWidth{}, captureHeight{};
    float smoothing{}, initialized{};
};
static_assert(sizeof(MetricsParams) == 32, "MetricsParams must be 32 bytes");

} // namespace

struct LiquidGlassCapture::Impl
{
    HWND widget{};
    HWND content{},shadow{};
    bool enabled{};
    bool fuseTripped{};
    int fuseCount{};
    bool inkIsDark{};
    bool metricsInitialized{};
    UINT metricsIndex{};
    bool sessionActive{};
    bool fullscreenHidden{};
    HMONITOR captureMonitor{};
    double lastFrameMs{};
    bool hasPresentedFrame{};
    CaptureRect lastRect{};

    ComPtr<ID3D11Device> device;
    ComPtr<ID3D11DeviceContext> context;
    ComPtr<ID3D11Texture2D> captureTexture;
    ComPtr<IDXGISwapChain1> swapChain;
    ComPtr<IDXGISwapChain1> refractionSwapChain;
    ComPtr<IDXGISwapChain1> tintSwapChain;
    ComPtr<IDXGISwapChain1> inkColorSwapChain;
    ComPtr<ID3D11RenderTargetView> captureRtv;
    ComPtr<ID3D11RenderTargetView> outputRtv;
    ComPtr<ID3D11RenderTargetView> refractionRtv;
    ComPtr<ID3D11RenderTargetView> tintRtv;
    ComPtr<ID3D11RenderTargetView> inkColorRtv;
    ComPtr<ID3D11ShaderResourceView> captureSrv;
    ID3D11Texture2D* captureSrvTexture{nullptr};
    ComPtr<ID3D11ShaderResourceView> frameSourceSrv;
    ID3D11Texture2D* frameSourceSrvTexture{nullptr};
    std::array<ComPtr<ID3D11Texture2D>, 2> metricsTextures{};
    std::array<ComPtr<ID3D11RenderTargetView>, 2> metricsRtvs{};
    std::array<ComPtr<ID3D11ShaderResourceView>, 2> metricsSrvs{};
    ComPtr<ID3D11VertexShader> vertexShader;
    ComPtr<ID3D11PixelShader> capturePixelShader;
    ComPtr<ID3D11PixelShader> metricsPixelShader;
    ComPtr<ID3D11PixelShader> pixelShader;
    ComPtr<ID3D11PixelShader> refractionPixelShader;
    ComPtr<ID3D11PixelShader> tintPixelShader;
    ComPtr<ID3D11SamplerState> sampler;
    ComPtr<ID3D11RasterizerState> rasterizerState;
    ComPtr<ID3D11Buffer> paramsBuffer;
    ComPtr<ID3D11Buffer> cropParamsBuffer;
    ComPtr<ID3D11Buffer> metricsParamsBuffer;
    IDirect3DDevice winrtDevice{nullptr};
    GraphicsCaptureItem captureItem{nullptr};
    Direct3D11CaptureFramePool framePool{nullptr};
    GraphicsCaptureSession session{nullptr};
    std::shared_ptr<BorderlessAccessResult> borderlessAccess{std::make_shared<BorderlessAccessResult>()};
    winrt::Windows::Foundation::IAsyncOperation<AppCapabilityAccessStatus> borderlessAccessRequest{nullptr};

    HRESULT EnsureDevice() noexcept
    {
        try
        {
            HRESULT hr = S_OK;
            if (!device)
            {
                UINT flags = D3D11_CREATE_DEVICE_BGRA_SUPPORT;
                D3D_FEATURE_LEVEL level{};
                hr = D3D11CreateDevice(nullptr, D3D_DRIVER_TYPE_HARDWARE, nullptr, flags, nullptr, 0,
                                       D3D11_SDK_VERSION, device.ReleaseAndGetAddressOf(), &level,
                                       context.ReleaseAndGetAddressOf());
                if (FAILED(hr))
                {
                    hr = D3D11CreateDevice(nullptr, D3D_DRIVER_TYPE_WARP, nullptr, flags, nullptr, 0,
                                           D3D11_SDK_VERSION, device.ReleaseAndGetAddressOf(), &level,
                                           context.ReleaseAndGetAddressOf());
                    if (FAILED(hr)) return hr;
                }
            }
            if (!context) return E_POINTER;
            if (vertexShader && capturePixelShader && metricsPixelShader && pixelShader &&
                refractionPixelShader && tintPixelShader && sampler && rasterizerState && paramsBuffer &&
                cropParamsBuffer && metricsParamsBuffer && winrtDevice) return S_OK;
            ComPtr<IDXGIDevice> dxgiDevice;
            hr = device.As(&dxgiDevice);
            if (FAILED(hr)) return hr;
            // Correct interop helper for a DXGI *device* (not a surface):
            // CreateDirect3D11DeviceFromDXGIDevice. The Surface variant takes
            // an IDXGISurface* and would not accept an IDXGIDevice*.
            hr = CreateDirect3D11DeviceFromDXGIDevice(dxgiDevice.Get(), reinterpret_cast<IInspectable**>(winrt::put_abi(winrtDevice)));
            if (FAILED(hr)) return hr;
            D3D11_SAMPLER_DESC samplerDesc{};
            samplerDesc.Filter = D3D11_FILTER_MIN_MAG_MIP_LINEAR;
            samplerDesc.AddressU = D3D11_TEXTURE_ADDRESS_CLAMP;
            samplerDesc.AddressV = D3D11_TEXTURE_ADDRESS_CLAMP;
            samplerDesc.AddressW = D3D11_TEXTURE_ADDRESS_CLAMP;
            samplerDesc.ComparisonFunc = D3D11_COMPARISON_NEVER;
            samplerDesc.MinLOD = 0.0f;
            samplerDesc.MaxLOD = D3D11_FLOAT32_MAX;
            hr = device->CreateSamplerState(&samplerDesc, sampler.ReleaseAndGetAddressOf());
            if (FAILED(hr)) return hr;
            D3D11_RASTERIZER_DESC rasterizerDesc{};
            rasterizerDesc.FillMode = D3D11_FILL_SOLID;
            rasterizerDesc.CullMode = D3D11_CULL_NONE;
            rasterizerDesc.DepthClipEnable = TRUE;
            rasterizerDesc.ScissorEnable = FALSE;
            rasterizerDesc.MultisampleEnable = FALSE;
            hr = device->CreateRasterizerState(&rasterizerDesc, rasterizerState.ReleaseAndGetAddressOf());
            if (FAILED(hr)) return hr;
            auto createConstantBuffer = [&](UINT byteWidth, ComPtr<ID3D11Buffer>& buffer) -> HRESULT
            {
                D3D11_BUFFER_DESC bufferDesc{};
                bufferDesc.ByteWidth = byteWidth;
                bufferDesc.Usage = D3D11_USAGE_DYNAMIC;
                bufferDesc.BindFlags = D3D11_BIND_CONSTANT_BUFFER;
                bufferDesc.CPUAccessFlags = D3D11_CPU_ACCESS_WRITE;
                return device->CreateBuffer(&bufferDesc, nullptr, buffer.ReleaseAndGetAddressOf());
            };
            hr = createConstantBuffer(sizeof(ShaderParams), paramsBuffer);
            if (FAILED(hr)) return hr;
            hr = createConstantBuffer(sizeof(CropParams), cropParamsBuffer);
            if (FAILED(hr)) return hr;
            hr = createConstantBuffer(sizeof(MetricsParams), metricsParamsBuffer);
            if (FAILED(hr)) return hr;
            ComPtr<ID3DBlob> vsBlob, psBlob, errorBlob;
            hr = D3DCompile(kVertexShader, std::strlen(kVertexShader), "LiquidGlassVS", nullptr, nullptr, "VS",
                            "vs_5_0", D3DCOMPILE_OPTIMIZATION_LEVEL3, 0, vsBlob.ReleaseAndGetAddressOf(),
                            errorBlob.ReleaseAndGetAddressOf());
            if (FAILED(hr)) return hr;
            hr = device->CreateVertexShader(vsBlob->GetBufferPointer(), vsBlob->GetBufferSize(), nullptr,
                                            vertexShader.ReleaseAndGetAddressOf());
            if (FAILED(hr)) return hr;
            auto createPixelShader = [&](char const* source, char const* sourceName, char const* entry,
                                         ComPtr<ID3D11PixelShader>& shader) -> HRESULT
            {
                ComPtr<ID3DBlob> bytecode;
                ComPtr<ID3DBlob> errors;
                HRESULT compileResult = D3DCompile(source, std::strlen(source), sourceName, nullptr, nullptr, entry,
                    "ps_5_0", D3DCOMPILE_OPTIMIZATION_LEVEL3, 0, bytecode.ReleaseAndGetAddressOf(),
                    errors.ReleaseAndGetAddressOf());
                if (FAILED(compileResult)) return compileResult;
                return device->CreatePixelShader(bytecode->GetBufferPointer(), bytecode->GetBufferSize(), nullptr,
                    shader.ReleaseAndGetAddressOf());
            };
            hr = createPixelShader(kCaptureShader, "LiquidGlassCropPS", "CropPS", capturePixelShader);
            if (FAILED(hr)) return hr;
            hr = createPixelShader(kMetricsShader, "LiquidGlassMetricsPS", "MetricsPS", metricsPixelShader);
            if (FAILED(hr)) return hr;
            hr = createPixelShader(kPixelShader, "LiquidGlassBasePS", "BasePS", pixelShader);
            if (FAILED(hr)) return hr;
            hr = createPixelShader(kPixelShader, "LiquidGlassRefractionPS", "LensPS", refractionPixelShader);
            if (FAILED(hr)) return hr;
            return createPixelShader(kPixelShader, "LiquidGlassTintPS", "TintPS", tintPixelShader);
        }
        catch (...)
        {
            return winrt::to_hresult();
        }
    }

    void StopSession() noexcept
    {
        try
        {
            if (session) session.Close();
        }
        catch (...)
        {
        }
        session = nullptr;
        framePool = nullptr;
        captureItem = nullptr;
        sessionActive = false;
        hasPresentedFrame = false;
        captureMonitor = nullptr;
    }

    HRESULT EnsureBorderlessAccess() noexcept
    {
        const auto current = borderlessAccess->state.load(std::memory_order_acquire);
        switch (current)
        {
        case BorderlessAccessState::Allowed:
            return S_OK;
        case BorderlessAccessState::Denied:
            return E_ACCESSDENIED;
        case BorderlessAccessState::Failed:
            return borderlessAccess->error.load(std::memory_order_acquire);
        case BorderlessAccessState::Pending:
            return E_PENDING;
        case BorderlessAccessState::NotRequested:
            break;
        }

        auto expected = BorderlessAccessState::NotRequested;
        if (!borderlessAccess->state.compare_exchange_strong(expected, BorderlessAccessState::Pending,
                                                             std::memory_order_acq_rel))
            return E_PENDING;
        try
        {
            const auto result = borderlessAccess;
            borderlessAccessRequest = GraphicsCaptureAccess::RequestAccessAsync(GraphicsCaptureAccessKind::Borderless);
            borderlessAccessRequest.Completed([result](auto const& operation, winrt::Windows::Foundation::AsyncStatus status) noexcept
            {
                try
                {
                    if (status == winrt::Windows::Foundation::AsyncStatus::Completed)
                    {
                        const auto access = operation.GetResults();
                        result->state.store(access == AppCapabilityAccessStatus::Allowed
                            ? BorderlessAccessState::Allowed : BorderlessAccessState::Denied, std::memory_order_release);
                        return;
                    }

                    HRESULT failure = HRESULT_FROM_WIN32(ERROR_CANCELLED);
                    if (status == winrt::Windows::Foundation::AsyncStatus::Error)
                        failure = static_cast<HRESULT>(operation.ErrorCode());
                    result->error.store(FAILED(failure) ? failure : E_FAIL, std::memory_order_release);
                    result->state.store(BorderlessAccessState::Failed, std::memory_order_release);
                }
                catch (...)
                {
                    result->error.store(winrt::to_hresult(), std::memory_order_release);
                    result->state.store(BorderlessAccessState::Failed, std::memory_order_release);
                }
            });
            return E_PENDING;
        }
        catch (...)
        {
            const HRESULT failure = winrt::to_hresult();
            borderlessAccess->error.store(FAILED(failure) ? failure : E_FAIL, std::memory_order_release);
            borderlessAccess->state.store(BorderlessAccessState::Failed, std::memory_order_release);
            return FAILED(failure) ? failure : E_FAIL;
        }
    }

    HRESULT ConfigureWindowExclusions() noexcept
    {
        try
        {
            auto displaySession = session.try_as<IDisplayGraphicsCaptureSession>();
            if (!displaySession) return HRESULT_FROM_WIN32(ERROR_NOT_SUPPORTED);
            const HMODULE windowingModule = LoadLibraryW(L"Windows.UI.dll");
            if (windowingModule == nullptr) return HRESULT_FROM_WIN32(GetLastError());
            struct ModuleReleaser
            {
                HMODULE module;
                ~ModuleReleaser() { FreeLibrary(module); }
            } releaseModule{windowingModule};
            using GetWindowIdFunction = HRESULT(WINAPI*)(HWND, ABI::Windows::UI::WindowId*);
            const auto getWindowId = reinterpret_cast<GetWindowIdFunction>(
                GetProcAddress(windowingModule, "GetWindowIdFromWindow"));
            if (getWindowId == nullptr) return HRESULT_FROM_WIN32(ERROR_PROC_NOT_FOUND);

            auto excluded = winrt::single_threaded_vector<winrt::Windows::UI::WindowId>();
            // Exclude only this widget's capture/content/shadow HWNDs. Other
            // GlassDesk windows, especially SettingsWindow, are real desktop
            // background content and must remain visible in monitor capture.
            for (HWND candidate : {widget, content, shadow})
            {
                if (candidate == nullptr || !IsWindow(candidate)) continue;
                ABI::Windows::UI::WindowId abiId{};
                const HRESULT hr = getWindowId(candidate, &abiId);
                if (FAILED(hr)) return hr;
                const winrt::Windows::UI::WindowId id{abiId.Value};
                if (id.Value == 0) return E_HANDLE;
                bool alreadyPresent = false;
                for (auto const& existing : excluded)
                {
                    if (existing.Value == id.Value)
                    {
                        alreadyPresent = true;
                        break;
                    }
                }
                if (!alreadyPresent) excluded.Append(id);
            }
            if (excluded.Size() == 0) return E_INVALIDARG;
            (void)displaySession.SetWindowExclusionList(excluded);
            return S_OK;
        }
        catch (...)
        {
            return winrt::to_hresult();
        }
    }

    HRESULT StartSession(CaptureRect const& rect) noexcept
    {
        try
        {
            HRESULT hr = EnsureDevice();
            if (FAILED(hr)) return hr;
            hr = EnsureBorderlessAccess();
            if (hr != S_OK) return hr;
            const HMONITOR monitor = MonitorFromWindow(widget, MONITOR_DEFAULTTONEAREST);
            MONITORINFO info{};
            info.cbSize = sizeof(info);
            if (!GetMonitorInfoW(monitor, &info)) return HRESULT_FROM_WIN32(GetLastError());
            // get_activation_factory<T, IGraphicsCaptureItemInterop>() returns
            // the interop interface directly; CreateForMonitor is its method.
            auto interop = winrt::get_activation_factory<GraphicsCaptureItem, IGraphicsCaptureItemInterop>();
            winrt::Windows::Graphics::Capture::GraphicsCaptureItem item{nullptr};
            hr = interop->CreateForMonitor(monitor, winrt::guid_of<GraphicsCaptureItem>(),
                                           reinterpret_cast<void**>(winrt::put_abi(item)));
            if (FAILED(hr)) return hr;
            captureItem = item;
            const auto captureSize = captureItem.Size();
            const winrt::Windows::Graphics::SizeInt32 size{
                std::max(1, captureSize.Width), std::max(1, captureSize.Height)};
            framePool = Direct3D11CaptureFramePool::Create(winrtDevice, DirectXPixelFormat::B8G8R8A8UIntNormalized, 2, size);
            session = framePool.CreateCaptureSession(captureItem);
            hr = ConfigureWindowExclusions();
            if (FAILED(hr)) return hr;
            session.IsBorderRequired(false);
            session.IsCursorCaptureEnabled(false);
            session.StartCapture();
            sessionActive = true;
            captureMonitor = monitor;
            (void)rect;
            return S_OK;
        }
        catch (...)
        {
            return winrt::to_hresult();
        }
    }

    HRESULT EnsureCompositionSwapChain(ComPtr<IDXGISwapChain1>& chain,
                                       ComPtr<ID3D11RenderTargetView>& renderTarget,
                                       UINT width, UINT height) noexcept
    {
        try
        {
            if (width == 0 || height == 0) return E_INVALIDARG;
            HRESULT hr = S_OK;
            if (!chain)
            {
                ComPtr<IDXGIDevice> dxgiDevice;
                hr = device.As(&dxgiDevice);
                if (FAILED(hr)) return hr;
                ComPtr<IDXGIAdapter> adapter;
                hr = dxgiDevice->GetAdapter(adapter.ReleaseAndGetAddressOf());
                if (FAILED(hr)) return hr;
                ComPtr<IDXGIFactory2> factory;
                hr = adapter->GetParent(IID_PPV_ARGS(factory.ReleaseAndGetAddressOf()));
                if (FAILED(hr)) return hr;
                DXGI_SWAP_CHAIN_DESC1 desc{};
                desc.Width = width;
                desc.Height = height;
                desc.Format = DXGI_FORMAT_B8G8R8A8_UNORM;
                desc.Stereo = FALSE;
                desc.SampleDesc.Count = 1;
                desc.BufferUsage = DXGI_USAGE_RENDER_TARGET_OUTPUT;
                desc.BufferCount = 2;
                desc.Scaling = DXGI_SCALING_STRETCH;
                desc.SwapEffect = DXGI_SWAP_EFFECT_FLIP_SEQUENTIAL;
                desc.AlphaMode = DXGI_ALPHA_MODE_PREMULTIPLIED;
                hr = factory->CreateSwapChainForComposition(device.Get(), &desc, nullptr,
                                                              chain.ReleaseAndGetAddressOf());
                if (FAILED(hr)) return hr;
            }
            else
            {
                DXGI_SWAP_CHAIN_DESC1 existing{};
                hr = chain->GetDesc1(&existing);
                if (FAILED(hr)) return hr;
                if (existing.Width != width || existing.Height != height)
                {
                    renderTarget.Reset();
                    hr = chain->ResizeBuffers(0, width, height, DXGI_FORMAT_UNKNOWN, 0);
                    if (FAILED(hr)) return hr;
                    hasPresentedFrame = false;
                }
            }
            if (!renderTarget)
            {
                ComPtr<ID3D11Texture2D> backBuffer;
                hr = chain->GetBuffer(0, IID_PPV_ARGS(backBuffer.ReleaseAndGetAddressOf()));
                if (FAILED(hr)) return hr;
                hr = device->CreateRenderTargetView(backBuffer.Get(), nullptr, renderTarget.ReleaseAndGetAddressOf());
                if (FAILED(hr)) return hr;
            }
            return S_OK;
        }
        catch (...)
        {
            return winrt::to_hresult();
        }
    }

    HRESULT EnsureMetricsTargets() noexcept
    {
        try
        {
            if (metricsTextures[0] && metricsTextures[1] && metricsRtvs[0] && metricsRtvs[1] &&
                metricsSrvs[0] && metricsSrvs[1]) return S_OK;
            D3D11_TEXTURE2D_DESC desc{};
            desc.Width = 1;
            desc.Height = 1;
            desc.MipLevels = 1;
            desc.ArraySize = 1;
            desc.Format = DXGI_FORMAT_R32G32B32A32_FLOAT;
            desc.SampleDesc.Count = 1;
            desc.Usage = D3D11_USAGE_DEFAULT;
            desc.BindFlags = D3D11_BIND_RENDER_TARGET | D3D11_BIND_SHADER_RESOURCE;
            const float clear[4]{};
            for (UINT index = 0; index < 2; ++index)
            {
                HRESULT hr = device->CreateTexture2D(&desc, nullptr, metricsTextures[index].ReleaseAndGetAddressOf());
                if (FAILED(hr)) return hr;
                hr = device->CreateRenderTargetView(metricsTextures[index].Get(), nullptr,
                    metricsRtvs[index].ReleaseAndGetAddressOf());
                if (FAILED(hr)) return hr;
                hr = device->CreateShaderResourceView(metricsTextures[index].Get(), nullptr,
                    metricsSrvs[index].ReleaseAndGetAddressOf());
                if (FAILED(hr)) return hr;
                context->ClearRenderTargetView(metricsRtvs[index].Get(), clear);
            }
            metricsIndex = 0;
            metricsInitialized = false;
            return S_OK;
        }
        catch (...)
        {
            return winrt::to_hresult();
        }
    }

    HRESULT RenderFrame(CaptureRect const& rect, float rimPx) noexcept
    {
        try
        {
            if (!framePool) return S_FALSE;
            auto frame = framePool.TryGetNextFrame();
            if (!frame) return S_FALSE;
            auto surface = frame.Surface();
            if (!surface) return S_FALSE;
            auto access = surface.as<::Windows::Graphics::DirectX::Direct3D11::IDirect3DDxgiInterfaceAccess>();
            ComPtr<IDXGISurface> dxgiSurface;
            HRESULT hr = access->GetInterface(IID_PPV_ARGS(&dxgiSurface));
            if (FAILED(hr)) return hr;
            ComPtr<ID3D11Texture2D> sourceTexture;
            hr = dxgiSurface.As(&sourceTexture);
            if (FAILED(hr)) return hr;
            D3D11_TEXTURE2D_DESC sourceDesc{};
            sourceTexture->GetDesc(&sourceDesc);

            const auto contentSize = frame.ContentSize();
            if (contentSize.Width <= 0 || contentSize.Height <= 0) return S_FALSE;
            if (static_cast<UINT>(contentSize.Width) > sourceDesc.Width ||
                static_cast<UINT>(contentSize.Height) > sourceDesc.Height)
            {
                sourceTexture.Reset();
                dxgiSurface.Reset();
                access = nullptr;
                surface = nullptr;
                frame.Close();
                frameSourceSrv.Reset();
                frameSourceSrvTexture = nullptr;
                framePool.Recreate(winrtDevice, DirectXPixelFormat::B8G8R8A8UIntNormalized, 2, contentSize);
                return S_FALSE;
            }

            // The WGC frame's ContentSize can differ from the backing texture
            // dimensions. Resample the current physical screen crop into a
            // card-aligned GPU texture so each later effect uses the same
            // physical-pixel coordinate system as the HWND and swap chains.
            const float dpiScale = rect.dpiScale > 0.0f ? rect.dpiScale : 1.0f;
            const LONG pad = static_cast<LONG>(std::ceil(
                static_cast<double>(rimPx) * kMaxRefractionScale +
                static_cast<double>(kMaxDispersionDip * dpiScale) +
                static_cast<double>(kMaxBlurRadiusPx)));
            const LONG captureWidth = static_cast<LONG>(rect.widthPixels) + pad * 2;
            const LONG captureHeight = static_cast<LONG>(rect.heightPixels) + pad * 2;
            if (captureWidth <= 0 || captureHeight <= 0) return E_INVALIDARG;
            if (!captureTexture)
            {
                D3D11_TEXTURE2D_DESC desc{};
                desc.Width = static_cast<UINT>(captureWidth);
                desc.Height = static_cast<UINT>(captureHeight);
                desc.MipLevels = 1;
                desc.ArraySize = 1;
                desc.Format = DXGI_FORMAT_B8G8R8A8_UNORM;
                desc.SampleDesc.Count = 1;
                desc.Usage = D3D11_USAGE_DEFAULT;
                desc.BindFlags = D3D11_BIND_RENDER_TARGET | D3D11_BIND_SHADER_RESOURCE;
                hr = device->CreateTexture2D(&desc, nullptr, captureTexture.ReleaseAndGetAddressOf());
                if (FAILED(hr)) return hr;
                captureSrvTexture = nullptr;
            }
            else
            {
                D3D11_TEXTURE2D_DESC existing{};
                captureTexture->GetDesc(&existing);
                if (static_cast<LONG>(existing.Width) != captureWidth || static_cast<LONG>(existing.Height) != captureHeight)
                {
                    captureRtv.Reset();
                    captureSrv.Reset();
                    captureTexture.Reset();
                    captureSrvTexture = nullptr;
                    D3D11_TEXTURE2D_DESC desc{};
                    desc.Width = static_cast<UINT>(captureWidth);
                    desc.Height = static_cast<UINT>(captureHeight);
                    desc.MipLevels = 1;
                    desc.ArraySize = 1;
                    desc.Format = DXGI_FORMAT_B8G8R8A8_UNORM;
                    desc.SampleDesc.Count = 1;
                    desc.Usage = D3D11_USAGE_DEFAULT;
                    desc.BindFlags = D3D11_BIND_RENDER_TARGET | D3D11_BIND_SHADER_RESOURCE;
                    hr = device->CreateTexture2D(&desc, nullptr, captureTexture.ReleaseAndGetAddressOf());
                    if (FAILED(hr)) return hr;
                }
            }
            if (!captureRtv)
            {
                hr = device->CreateRenderTargetView(captureTexture.Get(), nullptr, captureRtv.ReleaseAndGetAddressOf());
                if (FAILED(hr)) return hr;
            }
            const HMONITOR monitor = captureMonitor != nullptr ? captureMonitor : MonitorFromWindow(widget, MONITOR_DEFAULTTONEAREST);
            MONITORINFO info{};
            info.cbSize = sizeof(info);
            if (!GetMonitorInfoW(monitor, &info)) return HRESULT_FROM_WIN32(GetLastError());
            const LONG monitorWidth = info.rcMonitor.right - info.rcMonitor.left;
            const LONG monitorHeight = info.rcMonitor.bottom - info.rcMonitor.top;
            if (monitorWidth <= 0 || monitorHeight <= 0) return E_INVALIDARG;
            if (!frameSourceSrv || frameSourceSrvTexture != sourceTexture.Get())
            {
                frameSourceSrv.Reset();
                hr = device->CreateShaderResourceView(sourceTexture.Get(), nullptr, frameSourceSrv.ReleaseAndGetAddressOf());
                if (FAILED(hr)) return hr;
                frameSourceSrvTexture = sourceTexture.Get();
            }
            CropParams crop{};
            crop.screenOriginX = static_cast<float>(rect.screenX - pad);
            crop.screenOriginY = static_cast<float>(rect.screenY - pad);
            crop.monitorOriginX = static_cast<float>(info.rcMonitor.left);
            crop.monitorOriginY = static_cast<float>(info.rcMonitor.top);
            crop.monitorWidth = static_cast<float>(monitorWidth);
            crop.monitorHeight = static_cast<float>(monitorHeight);
            crop.contentWidth = static_cast<float>(contentSize.Width);
            crop.contentHeight = static_cast<float>(contentSize.Height);
            crop.sourceTextureWidth = static_cast<float>(sourceDesc.Width);
            crop.sourceTextureHeight = static_cast<float>(sourceDesc.Height);
            D3D11_MAPPED_SUBRESOURCE mapped{};
            hr = context->Map(cropParamsBuffer.Get(), 0, D3D11_MAP_WRITE_DISCARD, 0, &mapped);
            if (FAILED(hr)) return hr;
            std::memcpy(mapped.pData, &crop, sizeof(crop));
            context->Unmap(cropParamsBuffer.Get(), 0);

            const float clearColor[4]{};
            context->ClearRenderTargetView(captureRtv.Get(), clearColor);
            ID3D11RenderTargetView* captureTarget = captureRtv.Get();
            context->OMSetRenderTargets(1, &captureTarget, nullptr);
            D3D11_VIEWPORT cropViewport{};
            cropViewport.Width = static_cast<float>(captureWidth);
            cropViewport.Height = static_cast<float>(captureHeight);
            cropViewport.MaxDepth = 1.0f;
            context->RSSetViewports(1, &cropViewport);
            context->IASetInputLayout(nullptr);
            context->IASetPrimitiveTopology(D3D11_PRIMITIVE_TOPOLOGY_TRIANGLELIST);
            context->VSSetShader(vertexShader.Get(), nullptr, 0);
            context->GSSetShader(nullptr, nullptr, 0);
            context->HSSetShader(nullptr, nullptr, 0);
            context->DSSetShader(nullptr, nullptr, 0);
            context->CSSetShader(nullptr, nullptr, 0);
            context->PSSetShader(capturePixelShader.Get(), nullptr, 0);
            ID3D11ShaderResourceView* displaySrv = frameSourceSrv.Get();
            context->PSSetShaderResources(0, 1, &displaySrv);
            ID3D11SamplerState* samplerState = sampler.Get();
            context->PSSetSamplers(0, 1, &samplerState);
            ID3D11Buffer* cropBuffer = cropParamsBuffer.Get();
            context->PSSetConstantBuffers(0, 1, &cropBuffer);
            context->RSSetState(rasterizerState.Get());
            context->OMSetBlendState(nullptr, nullptr, 0xffffffff);
            context->OMSetDepthStencilState(nullptr, 0);
            context->SetPredication(nullptr, FALSE);
            context->Draw(3, 0);
            ID3D11ShaderResourceView* nullSrv = nullptr;
            context->PSSetShaderResources(0, 1, &nullSrv);
            context->OMSetRenderTargets(0, nullptr, nullptr);

            if (!captureSrv || captureSrvTexture != captureTexture.Get())
            {
                captureSrv.Reset();
                hr = device->CreateShaderResourceView(captureTexture.Get(), nullptr, captureSrv.ReleaseAndGetAddressOf());
                if (FAILED(hr)) return hr;
                captureSrvTexture = captureTexture.Get();
            }
            hr = EnsureCompositionSwapChain(swapChain, outputRtv,
                static_cast<UINT>(rect.widthPixels), static_cast<UINT>(rect.heightPixels));
            if (FAILED(hr)) return hr;
            hr = EnsureCompositionSwapChain(refractionSwapChain, refractionRtv,
                static_cast<UINT>(rect.widthPixels), static_cast<UINT>(rect.heightPixels));
            if (FAILED(hr)) return hr;
            hr = EnsureCompositionSwapChain(tintSwapChain, tintRtv,
                static_cast<UINT>(rect.widthPixels), static_cast<UINT>(rect.heightPixels));
            if (FAILED(hr)) return hr;
            hr = EnsureCompositionSwapChain(inkColorSwapChain, inkColorRtv, 1, 1);
            if (FAILED(hr)) return hr;
            hr = EnsureMetricsTargets();
            if (FAILED(hr)) return hr;

            ShaderParams params{};
            params.widgetOriginX = static_cast<float>(pad);
            params.widgetOriginY = static_cast<float>(pad);
            params.widgetWidth = static_cast<float>(rect.widthPixels);
            params.widgetHeight = static_cast<float>(rect.heightPixels);
            params.captureScaleX = 1.0f;
            params.captureScaleY = 1.0f;
            params.radius = rect.radiusPixels;
            params.rim = rimPx;
            params.dpiScale = dpiScale;
            const float textureWidth = static_cast<float>(captureWidth);
            const float textureHeight = static_cast<float>(captureHeight);
            params.captureMinU = 0.5f / textureWidth;
            params.captureMinV = 0.5f / textureHeight;
            params.captureMaxU = 1.0f - params.captureMinU;
            params.captureMaxV = 1.0f - params.captureMinV;
            hr = context->Map(paramsBuffer.Get(), 0, D3D11_MAP_WRITE_DISCARD, 0, &mapped);
            if (FAILED(hr)) return hr;
            std::memcpy(mapped.pData, &params, sizeof(params));
            context->Unmap(paramsBuffer.Get(), 0);

            const UINT outputMetricIndex = metricsInitialized ? 1U - metricsIndex : 0U;
            const UINT previousMetricIndex = 1U - outputMetricIndex;
            MetricsParams metrics{};
            metrics.widgetOriginX = params.widgetOriginX;
            metrics.widgetOriginY = params.widgetOriginY;
            metrics.widgetWidth = params.widgetWidth;
            metrics.widgetHeight = params.widgetHeight;
            metrics.captureWidth = textureWidth;
            metrics.captureHeight = textureHeight;
            metrics.smoothing = 0.22f;
            metrics.initialized = metricsInitialized ? 1.0f : 0.0f;
            hr = context->Map(metricsParamsBuffer.Get(), 0, D3D11_MAP_WRITE_DISCARD, 0, &mapped);
            if (FAILED(hr)) return hr;
            std::memcpy(mapped.pData, &metrics, sizeof(metrics));
            context->Unmap(metricsParamsBuffer.Get(), 0);

            ID3D11RenderTargetView* metricTargets[2]{metricsRtvs[outputMetricIndex].Get(), inkColorRtv.Get()};
            context->OMSetRenderTargets(2, metricTargets, nullptr);
            D3D11_VIEWPORT metricViewport{};
            metricViewport.Width = 1.0f;
            metricViewport.Height = 1.0f;
            metricViewport.MaxDepth = 1.0f;
            context->RSSetViewports(1, &metricViewport);
            context->VSSetShader(vertexShader.Get(), nullptr, 0);
            context->PSSetShader(metricsPixelShader.Get(), nullptr, 0);
            ID3D11ShaderResourceView* metricInputs[2]{captureSrv.Get(), metricsSrvs[previousMetricIndex].Get()};
            context->PSSetShaderResources(0, 2, metricInputs);
            context->PSSetSamplers(0, 1, &samplerState);
            ID3D11Buffer* metricsBuffer = metricsParamsBuffer.Get();
            context->PSSetConstantBuffers(0, 1, &metricsBuffer);
            context->RSSetState(rasterizerState.Get());
            context->OMSetBlendState(nullptr, nullptr, 0xffffffff);
            context->OMSetDepthStencilState(nullptr, 0);
            context->Draw(3, 0);
            ID3D11ShaderResourceView* nullSrvs[2]{};
            context->PSSetShaderResources(0, 2, nullSrvs);
            context->OMSetRenderTargets(0, nullptr, nullptr);
            hr = inkColorSwapChain->Present(1, 0);
            if (FAILED(hr)) return hr;
            metricsIndex = outputMetricIndex;
            metricsInitialized = true;

            auto drawLayer = [&](ComPtr<IDXGISwapChain1> const& chain,
                                 ComPtr<ID3D11RenderTargetView> const& renderTarget,
                                 ID3D11PixelShader* shader) -> HRESULT
            {
                context->ClearRenderTargetView(renderTarget.Get(), clearColor);
                ID3D11RenderTargetView* target = renderTarget.Get();
                context->OMSetRenderTargets(1, &target, nullptr);
                D3D11_VIEWPORT viewport{};
                viewport.Width = static_cast<float>(rect.widthPixels);
                viewport.Height = static_cast<float>(rect.heightPixels);
                viewport.MaxDepth = 1.0f;
                context->RSSetViewports(1, &viewport);
                context->VSSetShader(vertexShader.Get(), nullptr, 0);
                context->PSSetShader(shader, nullptr, 0);
                ID3D11ShaderResourceView* inputs[2]{captureSrv.Get(), metricsSrvs[metricsIndex].Get()};
                context->PSSetShaderResources(0, 2, inputs);
                ID3D11Buffer* constants = paramsBuffer.Get();
                context->PSSetConstantBuffers(0, 1, &constants);
                context->Draw(3, 0);
                context->PSSetShaderResources(0, 2, nullSrvs);
                context->OMSetRenderTargets(0, nullptr, nullptr);
                return chain->Present(1, 0);
            };
            hasPresentedFrame = false;
            hr = drawLayer(swapChain, outputRtv, pixelShader.Get());
            if (FAILED(hr)) return hr;
            hr = drawLayer(refractionSwapChain, refractionRtv, refractionPixelShader.Get());
            if (FAILED(hr)) return hr;
            hr = drawLayer(tintSwapChain, tintRtv, tintPixelShader.Get());
            if (FAILED(hr)) return hr;
            hasPresentedFrame = true;
            return S_OK;
        }
        catch (...)
        {
            return winrt::to_hresult();
        }
    }

    void TripIfDeviceError(HRESULT hr) noexcept
    {
        if (!IsDeviceError(hr)) return;
        ++fuseCount;
        if (fuseCount >= kFuseLimit)
        {
            fuseTripped = true;
            StopSession();
        }
    }
};

LiquidGlassCapture::LiquidGlassCapture(HWND widgetWindow, bool enabled, ID3D11Device* sharedDevice,
                                       ID3D11DeviceContext* sharedContext)
    : LiquidGlassCapture(widgetWindow, widgetWindow, nullptr, enabled, sharedDevice, sharedContext)
{
}

LiquidGlassCapture::LiquidGlassCapture(HWND widgetWindow, HWND contentWindow, HWND shadowWindow, bool enabled,
                                       ID3D11Device* sharedDevice, ID3D11DeviceContext* sharedContext)
{
    auto* impl = new Impl();
    impl->widget = widgetWindow;
    impl->content = contentWindow;
    impl->shadow = shadowWindow;
    impl->enabled = enabled;
    if (sharedDevice != nullptr) impl->device = sharedDevice;
    if (sharedContext != nullptr) impl->context = sharedContext;
    impl_ = impl;
    if (widgetWindow != nullptr)
    {
        // Exclude the widget itself from every capture so it never captures
        // its own pixels.
        if (!SetWindowDisplayAffinity(widgetWindow, WDA_EXCLUDEFROMCAPTURE))
        {
            DWORD error = GetLastError();
            if (error == ERROR_SUCCESS) error = ERROR_ACCESS_DENIED;
            const HRESULT hr = HRESULT_FROM_WIN32(error);
            delete impl_;
            impl_ = nullptr;
            throw winrt::hresult_error(hr);
        }
    }
}

LiquidGlassCapture::~LiquidGlassCapture()
{
    if (impl_ == nullptr) return;
    impl_->StopSession();
    if (impl_->widget != nullptr) SetWindowDisplayAffinity(impl_->widget, WDA_NONE);
    delete impl_;
    impl_ = nullptr;
}

HRESULT LiquidGlassCapture::Update(CaptureRect const& rect) noexcept
{
    if (impl_ == nullptr) return E_POINTER;
    auto* impl = impl_;
    if (!impl->enabled || impl->fuseTripped) return S_FALSE;
    if (rect.widthPixels <= 0 || rect.heightPixels <= 0) return E_INVALIDARG;
    if (IsIconic(impl->widget)) return S_FALSE;

    RECT widgetRect{};
    if (!GetPhysicalWindowBounds(impl->widget, widgetRect))
        return HRESULT_FROM_WIN32(GetLastError());
    const HMONITOR currentMonitor = MonitorFromWindow(impl->widget, MONITOR_DEFAULTTONEAREST);
    if (impl->sessionActive && impl->captureMonitor != currentMonitor) impl->StopSession();
    // Fullscreen: hide the widget and stop capturing; host restores it when
    // Update is called again after fullscreen ends (see below).
    if (IsFullscreenForeground(impl->widget, impl->content))
    {
        if (!impl->fullscreenHidden)
        {
            ShowWindow(impl->widget, SW_HIDE);
            if (impl->content != nullptr) ShowWindow(impl->content, SW_HIDE);
            impl->fullscreenHidden = true;
        }
        impl->StopSession();
        return S_FALSE;
    }
    if (impl->fullscreenHidden)
    {
        // Fade-in is the host's job (its visibility animation); here we only
        // restore the window so the host can animate it in.
        ShowWindow(impl->widget, SW_SHOWNOACTIVATE);
            if (impl->content != nullptr) ShowWindow(impl->content, SW_SHOWNOACTIVATE);
        impl->fullscreenHidden = false;
    }

    const float ratio = OcclusionRatio(impl->widget, impl->content, impl->shadow, widgetRect);

    if (ratio >= 0.999f)
    {
        impl->StopSession();
        return S_FALSE;
    }

    const double now = static_cast<double>(GetTickCount64());
    const bool rectChanged=rect.screenX!=impl->lastRect.screenX||rect.screenY!=impl->lastRect.screenY||
        rect.widthPixels!=impl->lastRect.widthPixels||rect.heightPixels!=impl->lastRect.heightPixels||
        rect.radiusPixels!=impl->lastRect.radiusPixels||rect.dpiScale!=impl->lastRect.dpiScale;
    if (!rectChanged&&impl->lastFrameMs != 0.0 && (now - impl->lastFrameMs) < kFrameIntervalMs) return S_OK;
    impl->lastFrameMs = now;

    if (!impl->sessionActive)
    {
        const HRESULT hr = impl->StartSession(rect);
        if (hr == E_PENDING) return S_OK;
        if (FAILED(hr))
        {
            impl->TripIfDeviceError(hr);
            return hr;
        }
        // The first call only starts the session; a frame arrives shortly after.
        return S_OK;
    }
    const LONG shortSide = std::min<LONG>(static_cast<LONG>(rect.widthPixels), static_cast<LONG>(rect.heightPixels));
    const float dpiScale = rect.dpiScale > 0.0f ? rect.dpiScale : 1.0f;
    const float rimPx = std::min(kRimDip * dpiScale, static_cast<float>(shortSide) * 0.45f);
    const HRESULT hr = impl->RenderFrame(rect, rimPx);
    if (FAILED(hr)) impl->TripIfDeviceError(hr);
    if (hr == S_OK) impl->lastRect = rect;
    return hr == S_FALSE ? S_OK : hr;
}

HRESULT LiquidGlassCapture::SetEnabled(bool enabled) noexcept
{
    if (impl_ == nullptr) return E_POINTER;
    impl_->enabled = enabled;
    if (!enabled)
    {
        impl_->StopSession();
        impl_->captureRtv.Reset();
        impl_->outputRtv.Reset();
        impl_->refractionRtv.Reset();
        impl_->tintRtv.Reset();
        impl_->inkColorRtv.Reset();
        impl_->swapChain.Reset();
        impl_->refractionSwapChain.Reset();
        impl_->tintSwapChain.Reset();
        impl_->inkColorSwapChain.Reset();
        impl_->captureTexture.Reset();
        impl_->captureSrv.Reset();
        impl_->captureSrvTexture = nullptr;
        impl_->frameSourceSrv.Reset();
        impl_->frameSourceSrvTexture = nullptr;
        for (auto& texture : impl_->metricsTextures) texture.Reset();
        for (auto& target : impl_->metricsRtvs) target.Reset();
        for (auto& source : impl_->metricsSrvs) source.Reset();
        impl_->metricsInitialized = false;
        impl_->metricsIndex = 0;
    }
    return S_OK;
}

HRESULT LiquidGlassCapture::ResetFuse() noexcept
{
    if (impl_ == nullptr) return E_POINTER;
    impl_->fuseTripped = false;
    impl_->fuseCount = 0;
    return S_OK;
}

bool LiquidGlassCapture::InkIsDark() const noexcept
{
    return impl_ != nullptr && impl_->inkIsDark;
}

bool LiquidGlassCapture::FuseTripped() const noexcept
{
    return impl_ != nullptr && impl_->fuseTripped;
}

void* LiquidGlassCapture::OutputSwapChain() const noexcept
{
    if (impl_ == nullptr) return nullptr;
    return impl_->swapChain.Get();
}

void* LiquidGlassCapture::RefractionSwapChain() const noexcept
{
    if (impl_ == nullptr) return nullptr;
    return impl_->refractionSwapChain.Get();
}

void* LiquidGlassCapture::TintSwapChain() const noexcept
{
    if (impl_ == nullptr) return nullptr;
    return impl_->tintSwapChain.Get();
}

void* LiquidGlassCapture::InkColorSwapChain() const noexcept
{
    if (impl_ == nullptr) return nullptr;
    return impl_->inkColorSwapChain.Get();
}

bool LiquidGlassCapture::HasPresentedFrame() const noexcept
{
    return impl_ != nullptr && impl_->hasPresentedFrame;
}

} // namespace gd::liquidglass

extern "C" __declspec(dllexport) HRESULT __stdcall GdLiquidGlass_Create(HWND widgetWindow, int enabled, void** handle)
{
    if (handle == nullptr) return E_POINTER;
    *handle = nullptr;
    try
    {
        auto* object = new gd::liquidglass::LiquidGlassCapture(widgetWindow, enabled != 0);
        *handle = object;
        return S_OK;
    }
    catch (...)
    {
        return winrt::to_hresult();
    }
}

extern "C" __declspec(dllexport) HRESULT __stdcall GdLiquidGlass_Update(void* handle, int screenX, int screenY, int widthPixels, int heightPixels, float radiusPixels, float dpiScale)
{
    if (handle == nullptr) return E_POINTER;
    auto* object = static_cast<gd::liquidglass::LiquidGlassCapture*>(handle);
    gd::liquidglass::CaptureRect rect{};
    rect.screenX = screenX;
    rect.screenY = screenY;
    rect.widthPixels = widthPixels;
    rect.heightPixels = heightPixels;
    rect.radiusPixels = radiusPixels;
    rect.dpiScale = dpiScale;
    return object->Update(rect);
}

extern "C" __declspec(dllexport) HRESULT __stdcall GdLiquidGlass_SetEnabled(void* handle, int enabled)
{
    if (handle == nullptr) return E_POINTER;
    return static_cast<gd::liquidglass::LiquidGlassCapture*>(handle)->SetEnabled(enabled != 0);
}

extern "C" __declspec(dllexport) HRESULT __stdcall GdLiquidGlass_Destroy(void* handle)
{
    if (handle == nullptr) return E_POINTER;
    delete static_cast<gd::liquidglass::LiquidGlassCapture*>(handle);
    return S_OK;
}

extern "C" __declspec(dllexport) HRESULT __stdcall GdLiquidGlass_ResetFuse(void* handle)
{
    if (handle == nullptr) return E_POINTER;
    return static_cast<gd::liquidglass::LiquidGlassCapture*>(handle)->ResetFuse();
}

extern "C" __declspec(dllexport) HRESULT __stdcall GdLiquidGlass_GetInkIsDark(void* handle, int* isDark)
{
    if (handle == nullptr || isDark == nullptr) return E_POINTER;
    *isDark = static_cast<gd::liquidglass::LiquidGlassCapture*>(handle)->InkIsDark() ? 1 : 0;
    return S_OK;
}

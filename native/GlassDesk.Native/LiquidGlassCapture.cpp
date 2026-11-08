#define NOMINMAX
#include "LiquidGlassCapture.h"
#include "LiquidGlassDiagnostics.h"

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
#include <winrt/Windows.UI.ViewManagement.h>
#include <windows.graphics.capture.interop.h>
#include <windows.graphics.directx.direct3d11.interop.h>
#include <windows.ui.interop.h>

#include <algorithm>
#include <array>
#include <atomic>
#include <cmath>
#include <cstdint>
#include <cstddef>
#include <cstring>
#include <iterator>
#include <limits>
#include <memory>
#include <string>
#include <utility>
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

constexpr int kFuseLimit = 3;
constexpr double kFrameIntervalMs = 1000.0 / 60.0;
constexpr double kStaticFrameIntervalMs = 1000.0 / 12.0;
constexpr double kThemeTransitionMs = 620.0;
constexpr double kDensitySmoothingMs = 500.0;

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
    // The widget's occlusion gate must observe the same completed Win32 move
    // as the crop. DWM extended bounds can still refer to its previous frame.
    return GetWindowRect(window, &bounds) != FALSE;
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

// Capture crop and adaptive metrics are GPU passes. GlassPS combines blurred
// refraction, tint, rim light and bevel; native composition supplies the two
// exterior shadows and places the separately rendered text/icon content above.
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
float3 SrgbToLinear(float3 c) {
    float3 low = c / 12.92;
    float3 high = pow(max((c + 0.055) / 1.055, 0.0), 2.4);
    return lerp(high, low, step(c, 0.04045));
}
float4 CropPS(float4 pos : SV_POSITION) : SV_TARGET {
    float2 screen = cropScreenOrigin + pos.xy;
    float2 minimum = monitorOrigin + 0.5;
    float2 maximum = monitorOrigin + monitorSize - 0.5;
    screen = clamp(screen, minimum, maximum);
    float2 sourcePixel = (screen - monitorOrigin) * contentSize / monitorSize;
    sourcePixel = clamp(sourcePixel, 0.5, contentSize - 0.5);
    float2 uv = sourcePixel / sourceTextureSize;
    float3 encoded = displayTex.SampleLevel(samp, uv, 0).rgb;
    // WGC monitor frames are opaque SDR desktop pixels. The render graph stores
    // linear RGB with alpha=1 and converts back to sRGB only at presentation.
    return float4(SrgbToLinear(encoded), 1.0);
}
)HLSL";

char const* kMetricsShader = R"HLSL(
// GPU-only 5x3 material adaptation, translated from Glasswork adapt.js.
Texture2D<float4> captureTex : register(t0);
Texture2D<float4> previousStats : register(t1);
SamplerState samp : register(s0);
cbuffer MetricsParams : register(b0) {
    float4 widgetRect;
    float4 captureData;
    float4 adaptData;
    float4 paramsPadding;
};
float3 LinearToSrgbMetrics(float3 c) {
    c=saturate(c);
    float3 low=12.92*c;
    float3 high=1.055*pow(c,1.0/2.4)-0.055;
    return lerp(high,low,step(c,0.0031308));
}
struct MetricsOut { float4 stats : SV_TARGET0; float4 ink : SV_TARGET1; };
MetricsOut MetricsPS(float4 pos : SV_POSITION) {
    float luminances[15];
    float3 colors[15];
    int colorOrder[15];
    float minLuminance = 1.0;
    float maxLuminance = 0.0;
    [unroll]
    for (int y = 0; y < 3; ++y) {
        [unroll]
        for (int x = 0; x < 5; ++x) {
            int index = y * 5 + x;
            float2 local = widgetRect.zw * (float2((float)x, (float)y) + 0.5) / float2(5.0, 3.0);
            float2 uv = clamp((widgetRect.xy + local) / captureData.xy, 0.5 / captureData.xy, 1.0 - 0.5 / captureData.xy);
            float4 texel = captureTex.SampleLevel(samp, uv, 0);
            float coverage = max(texel.a, 0.0001);
            float3 color = LinearToSrgbMetrics(texel.rgb / coverage);
            float luminance = dot(color, float3(0.299, 0.587, 0.114));
            colors[index] = color;
            luminances[index] = luminance;
            colorOrder[index] = index;
            minLuminance = min(minLuminance, luminance);
            maxLuminance = max(maxLuminance, luminance);
        }
    }
    // Insertion sort is bounded to these 15 luminance values and removes
    // the two darkest and two brightest samples before averaging.
    [loop]
    for (int i = 1; i < 15; ++i) {
        float value = luminances[i];
        int colorIndex = colorOrder[i];
        int j = i - 1;
        [loop]
        while (j >= 0) {
            if (luminances[j] <= value) break;
            luminances[j + 1] = luminances[j];
            colorOrder[j + 1] = colorOrder[j];
            j -= 1;
        }
        luminances[j + 1] = value;
        colorOrder[j + 1] = colorIndex;
    }
    float3 trimmedRgb = 0.0;
    float trimmedLuminance = 0.0;
    [unroll]
    for (int sampleIndex = 2; sampleIndex < 13; ++sampleIndex) {
        trimmedRgb += colors[colorOrder[sampleIndex]];
        trimmedLuminance += luminances[sampleIndex];
    }
    float3 averageRgb = trimmedRgb / 11.0;
    float backgroundLuminance = trimmedLuminance / 11.0;
    float chroma = saturate(max(averageRgb.r, max(averageRgb.g, averageRgb.b))
        - min(averageRgb.r, min(averageRgb.g, averageRgb.b)));
    float textureContrast = saturate(maxLuminance - minLuminance);
    float inkLuminance = saturate(adaptData.w);
    float closeness = saturate(1.0 - abs(backgroundLuminance - inkLuminance) / max(adaptData.z, 0.01));
    float targetDensity = adaptData.x > 0.5
        ? saturate(0.55 * closeness + 0.7 * textureContrast + 0.3 * chroma)
        : saturate(adaptData.y);
    // High contrast used to pin auto-density at one. Keep adaptation bounded,
    // with a lower ceiling for the more transparent dark material.
    if (adaptData.x > 0.5)
        targetDensity = min(targetDensity, lerp(0.88, 0.72, saturate(paramsPadding.x)));
    float targetKeep = max(0.65, 1.0 - 0.55 * chroma);
    float4 previous = previousStats.SampleLevel(samp, float2(0.5, 0.5), 0);
    float4 current = float4(targetDensity, targetKeep, backgroundLuminance, textureContrast);
    float4 smoothed = captureData.w > 0.5
        ? lerp(previous, current, captureData.z) : current;
    if (adaptData.x <= 0.5) smoothed.r = targetDensity;
    MetricsOut output;
    output.stats = smoothed;
    float inkAlpha = lerp(0.86, 0.94, inkLuminance);
    output.ink = float4(inkLuminance.xxx * inkAlpha, inkAlpha);
    return output;
}
)HLSL";

// The previous 2-DIP, per-channel 13-tap shader was retired; all capture output uses the v0.3 graph below.

char const* kLiquidGlassPanelShader = R"HLSL(
Texture2D<float4> sharpTex : register(t0);
Texture2D<float4> metricsTex : register(t1);
Texture2D<float4> softTex : register(t2);
SamplerState samp : register(s0);
cbuffer PanelParams : register(b0) {
    float4 widgetRect;   // capture-space origin x/y, panel width/height
    float4 captureData;  // capacity width/height, active width/height
    float4 shapeData;    // radius px, maximum B DIP, surface tension, padding px
    float4 opticsData;   // dpi scale, lens enabled, dispersion ratio, opacity
    float4 toneData;     // saturation, brightness, contrast, tint alpha scale
    float4 tintData;     // sRGB tint rgb, reserved
    float4 blurData;     // half-resolution capacity w/h, active w/h
    float4 lightData;    // key gain, counter gain, occlusion alpha, dark theme
};
float sdRoundRect(float2 p,float2 halfSize,float radius) {
    radius=clamp(radius,0.0,min(halfSize.x,halfSize.y));
    float2 q=abs(p)-halfSize+radius;
    return length(max(q,0.0))+min(max(q.x,q.y),0.0)-radius;
}
float surfaceDistance(float2 p,float2 halfSize,float radius) {
    float v=saturate((p.y+halfSize.y)/max(halfSize.y*2.0,1.0));
    float strain=shapeData.z;
    float belly=4.0*v*(1.0-v);
    float amount=abs(strain)*belly*belly*(0.78+0.22*(strain>=0?1.0:-1.0)*(2.0*v-1.0));
    float limit=min(7.0*opticsData.x,min(halfSize.y*0.07,halfSize.x*0.036));
    float factor=1.0-min(amount,1.15)*limit/max(halfSize.x,1.0);
    p.x/=factor;
    return sdRoundRect(p,halfSize,radius)*factor;
}
float3 readSharp(float2 pixel) {
    float2 cap=max(captureData.xy,1.0.xx);
    float2 lo=0.5/cap,hi=(captureData.zw-0.5)/cap;
    return sharpTex.SampleLevel(samp,clamp(pixel/cap,lo,hi),0).rgb;
}
float3 readSoft(float2 pixel) {
    float2 cap=max(blurData.xy,1.0.xx);
    float2 lo=0.5/cap,hi=(blurData.zw-0.5)/cap;
    return softTex.SampleLevel(samp,clamp(pixel/(cap*2.0),lo,hi),0).rgb;
}
float3 saturateBy(float3 c,float value) {
    float l=dot(c,float3(0.213,0.715,0.072));
    return l.xxx+(c-l.xxx)*value;
}
float3 srgbToLinear(float3 c) {
    float3 low=c/12.92;
    float3 high=pow(max((c+0.055)/1.055,0.0),2.4);
    return lerp(high,low,step(c,0.04045));
}
float4 GlassPS(float4 pos : SV_POSITION) : SV_TARGET {
    float2 halfSize=widgetRect.zw*0.5;
    float2 local=pos.xy-widgetRect.xy-halfSize;
    float radius=min(shapeData.x,min(halfSize.x,halfSize.y));
    float d=surfaceDistance(local,halfSize,radius);
    // The shared Composition panel clip owns this contour. Leave a one-pixel
    // color apron and do not apply a second shader coverage mask.
    if(d>1.0)return 0.0;
    float dpi=max(opticsData.x,0.01);
    float shortSide=min(widgetRect.z,widgetRect.w);
    float shortDip=shortSide/dpi;
    float smallFade=smoothstep(0.0,12.0,shortDip);
    // Thickness grows continuously with the short side; rimMax (shapeData.y) remains the absolute ceiling.
    float rimFloor=14.0;
    float rimCeil=min(shapeData.y,56.0);
    float rimCap=clamp(shortDip*0.28,rimFloor,rimCeil);
    float B=round(max(4.0,min(rimCap,shortDip/3.2)))*dpi*smallFade;
    float dCapDip=clamp(shortDip*0.11,6.0,22.0);
    float D=min(dCapDip*dpi,0.78*B)*smallFade;
    float u=saturate(max(-d,0.0)/max(B,0.001));
    float eps=clamp(B*0.18,0.75,4.0);
    float2 g=float2(
        surfaceDistance(local+float2(eps,0),halfSize,radius)-surfaceDistance(local-float2(eps,0),halfSize,radius),
        surfaceDistance(local+float2(0,eps),halfSize,radius)-surfaceDistance(local-float2(0,eps),halfSize,radius))/(2.0*eps);
    float gl=length(g);
    float2 outward=g/max(gl,1e-5);
    float x=max(u,0.0001),v=1.0-x;
    const float lensPower=3.2,lensEdge=1.55;
    float base=max(1.0-pow(v,lensPower),1e-6);
    float slope=(0.80*lensEdge)*pow(v,lensPower-1.0)/pow(base,1.0-1.0/lensPower);
    float cosI=rsqrt(1.0+slope*slope),sinI=slope*cosI;
    float sinT=sinI/1.50,cosT=sqrt(max(1.0-sinT*sinT,0.0));
    float tanDelta=(sinI*cosT-cosI*sinT)/max(cosI*cosT+sinI*sinT,1e-5);
    float bendNorm=saturate(tanDelta/sqrt(1.50*1.50-1.0));
    float axisFade=smoothstep(0.10,0.75,gl);
    float joinFade=1.0-smoothstep(0.88,1.0,u);
    float amount=D*bendNorm*joinFade*axisFade*opticsData.y;
    float2 offset=-outward*amount;
    float inside=max(-d,0.0);
    float split=min(amount*opticsData.z,0.45*dpi)*smoothstep(0.5*dpi,1.8*dpi,inside)*joinFade;
    float3 sharp=readSharp(pos.xy+offset);
    float3 soft=readSoft(pos.xy+offset);
    float crisp=0.82*(1.0-u)*(1.0-u);
    float3 color=lerp(soft,sharp,crisp);
    if(split>0.001) {
        float3 r=lerp(readSoft(pos.xy+offset+outward*split),readSharp(pos.xy+offset+outward*split),crisp);
        float3 b=lerp(readSoft(pos.xy+offset-outward*split),readSharp(pos.xy+offset-outward*split),crisp);
        color=float3(r.r,color.g,b.b);
    }
    float4 metrics=metricsTex.SampleLevel(samp,0.5.xx,0);
    float dark=saturate(lightData.w);
    color=saturateBy(color,clamp(toneData.x,1.0,1.15));
    color=(color-0.5)*clamp(toneData.z,0.95,1.05)+0.5;
    color*=clamp(toneData.y,0.9,1.1);
    float3 tint=srgbToLinear(saturate(tintData.rgb));
    float baseTint=lerp(0.10,0.055,dark)*clamp(toneData.w,0.0,1.5);
    float tintAlpha=min(lerp(0.18,0.10,dark),baseTint*lerp(0.85,1.0,saturate(metrics.r)));
    color=lerp(color,tint,tintAlpha);
    float2 lightDir=normalize(float2(-0.55,-0.83));
    float key=pow(saturate(dot(outward,lightDir)),5.0);
    float opposite=pow(saturate(dot(outward,-lightDir)),7.0);
    float width=max(dpi,1.0);
    float rim=1.0-smoothstep(0.0,width,inside);
    float fresnel=0.04+0.96*pow(1.0-saturate(cosI),5.0);
    float H=saturate(rim*(0.035+lightData.x*key+lightData.y*opposite)+0.025*fresnel*(1.0-u));
    H=saturate((H+0.018*(1.0-u)*key)*opticsData.y);
    float shadeBand=smoothstep(width,2.0*width,inside)*(1.0-smoothstep(2.0*width,4.0*width,inside));
    float innerOcclusion=lightData.z*shadeBand*opposite*(1.0-saturate(H*4.0));
    float3 shaded=saturate(color*(1.0-innerOcclusion));
    float3 lightRGB=1.0.xxx;
    float3 E=saturate(lightRGB*H);
    float3 result=1.0-(1.0-shaded)*(1.0-E);
    return float4(result,1.0);
}
)HLSL";

char const* kGaussianShader = R"HLSL(
Texture2D<float4> inputTex : register(t0);
SamplerState samp : register(s0);
cbuffer GaussianParams : register(b0) {
    float4 inputData;  // capacity width/height, active width/height
    float4 outputData; // capacity width/height, active width/height
    float4 blurData;   // sigma in source pixels, axis x/y, half-resolution pass
};
float4 GaussianPS(float4 pos : SV_POSITION) : SV_TARGET {
    float2 inputCap=max(inputData.xy,1.0.xx);
    float2 inputActive=max(inputData.zw,1.0.xx);
    float2 outputPixel=pos.xy;
    float2 basePixel=blurData.z>0.5 ? outputPixel*2.0 : outputPixel;
    float2 axis=blurData.y<0.5 ? float2(1,0) : float2(0,1);
    float sigma=max(blurData.x,0.001);
    int radius=(int)ceil(3.0*sigma);
    float2 lo=0.5/inputCap,hi=(inputActive-0.5)/inputCap;
    float2 uv=clamp(basePixel/inputCap,lo,hi);
    float4 sum=inputTex.SampleLevel(samp,uv,0);
    float total=1.0;
    [loop]
    for(int i=1;i<=radius;i+=2) {
        float w0=exp(-(float(i*i))/(2.0*sigma*sigma));
        int next=i+1;
        float w1=next<=radius ? exp(-(float(next*next))/(2.0*sigma*sigma)) : 0.0;
        float weight=w0+w1;
        float offset=(float)i+(weight>0.0?w1/weight:0.0);
        float2 delta=axis*(offset/inputCap);
        sum+=inputTex.SampleLevel(samp,clamp(uv+delta,lo,hi),0)*weight;
        sum+=inputTex.SampleLevel(samp,clamp(uv-delta,lo,hi),0)*weight;
        total+=2.0*weight;
    }
    return sum/total;
}
)HLSL";

char const* kLayerShader = R"HLSL(
Texture2D<float4> lowerSharp : register(t0);
Texture2D<float4> lowerSoft : register(t1);
SamplerState samp : register(s0);
cbuffer LayerParams : register(b0) {
    float4 shapeRect;    // x/y in capture pixels, width/height
    float4 captureData;  // sharp capacity width/height, active width/height
    float4 shapeData;    // radius px, B default DIP, D default DIP, materialize
    float4 opticsData;   // dpi, dispersion, press, surface tension
    float4 tintData;     // sRGB tint rgb, tint alpha
    float4 blurData;     // half capacity width/height, active width/height
    float4 toneData;     // saturation, brightness, contrast, dark theme blend
    float4 lightData;    // key gain, counter gain, occlusion alpha, layer id
};
float sdRoundRect(float2 p,float2 halfSize,float radius) {
    radius=clamp(radius,0.0,min(halfSize.x,halfSize.y));
    float2 q=abs(p)-halfSize+radius;
    return length(max(q,0.0))+min(max(q.x,q.y),0.0)-radius;
}
float sdShape(float2 p,float2 halfSize,float radius) {
    float v=saturate((p.y+halfSize.y)/max(halfSize.y*2.0,1.0));
    float belly=4.0*v*(1.0-v),strain=opticsData.w;
    float amount=abs(strain)*belly*belly*(0.78+0.22*(strain>=0?1.0:-1.0)*(2.0*v-1.0));
    float limit=min(7.0*opticsData.x,min(halfSize.y*0.07,halfSize.x*0.036));
    float factor=1.0-min(amount,1.15)*limit/max(halfSize.x,1.0);
    p.x/=factor;
    return sdRoundRect(p,halfSize,radius)*factor;
}
float3 readSharp(float2 pixel) {
    float2 cap=max(captureData.xy,1.0.xx);
    float2 lo=0.5/cap,hi=(captureData.zw-0.5)/cap;
    return lowerSharp.SampleLevel(samp,clamp(pixel/cap,lo,hi),0).rgb;
}
float3 readSoft(float2 pixel) {
    float2 cap=max(blurData.xy,1.0.xx);
    float2 lo=0.5/cap,hi=(blurData.zw-0.5)/cap;
    return lowerSoft.SampleLevel(samp,clamp(pixel/(cap*2.0),lo,hi),0).rgb;
}
float3 saturateBy(float3 c,float value) {
    float l=dot(c,float3(0.213,0.715,0.072));return l.xxx+(c-l.xxx)*value;
}
float3 srgbToLinear(float3 c) {
    float3 low=c/12.92,high=pow(max((c+0.055)/1.055,0.0),2.4);
    return lerp(high,low,step(c,0.04045));
}
float4 LayerPS(float4 pos : SV_POSITION) : SV_TARGET {
    float2 halfSize=shapeRect.zw*0.5;
    float2 local=pos.xy-shapeRect.xy-halfSize;
    float radius=min(shapeData.x,min(halfSize.x,halfSize.y));
    float d=sdShape(local,halfSize,radius);
    float dpi=max(opticsData.x,0.01);
    float isLayer3=step(2.5,lightData.w);
    float shadowOffset=isLayer3?0.0:2.0*dpi;
    float shadowDistance=sdShape(local-float2(0,shadowOffset),halfSize,radius);
    float outside=max(shadowDistance,0.0);
    float shadowSigma=(isLayer3?3.0:5.0)*dpi;
    float shadowGain=lerp(isLayer3?0.04:0.08,isLayer3?0.05:0.10,saturate(toneData.w));
    float shadowAlpha=shadowGain*exp(-(outside*outside)/(2.0*shadowSigma*shadowSigma))
        *(1.0-smoothstep(2.0*shadowSigma,3.0*shadowSigma,outside))*shapeData.w;
    float coverage=1.0-smoothstep(-0.5,0.5,d);
    if(coverage<0.0001)
        return float4(0,0,0,shadowAlpha);
    float shortSide=min(shapeRect.z,shapeRect.w);
    float fade=smoothstep(0.0,12.0,shortSide/dpi);
    float shortDip=shortSide/dpi;
    float B=round(max(4.0,min(shapeData.y,shortDip/3.2)))*dpi*fade;
    float D=min(shapeData.z*dpi,0.78*B)*fade;
    float u=saturate(max(-d,0.0)/max(B,0.001));
    float eps=clamp(B*0.18,0.75,4.0);
    float2 g=float2(sdShape(local+float2(eps,0),halfSize,radius)-sdShape(local-float2(eps,0),halfSize,radius),
        sdShape(local+float2(0,eps),halfSize,radius)-sdShape(local-float2(0,eps),halfSize,radius))/(2.0*eps);
    float gl=length(g);float2 outward=g/max(gl,1e-5);
    float x=max(u,0.0001),v=1.0-x;
    const float lensPower=3.2,lensEdge=1.55;
    float base=max(1.0-pow(v,lensPower),1e-6);
    float slope=(0.80*lensEdge)*pow(v,lensPower-1.0)/pow(base,1.0-1.0/lensPower);
    float cosI=rsqrt(1.0+slope*slope),sinI=slope*cosI;
    float sinT=sinI/1.50,cosT=sqrt(max(1.0-sinT*sinT,0.0));
    float tanDelta=(sinI*cosT-cosI*sinT)/max(cosI*cosT+sinI*sinT,1e-5);
    float bendNorm=saturate(tanDelta/sqrt(1.50*1.50-1.0));
    float joinFade=1.0-smoothstep(0.88,1.0,u);
    float amount=D*bendNorm*joinFade*smoothstep(0.10,0.75,gl)*shapeData.w;
    float2 offset=-outward*amount;
    float inside=max(-d,0.0);
    float split=min(amount*opticsData.y,0.45*dpi)*smoothstep(0.5*dpi,1.8*dpi,inside)*joinFade;
    float3 sharp=readSharp(pos.xy+offset),soft=readSoft(pos.xy+offset);
    float crisp=0.82*(1.0-u)*(1.0-u);
    float3 color=lerp(soft,sharp,crisp);
    if(split>0.001) {
        float3 r=lerp(readSoft(pos.xy+offset+outward*split),readSharp(pos.xy+offset+outward*split),crisp);
        float3 b=lerp(readSoft(pos.xy+offset-outward*split),readSharp(pos.xy+offset-outward*split),crisp);
        color=float3(r.r,color.g,b.b);
    }
    color=saturateBy(color,clamp(toneData.x,1.0,1.15));
    color=(color-0.5)*clamp(toneData.z,0.95,1.05)+0.5;
    color*=clamp(toneData.y,0.9,1.1);
    color=lerp(color,srgbToLinear(saturate(tintData.rgb)),saturate(tintData.a));
    float2 L=normalize(float2(-0.55,-0.83));
    float key=pow(saturate(dot(outward,L)),5.0),opposite=pow(saturate(dot(outward,-L)),7.0);
    float width=max(dpi,1.0),rim=1.0-smoothstep(0.0,width,inside);
    float fresnel=0.04+0.96*pow(1.0-saturate(cosI),5.0);
    float H=saturate(rim*(0.035+lightData.x*key+lightData.y*opposite)+0.025*fresnel*(1.0-u));
    H=saturate((H+0.018*(1.0-u)*key+0.06*opticsData.z)*shapeData.w);
    float shadeBand=smoothstep(width,2.0*width,inside)*(1.0-smoothstep(2.0*width,4.0*width,inside));
    float innerOcclusion=lightData.z*shadeBand*opposite*(1.0-saturate(H*4.0));
    float3 shaded=saturate(color*(1.0-innerOcclusion));
    float3 result=1.0-(1.0-shaded)*(1.0-saturate(H).xxx);
    return float4(result*coverage*shapeData.w,coverage*shapeData.w);
}
)HLSL";

char const* kPresentShader = R"HLSL(
Texture2D<float4> sceneTex : register(t0);
SamplerState samp : register(s0);
cbuffer PresentParams : register(b0) {
    float4 sourceData; // capacity width/height, active width/height
    float4 targetData; // apron x/y, output width/height
};
float3 LinearToSrgb(float3 c) {
    c=max(c,0.0);
    float3 low=12.92*c;
    float3 high=1.055*pow(c,1.0/2.4)-0.055;
    return lerp(high,low,step(c,0.0031308));
}
float4 PresentPS(float4 pos : SV_POSITION) : SV_TARGET {
    float2 cap=max(sourceData.xy,1.0.xx),active=max(sourceData.zw,1.0.xx);
    float2 pixel=targetData.xy+pos.xy;
    float2 uv=clamp(pixel/cap,0.5/cap,(active-0.5)/cap);
    float3 linearRgb=sceneTex.SampleLevel(samp,uv,0).rgb;
    return float4(saturate(LinearToSrgb(linearRgb)),1.0);
}
)HLSL";

char const* kEmptyCompatibilityShader = R"HLSL(
float4 EmptyPS(float4 pos : SV_POSITION) : SV_TARGET { return 0.0; }
)HLSL";

// Every cbuffer is 16-byte aligned and mirrors the HLSL field order.
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
    float captureWidth{}, captureHeight{}, smoothing{}, initialized{};
    float autoDensity{}, manualDensity{}, threshold{}, inkLight{};
    float paddingX{}, paddingY{}, paddingZ{}, paddingW{};
};
static_assert(sizeof(MetricsParams) == 64, "MetricsParams must be 64 bytes");

struct OutputBundle
{
    ComPtr<IDXGISwapChain1> output;
    ComPtr<IDXGISwapChain1> refraction;
    ComPtr<IDXGISwapChain1> tint;
    ComPtr<ID3D11RenderTargetView> outputRtv;
    ComPtr<ID3D11RenderTargetView> refractionRtv;
    ComPtr<ID3D11RenderTargetView> tintRtv;
    struct RenderTexture
    {
        ComPtr<ID3D11Texture2D> texture;
        ComPtr<ID3D11RenderTargetView> rtv;
        ComPtr<ID3D11ShaderResourceView> srv;
    };
    struct RenderGraph
    {
        std::array<RenderTexture,2> scene;
        std::array<RenderTexture,3> blur;
        RenderTexture blurScratch;
        UINT capacityWidth{},capacityHeight{};
    } graph;
};

struct PanelParamsV3
{
    float widgetOriginX{},widgetOriginY{},widgetWidth{},widgetHeight{};
    float captureCapacityWidth{},captureCapacityHeight{},captureActiveWidth{},captureActiveHeight{};
    float radiusPixels{},rimMaxDip{},surfaceTension{},paddingPixels{};
    float dpiScale{1.0f},lensEnabled{1.0f},dispersion{},opacity{1.0f};
    float saturation{1.0f},brightness{1.0f},contrast{1.0f},tintScale{1.0f};
    float tintR{},tintG{},tintB{},tintPadding{};
    float blurCapacityWidth{},blurCapacityHeight{},blurActiveWidth{},blurActiveHeight{};
    float keyGain{},counterGain{},occlusionAlpha{},darkThemeWeight{};
};
static_assert(sizeof(PanelParamsV3)==128);

struct LayerParamsV1
{
    float shapeX{},shapeY{},shapeWidth{},shapeHeight{};
    float captureCapacityWidth{},captureCapacityHeight{},captureActiveWidth{},captureActiveHeight{};
    float radiusPixels{},bandDip{},displacementDip{},materialize{};
    float dpiScale{1.0f},dispersion{},press{},surfaceTension{};
    float tintR{},tintG{},tintB{},tintAlpha{};
    float blurCapacityWidth{},blurCapacityHeight{},blurActiveWidth{},blurActiveHeight{};
    float saturation{1.0f},brightness{1.0f},contrast{1.0f},darkThemeWeight{};
    float keyGain{},counterGain{},occlusionAlpha{},layer{};
};
static_assert(sizeof(LayerParamsV1)==128);

struct GaussianParams
{
    float inputCapacityWidth{},inputCapacityHeight{},inputActiveWidth{},inputActiveHeight{};
    float outputCapacityWidth{},outputCapacityHeight{},outputActiveWidth{},outputActiveHeight{};
    float sigmaPixels{},axis{},halfResolution{},padding{};
};
static_assert(sizeof(GaussianParams)==48);

struct PresentParams
{
    float sourceCapacityWidth{},sourceCapacityHeight{},sourceActiveWidth{},sourceActiveHeight{};
    float apronX{},apronY{},outputWidth{},outputHeight{};
};
static_assert(sizeof(PresentParams)==32);

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
    bool settingsInitialized{};
    bool themeTransitionActive{};
    bool resolvedThemeDark{};
    double themeTransitionStartMs{};
    float themeInkFrom{};
    float themeDarkFrom{};
    LiquidGlassMaterialSettings themeMaterialFrom{};
    GDLiquidGlassSettings materialSettings{};
    double previousMetricsMs{};
    UINT metricsIndex{};
    ULONGLONG lastLayerLogMs{};
    uint32_t lastLayer2Count{~0u};
    uint32_t lastLayer3Count{~0u};
    bool sessionActive{};
    bool fullscreenHidden{};
    HMONITOR captureMonitor{};
    double lastFrameMs{};
    double lastMotionMs{};
    bool hasPresentedFrame{};
    CaptureRect lastRect{};
    bool waitingFrameLogged{};
    bool activeFrameLogged{};
    CaptureFailureStage lastFailureStage{CaptureFailureStage::None};
    wchar_t const* lastFailureStep{L"none"};
    HRESULT lastFailureStepHr{S_OK};
    wchar_t const* lastLoggedStep{nullptr};
    HRESULT lastLoggedStepHr{S_OK};
    ULONGLONG lastLoggedStepMs{};
    std::vector<GDGlassShapeV1> sceneShapes;
    uint64_t sceneSequence{};
    uint64_t sourceGeneration{};
    DXGI_FORMAT lastSourceFormat{DXGI_FORMAT_UNKNOWN};
    UINT lastSourceWidth{},lastSourceHeight{},lastContentWidth{},lastContentHeight{};
    UINT lastCaptureActiveWidth{},lastCaptureActiveHeight{},lastCaptureCapacityWidth{},lastCaptureCapacityHeight{};
    LONG lastPaddingPixels{},lastMonitorWidth{},lastMonitorHeight{};

    ComPtr<ID3D11Device> device;
    ComPtr<ID3D11DeviceContext> context;
    ComPtr<ID3D11Texture2D> captureTexture;
    ComPtr<ID3D11RenderTargetView> captureRtv;
    UINT captureCapacityWidth{},captureCapacityHeight{};
    ComPtr<IDXGISwapChain1> inkColorSwapChain;
    ComPtr<ID3D11RenderTargetView> inkColorRtv;
    OutputBundle outputs;
    OutputBundle spareOutputs;
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
    ComPtr<ID3D11PixelShader> layerPixelShader;
    ComPtr<ID3D11PixelShader> gaussianPixelShader;
    ComPtr<ID3D11PixelShader> presentPixelShader;
    ComPtr<ID3D11PixelShader> refractionPixelShader;
    ComPtr<ID3D11PixelShader> tintPixelShader;
    ComPtr<ID3D11SamplerState> sampler;
    ComPtr<ID3D11RasterizerState> rasterizerState;
    ComPtr<ID3D11RasterizerState> scissorRasterizerState;
    ComPtr<ID3D11BlendState> premultipliedBlendState;
    ComPtr<ID3D11Buffer> paramsBuffer;
    ComPtr<ID3D11Buffer> cropParamsBuffer;
    ComPtr<ID3D11Buffer> metricsParamsBuffer;
    ComPtr<ID3D11Buffer> layerParamsBuffer;
    ComPtr<ID3D11Buffer> gaussianParamsBuffer;
    ComPtr<ID3D11Buffer> presentParamsBuffer;
    IDirect3DDevice winrtDevice{nullptr};
    GraphicsCaptureItem captureItem{nullptr};
    Direct3D11CaptureFramePool framePool{nullptr};
    Direct3D11CaptureFrame retainedFrame{nullptr};
    GraphicsCaptureSession session{nullptr};
    winrt::Windows::UI::ViewManagement::UISettings systemUiSettings{nullptr};
    std::shared_ptr<BorderlessAccessResult> borderlessAccess{std::make_shared<BorderlessAccessResult>()};
    winrt::Windows::Foundation::IAsyncOperation<AppCapabilityAccessStatus> borderlessAccessRequest{nullptr};

    static float FiniteClamp(float value, float fallback, float minimum, float maximum) noexcept
    {
        return std::isfinite(value) ? std::clamp(value, minimum, maximum) : fallback;
    }

    static LiquidGlassMaterialSettings SanitizeMaterial(LiquidGlassMaterialSettings value,
        LiquidGlassMaterialSettings const& fallback) noexcept
    {
        value.opacity = FiniteClamp(value.opacity, fallback.opacity, 0.0f, 1.5f);
        value.thin = FiniteClamp(value.thin, fallback.thin, 0.0f, 0.8f);
        value.dense = FiniteClamp(value.dense, fallback.dense, 0.0f, 0.95f);
        value.blur = FiniteClamp(value.blur, fallback.blur, 1.0f, 6.0f);
        value.saturation = FiniteClamp(value.saturation, fallback.saturation, 1.0f, 1.15f);
        value.brightness = FiniteClamp(value.brightness, fallback.brightness, 0.9f, 1.1f);
        value.contrast = FiniteClamp(value.contrast, fallback.contrast, 0.95f, 1.05f);
        value.rimLight = FiniteClamp(value.rimLight, fallback.rimLight, 0.0f, 2.0f);
        value.bevel = FiniteClamp(value.bevel, fallback.bevel, 0.0f, 2.5f);
        value.shadow = FiniteClamp(value.shadow, fallback.shadow, 0.0f, 3.0f);
        return value;
    }

    static GDLiquidGlassSettings SanitizeSettings(GDLiquidGlassSettings value) noexcept
    {
        GDLiquidGlassSettings defaults{};
        value.light = SanitizeMaterial(value.light, defaults.light);
        value.dark = SanitizeMaterial(value.dark, defaults.dark);
        value.theme = value.theme >= 0 && value.theme <= 2 ? value.theme : 2;
        value.ink = value.ink >= 0 && value.ink <= 2 ? value.ink : 0;
        value.autoDensity = value.autoDensity != 0 ? 1u : 0u;
        value.lensEnabled = value.lensEnabled != 0 ? 1u : 0u;
        value.rimMaxPixels = FiniteClamp(value.rimMaxPixels, 56.0f, 1.0f, 96.0f);
        value.edge = FiniteClamp(value.edge, 1.55f, 0.0f, 4.0f);
        value.power = FiniteClamp(value.power, 3.2f, 1.0f, 6.0f);
        value.dispersion = FiniteClamp(value.dispersion, 0.030f, 0.0f, 0.040f);
        value.centerDispersion = 0.0f;
        value.density = FiniteClamp(value.density, 0.35f, 0.0f, 1.0f);
        value.threshold = FiniteClamp(value.threshold, 0.5f, 0.2f, 0.8f);
        // 0.3 fixed shipping preset; existing saved tuning must not mask the new optics.
        value.rimMaxPixels=56.0f;value.edge=1.55f;value.power=3.2f;
        value.dispersion=0.030f;value.centerDispersion=0.0f;
        value.light.blur=2.6f;value.dark.blur=1.4f;
        value.size = sizeof(GDLiquidGlassSettings);
        value.version = 1;
        return value;
    }

    bool DetectDarkTheme(int theme) noexcept
    {
        if (theme == 0) return false;
        if (theme == 1) return true;
        try
        {
            using namespace winrt::Windows::UI::ViewManagement;
            if (!systemUiSettings) systemUiSettings = UISettings();
            const auto background = systemUiSettings.GetColorValue(UIColorType::Background);
            return 0.299f * background.R + 0.587f * background.G + 0.114f * background.B < 127.5f;
        }
        catch (...) {}
        const COLORREF background = GetSysColor(COLOR_WINDOW);
        const float luminance = 0.299f * static_cast<float>(GetRValue(background)) / 255.0f
            + 0.587f * static_cast<float>(GetGValue(background)) / 255.0f
            + 0.114f * static_cast<float>(GetBValue(background)) / 255.0f;
        return luminance < 0.5f;
    }

    LiquidGlassMaterialSettings TargetMaterial() const noexcept
    {
        return resolvedThemeDark ? materialSettings.dark : materialSettings.light;
    }

    float TargetInkLight() const noexcept
    {
        if (materialSettings.ink == 1) return 0.0f;
        if (materialSettings.ink == 2) return 1.0f;
        return resolvedThemeDark ? 1.0f : 0.0f;
    }

    static LiquidGlassMaterialSettings MixMaterial(LiquidGlassMaterialSettings const& from,
        LiquidGlassMaterialSettings const& to, float amount) noexcept
    {
        auto mix = [amount](float a, float b) { return a + (b - a) * amount; };
        LiquidGlassMaterialSettings value = to;
        value.opacity = mix(from.opacity, to.opacity);
        value.thin = mix(from.thin, to.thin);
        value.dense = mix(from.dense, to.dense);
        value.blur = mix(from.blur, to.blur);
        value.saturation = mix(from.saturation, to.saturation);
        value.brightness = mix(from.brightness, to.brightness);
        value.contrast = mix(from.contrast, to.contrast);
        value.rimLight = mix(from.rimLight, to.rimLight);
        value.bevel = mix(from.bevel, to.bevel);
        value.shadow = mix(from.shadow, to.shadow);
        auto mixByte = [amount](uint32_t a, uint32_t b, unsigned shift)
        {
            const float first = static_cast<float>((a >> shift) & 0xffu);
            const float second = static_cast<float>((b >> shift) & 0xffu);
            return static_cast<uint32_t>(std::lround(first + (second - first) * amount));
        };
        value.tintArgb = (mixByte(from.tintArgb, to.tintArgb, 24) << 24)
            | (mixByte(from.tintArgb, to.tintArgb, 16) << 16)
            | (mixByte(from.tintArgb, to.tintArgb, 8) << 8)
            | mixByte(from.tintArgb, to.tintArgb, 0);
        return value;
    }

    float TransitionAmount(double now) noexcept
    {
        if (!themeTransitionActive) return 1.0f;
        const float amount = std::clamp(static_cast<float>((now - themeTransitionStartMs) / kThemeTransitionMs), 0.0f, 1.0f);
        if (amount >= 1.0f) themeTransitionActive = false;
        return amount * amount * (3.0f - 2.0f * amount);
    }

    LiquidGlassMaterialSettings ActiveMaterial(double now) noexcept
    {
        const float amount = TransitionAmount(now);
        return themeTransitionActive ? MixMaterial(themeMaterialFrom, TargetMaterial(), amount) : TargetMaterial();
    }

    float ActiveInkLight(double now) noexcept
    {
        const float amount = TransitionAmount(now);
        return themeTransitionActive ? themeInkFrom + (TargetInkLight() - themeInkFrom) * amount : TargetInkLight();
    }

    float ActiveDarkWeight(double now) noexcept
    {
        const float amount = TransitionAmount(now);
        const float target = resolvedThemeDark ? 1.0f : 0.0f;
        return themeTransitionActive ? themeDarkFrom + (target - themeDarkFrom) * amount : target;
    }

    void RefreshSystemTheme(double now) noexcept
    {
        const bool detected = DetectDarkTheme(materialSettings.theme);
        if (!settingsInitialized)
        {
            resolvedThemeDark = detected;
            settingsInitialized = true;
            themeInkFrom = TargetInkLight();
            themeDarkFrom = detected ? 1.0f : 0.0f;
            return;
        }
        if (detected == resolvedThemeDark) return;
        themeMaterialFrom = ActiveMaterial(now);
        themeInkFrom = ActiveInkLight(now);
        themeDarkFrom = ActiveDarkWeight(now);
        resolvedThemeDark = detected;
        themeTransitionStartMs = now;
        themeTransitionActive = true;
    }

    HRESULT Fail(wchar_t const* step,HRESULT hr,void const* detail=nullptr,size_t detailBytes=0) noexcept
    {
        lastFailureStep=step;lastFailureStepHr=hr;
        const ULONGLONG now=GetTickCount64();
        // 同一 (step,hr) 5 秒内只写一次，防止 16ms 定时器刷屏
        if(lastLoggedStep==step&&lastLoggedStepHr==hr&&now-lastLoggedStepMs<5000)return hr;
        lastLoggedStep=step;lastLoggedStepHr=hr;lastLoggedStepMs=now;
        try
        {
            std::wstring text;
            if(detail&&detailBytes>0)
            {
                std::string narrow(static_cast<char const*>(detail),detailBytes);
                while(!narrow.empty()&&narrow.back()=='\0')narrow.pop_back();
                const int count=MultiByteToWideChar(CP_ACP,0,narrow.data(),static_cast<int>(narrow.size()),nullptr,0);
                if(count>0){text.resize(static_cast<size_t>(count));
                    MultiByteToWideChar(CP_ACP,0,narrow.data(),static_cast<int>(narrow.size()),text.data(),count);}
                for(auto& ch:text)if(ch==L'\r'||ch==L'\n'||ch==L'\t'||ch==L'"')ch=L' ';
                if(text.size()>1200)text.resize(1200);
            }
            wchar_t fields[1600]{};
            swprintf_s(fields,std::size(fields),
                L"stage=%ls step=%ls hr=0x%08X backend=WGC captureGeneration=%llu shaderVersion=0.3 detail=\"%ls\"",
                CaptureFailureStageName(lastFailureStage),step,static_cast<unsigned>(hr),
                static_cast<unsigned long long>(sourceGeneration),text.empty()?L"none":text.c_str());
            diagnostics::Log(L"capture-step-error",fields);
        }
        catch(...){}
        return hr;
    }

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
                    if (FAILED(hr)) return Fail(L"d3d-create-device",hr);
                }
            }
            if (!context) return Fail(L"ensure-context-null",E_POINTER);
            if (vertexShader && capturePixelShader && metricsPixelShader && pixelShader && layerPixelShader &&
                gaussianPixelShader && presentPixelShader && refractionPixelShader && tintPixelShader &&
                sampler && rasterizerState && scissorRasterizerState && premultipliedBlendState && paramsBuffer &&
                cropParamsBuffer && metricsParamsBuffer && layerParamsBuffer && gaussianParamsBuffer &&
                presentParamsBuffer && winrtDevice) return S_OK;
            ComPtr<IDXGIDevice> dxgiDevice;
            hr = device.As(&dxgiDevice);
            if (FAILED(hr)) return Fail(L"query-dxgi-device",hr);
            // Correct interop helper for a DXGI *device* (not a surface):
            // CreateDirect3D11DeviceFromDXGIDevice. The Surface variant takes
            // an IDXGISurface* and would not accept an IDXGIDevice*.
            hr = CreateDirect3D11DeviceFromDXGIDevice(dxgiDevice.Get(), reinterpret_cast<IInspectable**>(winrt::put_abi(winrtDevice)));
            if (FAILED(hr)) return Fail(L"create-winrt-device",hr);
            D3D11_SAMPLER_DESC samplerDesc{};
            samplerDesc.Filter = D3D11_FILTER_MIN_MAG_MIP_LINEAR;
            samplerDesc.AddressU = D3D11_TEXTURE_ADDRESS_CLAMP;
            samplerDesc.AddressV = D3D11_TEXTURE_ADDRESS_CLAMP;
            samplerDesc.AddressW = D3D11_TEXTURE_ADDRESS_CLAMP;
            samplerDesc.ComparisonFunc = D3D11_COMPARISON_NEVER;
            samplerDesc.MinLOD = 0.0f;
            samplerDesc.MaxLOD = D3D11_FLOAT32_MAX;
            hr = device->CreateSamplerState(&samplerDesc, sampler.ReleaseAndGetAddressOf());
            if (FAILED(hr)) return Fail(L"create-sampler",hr);
            D3D11_RASTERIZER_DESC rasterizerDesc{};
            rasterizerDesc.FillMode = D3D11_FILL_SOLID;
            rasterizerDesc.CullMode = D3D11_CULL_NONE;
            rasterizerDesc.DepthClipEnable = TRUE;
            rasterizerDesc.ScissorEnable = FALSE;
            rasterizerDesc.MultisampleEnable = FALSE;
            hr = device->CreateRasterizerState(&rasterizerDesc, rasterizerState.ReleaseAndGetAddressOf());
            if (FAILED(hr)) return Fail(L"create-rasterizer",hr);
            rasterizerDesc.ScissorEnable = TRUE;
            hr = device->CreateRasterizerState(&rasterizerDesc, scissorRasterizerState.ReleaseAndGetAddressOf());
            if (FAILED(hr)) return Fail(L"create-scissor-rasterizer",hr);
            D3D11_BLEND_DESC blendDesc{};
            auto& blend = blendDesc.RenderTarget[0];
            blend.BlendEnable = TRUE;
            blend.SrcBlend = D3D11_BLEND_ONE;
            blend.DestBlend = D3D11_BLEND_INV_SRC_ALPHA;
            blend.BlendOp = D3D11_BLEND_OP_ADD;
            blend.SrcBlendAlpha = D3D11_BLEND_ONE;
            blend.DestBlendAlpha = D3D11_BLEND_INV_SRC_ALPHA;
            blend.BlendOpAlpha = D3D11_BLEND_OP_ADD;
            blend.RenderTargetWriteMask = D3D11_COLOR_WRITE_ENABLE_ALL;
            hr = device->CreateBlendState(&blendDesc, premultipliedBlendState.ReleaseAndGetAddressOf());
            if (FAILED(hr)) return Fail(L"create-blend-state",hr);
            auto createConstantBuffer = [&](UINT byteWidth, ComPtr<ID3D11Buffer>& buffer) -> HRESULT
            {
                D3D11_BUFFER_DESC bufferDesc{};
                bufferDesc.ByteWidth = byteWidth;
                bufferDesc.Usage = D3D11_USAGE_DYNAMIC;
                bufferDesc.BindFlags = D3D11_BIND_CONSTANT_BUFFER;
                bufferDesc.CPUAccessFlags = D3D11_CPU_ACCESS_WRITE;
                return device->CreateBuffer(&bufferDesc, nullptr, buffer.ReleaseAndGetAddressOf());
            };
            hr = createConstantBuffer(static_cast<UINT>(sizeof(PanelParamsV3)), paramsBuffer);
            if (FAILED(hr)) return Fail(L"create-cb-panel",hr);
            hr = createConstantBuffer(static_cast<UINT>(sizeof(CropParams)), cropParamsBuffer);
            if (FAILED(hr)) return Fail(L"create-cb-crop",hr);
            hr = createConstantBuffer(static_cast<UINT>(sizeof(MetricsParams)), metricsParamsBuffer);
            if (FAILED(hr)) return Fail(L"create-cb-metrics",hr);
            hr = createConstantBuffer(static_cast<UINT>(sizeof(LayerParamsV1)), layerParamsBuffer);
            if (FAILED(hr)) return Fail(L"create-cb-layer",hr);
            hr = createConstantBuffer(static_cast<UINT>(sizeof(GaussianParams)), gaussianParamsBuffer);
            if (FAILED(hr)) return Fail(L"create-cb-gaussian",hr);
            hr = createConstantBuffer(static_cast<UINT>(sizeof(PresentParams)), presentParamsBuffer);
            if (FAILED(hr)) return Fail(L"create-cb-present",hr);
            ComPtr<ID3DBlob> vsBlob, psBlob, errorBlob;
            hr = D3DCompile(kVertexShader, std::strlen(kVertexShader), "LiquidGlassVS", nullptr, nullptr, "VS",
                            "vs_5_0", D3DCOMPILE_OPTIMIZATION_LEVEL3, 0, vsBlob.ReleaseAndGetAddressOf(),
                            errorBlob.ReleaseAndGetAddressOf());
            if (FAILED(hr)) return Fail(L"compile-vs",hr,errorBlob?errorBlob->GetBufferPointer():nullptr,errorBlob?errorBlob->GetBufferSize():0);
            hr = device->CreateVertexShader(vsBlob->GetBufferPointer(), vsBlob->GetBufferSize(), nullptr,
                                            vertexShader.ReleaseAndGetAddressOf());
            if (FAILED(hr)) return Fail(L"create-vs",hr);
            auto createPixelShader = [&](wchar_t const* step,char const* source,char const* sourceName,char const* entry,
                                         ComPtr<ID3D11PixelShader>& shader) -> HRESULT
            {
                ComPtr<ID3DBlob> bytecode;
                ComPtr<ID3DBlob> errors;
                HRESULT compileResult = D3DCompile(source, std::strlen(source), sourceName, nullptr, nullptr, entry,
                    "ps_5_0", D3DCOMPILE_OPTIMIZATION_LEVEL3, 0, bytecode.ReleaseAndGetAddressOf(),
                    errors.ReleaseAndGetAddressOf());
                if (FAILED(compileResult)) return Fail(step,compileResult,errors?errors->GetBufferPointer():nullptr,errors?errors->GetBufferSize():0);
                HRESULT createResult=device->CreatePixelShader(bytecode->GetBufferPointer(),bytecode->GetBufferSize(),nullptr,
                    shader.ReleaseAndGetAddressOf());
                if(FAILED(createResult))return Fail(step,createResult);
                return S_OK;
            };
            hr = createPixelShader(L"compile-ps-crop",kCaptureShader,"LiquidGlassCropPS","CropPS",capturePixelShader);
            if (FAILED(hr)) return hr;
            hr = createPixelShader(L"compile-ps-metrics",kMetricsShader,"LiquidGlassMetricsPS","MetricsPS",metricsPixelShader);
            if (FAILED(hr)) return hr;
            hr = createPixelShader(L"compile-ps-panel",kLiquidGlassPanelShader,"GlassDesk.LiquidGlass.0.3.PanelPS","GlassPS",pixelShader);
            if (FAILED(hr)) return hr;
            hr = createPixelShader(L"compile-ps-layer",kLayerShader,"GlassDesk.LiquidGlass.0.3.LayerPS","LayerPS",layerPixelShader);
            if (FAILED(hr)) return hr;
            hr = createPixelShader(L"compile-ps-gaussian",kGaussianShader,"GlassDesk.LiquidGlass.0.3.GaussianPS","GaussianPS",gaussianPixelShader);
            if (FAILED(hr)) return hr;
            hr = createPixelShader(L"compile-ps-present",kPresentShader,"GlassDesk.LiquidGlass.0.3.PresentPS","PresentPS",presentPixelShader);
            if (FAILED(hr)) return hr;
            hr = createPixelShader(L"compile-ps-empty-refraction",kEmptyCompatibilityShader,"LiquidGlassEmptyRefractionPS","EmptyPS",refractionPixelShader);
            if (FAILED(hr)) return hr;
            hr=createPixelShader(L"compile-ps-empty-tint",kEmptyCompatibilityShader,"LiquidGlassEmptyTintPS","EmptyPS",tintPixelShader);
            if(FAILED(hr))return hr;
            diagnostics::Log(L"shader-ready",L"stage=compile backend=WGC shaderVersion=0.3 activeShaderVersion=0.3 shaderActive=1 fallback=0 renderer=LiquidGlassCapture");
            return S_OK;
        }
        catch (...)
        {
            return Fail(L"ensure-device-exception",winrt::to_hresult());
        }
    }

    void StopSession() noexcept
    {
        try
        {
            if (session) session.Close();
            if (retainedFrame) retainedFrame.Close();
        }
        catch (...)
        {
        }
        session = nullptr;
        retainedFrame = nullptr;
        framePool = nullptr;
        captureItem = nullptr;
        sessionActive = false;
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
            if (hr==E_PENDING)return hr;
            if (FAILED(hr))return Fail(L"borderless-access",hr);
            if(hr!=S_OK)return hr;
            const HMONITOR monitor = MonitorFromWindow(widget, MONITOR_DEFAULTTONEAREST);
            MONITORINFO info{};
            info.cbSize = sizeof(info);
            if (!GetMonitorInfoW(monitor, &info)) return Fail(L"monitor-info",HRESULT_FROM_WIN32(GetLastError()));
            // get_activation_factory<T, IGraphicsCaptureItemInterop>() returns
            // the interop interface directly; CreateForMonitor is its method.
            auto interop = winrt::get_activation_factory<GraphicsCaptureItem, IGraphicsCaptureItemInterop>();
            winrt::Windows::Graphics::Capture::GraphicsCaptureItem item{nullptr};
            hr = interop->CreateForMonitor(monitor, winrt::guid_of<GraphicsCaptureItem>(),
                                           reinterpret_cast<void**>(winrt::put_abi(item)));
            if (FAILED(hr)) return Fail(L"capture-item-create",hr);
            captureItem = item;
            const auto captureSize = captureItem.Size();
            const winrt::Windows::Graphics::SizeInt32 size{
                std::max(1, captureSize.Width), std::max(1, captureSize.Height)};
            framePool = Direct3D11CaptureFramePool::Create(winrtDevice, DirectXPixelFormat::B8G8R8A8UIntNormalized, 2, size);
            session = framePool.CreateCaptureSession(captureItem);
            hr = ConfigureWindowExclusions();
            if (FAILED(hr)) return Fail(L"window-exclusions",hr);
            session.IsBorderRequired(false);
            session.IsCursorCaptureEnabled(false);
            session.StartCapture();
            sessionActive = true;
            captureMonitor = monitor;
            ++sourceGeneration;
            lastSourceFormat=DXGI_FORMAT_UNKNOWN;
            lastSourceWidth=lastSourceHeight=lastContentWidth=lastContentHeight=0;
            waitingFrameLogged = false;
            activeFrameLogged = false;
            wchar_t fields[256]{};
            swprintf_s(fields,std::size(fields),L"stage=start-session backend=WGC captureCandidate=1 capturedPresented=0 materialState=waiting fallbackReason=first-frame-pending captureGeneration=%llu geometrySequence=%llu dpi=%.3f",
                static_cast<unsigned long long>(sourceGeneration),static_cast<unsigned long long>(rect.sequence),rect.dpiScale);
            diagnostics::Log(L"capture-session",fields);
            return S_OK;
        }
        catch (...)
        {
            return Fail(L"start-session-exception",winrt::to_hresult());
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
            if(chain)
            {
                DXGI_SWAP_CHAIN_DESC1 existing{};
                hr=chain->GetDesc1(&existing);
                if(FAILED(hr))return hr;
                if(existing.Width!=width||existing.Height!=height)
                {
                    // A spare chain can still be referenced by the currently
                    // attached Composition surface after a previous attach
                    // retry. Replace its local reference instead of resizing
                    // the displayed chain in place; the old frame stays valid
                    // until the host atomically attaches the replacement.
                    renderTarget.Reset();
                    chain.Reset();
                }
            }
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

    UINT MaxTextureDimension() const noexcept
    {
        const D3D_FEATURE_LEVEL feature=device?device->GetFeatureLevel():D3D_FEATURE_LEVEL_11_0;
        UINT limit=4096;
        if(feature>=D3D_FEATURE_LEVEL_11_0)limit=16384;
        else if(feature>=D3D_FEATURE_LEVEL_10_0)limit=8192;
        else if(feature>=D3D_FEATURE_LEVEL_9_3)limit=4096;
        else if(feature>=D3D_FEATURE_LEVEL_9_2)limit=2048;
        else limit=2048;
        return limit;
    }

    UINT MaxActiveDimension() const noexcept
    {return std::min<UINT>(MaxTextureDimension(),4096u);}

    static UINT BucketCapacity(UINT current,UINT required) noexcept
    {
        const uint64_t alignedRequired=(static_cast<uint64_t>(required)+63ull)&~63ull;
        const uint64_t grown=current==0?0ull:((static_cast<uint64_t>(current)*3ull+1ull)/2ull+63ull)&~63ull;
        const uint64_t chosen=std::max(alignedRequired,grown);
        return chosen>std::numeric_limits<UINT>::max()?0u:static_cast<UINT>(chosen);
    }

    static bool GraphHasCapacity(OutputBundle const& bundle,UINT width,UINT height) noexcept
    {
        return bundle.graph.capacityWidth>=width&&bundle.graph.capacityHeight>=height&&
            bundle.graph.scene[0].texture&&bundle.graph.scene[1].texture&&
            bundle.graph.blur[0].texture&&bundle.graph.blur[1].texture&&bundle.graph.blur[2].texture&&
            bundle.graph.blurScratch.texture;
    }

    static uint64_t GraphMemoryBytes(OutputBundle::RenderGraph const& graph) noexcept
    {
        const uint64_t full=static_cast<uint64_t>(graph.capacityWidth)*graph.capacityHeight*8ull;
        const uint64_t half=static_cast<uint64_t>((graph.capacityWidth+1u)/2u)*((graph.capacityHeight+1u)/2u)*8ull;
        uint64_t bytes{};
        for(auto const& scene:graph.scene)if(scene.texture)bytes+=full;
        for(auto const& blur:graph.blur)if(blur.texture)bytes+=half;
        if(graph.blurScratch.texture)bytes+=half;
        return bytes;
    }

    static uint64_t SwapChainMemoryBytes(ComPtr<IDXGISwapChain1> const& chain) noexcept
    {
        if(!chain)return 0;
        DXGI_SWAP_CHAIN_DESC1 desc{};
        if(FAILED(chain->GetDesc1(&desc)))return 0;
        return static_cast<uint64_t>(desc.Width)*desc.Height*desc.BufferCount*4ull;
    }

    static uint64_t BundleOutputMemoryBytes(OutputBundle const& bundle) noexcept
    {
        return SwapChainMemoryBytes(bundle.output)+SwapChainMemoryBytes(bundle.refraction)+SwapChainMemoryBytes(bundle.tint);
    }

    uint64_t PoolMemoryBytes() const noexcept
    {
        uint64_t bytes=GraphMemoryBytes(outputs.graph)+GraphMemoryBytes(spareOutputs.graph)+
            BundleOutputMemoryBytes(outputs)+BundleOutputMemoryBytes(spareOutputs)+
            SwapChainMemoryBytes(inkColorSwapChain);
        if(captureTexture)bytes+=static_cast<uint64_t>(captureCapacityWidth)*captureCapacityHeight*8ull;
        for(auto const& texture:metricsTextures)if(texture)bytes+=16ull;
        return bytes;
    }

    HRESULT CreateGraphTexture(OutputBundle::RenderTexture& target,UINT width,UINT height) noexcept
    {
        D3D11_TEXTURE2D_DESC desc{};
        desc.Width=width;desc.Height=height;desc.MipLevels=1;desc.ArraySize=1;
        desc.Format=DXGI_FORMAT_R16G16B16A16_FLOAT;desc.SampleDesc.Count=1;
        desc.Usage=D3D11_USAGE_DEFAULT;desc.BindFlags=D3D11_BIND_RENDER_TARGET|D3D11_BIND_SHADER_RESOURCE;
        HRESULT hr=device->CreateTexture2D(&desc,nullptr,target.texture.ReleaseAndGetAddressOf());
        if(FAILED(hr))return hr;
        hr=device->CreateRenderTargetView(target.texture.Get(),nullptr,target.rtv.ReleaseAndGetAddressOf());
        if(FAILED(hr))return hr;
        return device->CreateShaderResourceView(target.texture.Get(),nullptr,target.srv.ReleaseAndGetAddressOf());
    }

    HRESULT EnsureGraphResources(OutputBundle& bundle,UINT activeWidth,UINT activeHeight) noexcept
    {
        try
        {
            if(GraphHasCapacity(bundle,activeWidth,activeHeight))return S_OK;
            const UINT maximum=MaxTextureDimension();
            const UINT width=BucketCapacity(bundle.graph.capacityWidth,activeWidth);
            const UINT height=BucketCapacity(bundle.graph.capacityHeight,activeHeight);
            if(width==0||height==0||width>maximum||height>maximum)return Fail(L"graph-capacity-bounds",E_BOUNDS);
            UINT formatSupport{};
            HRESULT hr=device->CheckFormatSupport(DXGI_FORMAT_R16G16B16A16_FLOAT,&formatSupport);
            if(FAILED(hr))return Fail(L"graph-format-support",hr);
            const UINT requiredSupport=D3D11_FORMAT_SUPPORT_RENDER_TARGET|D3D11_FORMAT_SUPPORT_SHADER_SAMPLE;
            if((formatSupport&requiredSupport)!=requiredSupport)return Fail(L"graph-fp16-unsupported",DXGI_ERROR_UNSUPPORTED);
            const uint64_t fullPixels=static_cast<uint64_t>(width)*height;
            const uint64_t halfPixels=static_cast<uint64_t>((width+1u)/2u)*((height+1u)/2u);
            const uint64_t graphBytes=(fullPixels*2ull+halfPixels*4ull)*8ull;
            const uint64_t outputBytes=fullPixels*8ull;
            const uint64_t oldCaptureBytes=captureTexture?
                static_cast<uint64_t>(captureCapacityWidth)*captureCapacityHeight*8ull:0ull;
            const uint64_t replacedBytes=GraphMemoryBytes(bundle.graph)+BundleOutputMemoryBytes(bundle)+oldCaptureBytes;
            const uint64_t currentPool=PoolMemoryBytes();
            const uint64_t retainedPool=currentPool>replacedBytes?currentPool-replacedBytes:0ull;
            const uint64_t estimatedPool=retainedPool+graphBytes+outputBytes+fullPixels*8ull;
            constexpr uint64_t hardBudget=192ull*1024ull*1024ull;
            if(estimatedPool>hardBudget)return Fail(L"graph-budget",E_OUTOFMEMORY);

            OutputBundle::RenderGraph graph{};
            const UINT halfWidth=(width+1u)/2u,halfHeight=(height+1u)/2u;
            for(auto& scene:graph.scene){hr=CreateGraphTexture(scene,width,height);if(FAILED(hr))return Fail(L"graph-create-texture",hr);}
            for(auto& blur:graph.blur){hr=CreateGraphTexture(blur,halfWidth,halfHeight);if(FAILED(hr))return Fail(L"graph-create-texture",hr);}
            hr=CreateGraphTexture(graph.blurScratch,halfWidth,halfHeight);if(FAILED(hr))return Fail(L"graph-create-texture",hr);
            graph.capacityWidth=width;graph.capacityHeight=height;
            bundle.graph=std::move(graph);
            const wchar_t* budget=estimatedPool>128ull*1024ull*1024ull?L"soft-exceeded":L"within-soft";
            wchar_t fields[256]{};
            swprintf_s(fields,std::size(fields),L"stage=graph-create activePx=%ux%u capacityPx=%ux%u graphBytes=%llu outputBytes=%llu poolBytes=%llu softBudget=134217728 hardBudget=201326592 budget=%ls geometrySequence=%llu activeShaderVersion=0.3",
                activeWidth,activeHeight,width,height,static_cast<unsigned long long>(graphBytes),
                static_cast<unsigned long long>(outputBytes),static_cast<unsigned long long>(estimatedPool),budget,
                static_cast<unsigned long long>(sceneSequence));
            diagnostics::Log(L"render-graph",fields);
            return S_OK;
        }
        catch(...){return Fail(L"ensure-graph-exception",winrt::to_hresult());}
    }

    HRESULT EnsureCaptureTexture(UINT width,UINT height) noexcept
    {
        if(captureTexture&&captureCapacityWidth==width&&captureCapacityHeight==height&&captureRtv&&captureSrv)return S_OK;
        D3D11_TEXTURE2D_DESC desc{};
        desc.Width=width;desc.Height=height;desc.MipLevels=1;desc.ArraySize=1;
        desc.Format=DXGI_FORMAT_R16G16B16A16_FLOAT;desc.SampleDesc.Count=1;
        desc.Usage=D3D11_USAGE_DEFAULT;desc.BindFlags=D3D11_BIND_RENDER_TARGET|D3D11_BIND_SHADER_RESOURCE;
        ComPtr<ID3D11Texture2D> texture;
        HRESULT hr=device->CreateTexture2D(&desc,nullptr,texture.ReleaseAndGetAddressOf());
        if(FAILED(hr))return hr;
        ComPtr<ID3D11RenderTargetView> rtv;
        hr=device->CreateRenderTargetView(texture.Get(),nullptr,rtv.ReleaseAndGetAddressOf());
        if(FAILED(hr))return hr;
        ComPtr<ID3D11ShaderResourceView> srv;
        hr=device->CreateShaderResourceView(texture.Get(),nullptr,srv.ReleaseAndGetAddressOf());
        if(FAILED(hr))return hr;
        captureTexture=std::move(texture);captureRtv=std::move(rtv);captureSrv=std::move(srv);
        captureSrvTexture=captureTexture.Get();captureCapacityWidth=width;captureCapacityHeight=height;
        return S_OK;
    }

    HRESULT UploadConstantBuffer(ComPtr<ID3D11Buffer> const& buffer,void const* value,size_t bytes) noexcept
    {
        D3D11_MAPPED_SUBRESOURCE mapped{};
        HRESULT hr=context->Map(buffer.Get(),0,D3D11_MAP_WRITE_DISCARD,0,&mapped);
        if(FAILED(hr))return hr;
        std::memcpy(mapped.pData,value,bytes);
        context->Unmap(buffer.Get(),0);
        return S_OK;
    }

    HRESULT DrawGaussian(ID3D11ShaderResourceView* source,UINT inputCapacityWidth,UINT inputCapacityHeight,
        UINT inputActiveWidth,UINT inputActiveHeight,OutputBundle::RenderGraph& graph,
        OutputBundle::RenderTexture& destination,float sigmaPixels) noexcept
    {
        const UINT outputActiveWidth=(inputActiveWidth+1u)/2u,outputActiveHeight=(inputActiveHeight+1u)/2u;
        const UINT outputCapacityWidth=(graph.capacityWidth+1u)/2u,outputCapacityHeight=(graph.capacityHeight+1u)/2u;
        auto draw=[&](ID3D11ShaderResourceView* input,ID3D11RenderTargetView* target,
            UINT inputCapW,UINT inputCapH,UINT inputActW,UINT inputActH,
            UINT outputCapW,UINT outputCapH,UINT outputActW,UINT outputActH,
            float sigma,float axis,float halfResolution)->HRESULT
        {
            GaussianParams params{};
            params.inputCapacityWidth=static_cast<float>(inputCapW);params.inputCapacityHeight=static_cast<float>(inputCapH);
            params.inputActiveWidth=static_cast<float>(inputActW);params.inputActiveHeight=static_cast<float>(inputActH);
            params.outputCapacityWidth=static_cast<float>(outputCapW);params.outputCapacityHeight=static_cast<float>(outputCapH);
            params.outputActiveWidth=static_cast<float>(outputActW);params.outputActiveHeight=static_cast<float>(outputActH);
            params.sigmaPixels=sigma;params.axis=axis;params.halfResolution=halfResolution;
            HRESULT result=UploadConstantBuffer(gaussianParamsBuffer,&params,sizeof(params));
            if(FAILED(result))return result;
            ID3D11RenderTargetView* renderTarget=target;
            context->OMSetRenderTargets(1,&renderTarget,nullptr);
            D3D11_VIEWPORT viewport{};viewport.Width=static_cast<float>(outputActW);viewport.Height=static_cast<float>(outputActH);viewport.MaxDepth=1.0f;
            context->RSSetViewports(1,&viewport);context->RSSetState(rasterizerState.Get());
            context->OMSetBlendState(nullptr,nullptr,0xffffffff);context->OMSetDepthStencilState(nullptr,0);
            context->VSSetShader(vertexShader.Get(),nullptr,0);context->PSSetShader(gaussianPixelShader.Get(),nullptr,0);
            context->IASetInputLayout(nullptr);context->IASetPrimitiveTopology(D3D11_PRIMITIVE_TOPOLOGY_TRIANGLELIST);
            context->GSSetShader(nullptr,nullptr,0);context->HSSetShader(nullptr,nullptr,0);context->DSSetShader(nullptr,nullptr,0);context->CSSetShader(nullptr,nullptr,0);
            ID3D11ShaderResourceView* inputView=input;context->PSSetShaderResources(0,1,&inputView);
            ID3D11SamplerState* samplerState=sampler.Get();context->PSSetSamplers(0,1,&samplerState);
            ID3D11Buffer* constants=gaussianParamsBuffer.Get();context->PSSetConstantBuffers(0,1,&constants);
            context->Draw(3,0);
            ID3D11ShaderResourceView* nullView=nullptr;context->PSSetShaderResources(0,1,&nullView);context->OMSetRenderTargets(0,nullptr,nullptr);
            return S_OK;
        };
        HRESULT hr=draw(source,graph.blurScratch.rtv.Get(),inputCapacityWidth,inputCapacityHeight,
            inputActiveWidth,inputActiveHeight,outputCapacityWidth,outputCapacityHeight,
            outputActiveWidth,outputActiveHeight,sigmaPixels,0.0f,1.0f);
        if(FAILED(hr))return hr;
        return draw(graph.blurScratch.srv.Get(),destination.rtv.Get(),outputCapacityWidth,outputCapacityHeight,
            outputActiveWidth,outputActiveHeight,outputCapacityWidth,outputCapacityHeight,
            outputActiveWidth,outputActiveHeight,sigmaPixels*0.5f,1.0f,0.0f);
    }

    HRESULT DrawPanel(OutputBundle::RenderGraph& graph,UINT activeWidth,UINT activeHeight,
        LONG pad,CaptureRect const& rect,LiquidGlassMaterialSettings const& material,
        float darkWeight,float inkLight) noexcept
    {
        context->CopyResource(graph.scene[0].texture.Get(),captureTexture.Get());
        const float sigmaDip=material.blur;
        HRESULT hr=DrawGaussian(captureSrv.Get(),captureCapacityWidth,captureCapacityHeight,
            activeWidth,activeHeight,graph,graph.blur[0],sigmaDip*rect.dpiScale);
        if(FAILED(hr))return hr;
        PanelParamsV3 params{};
        params.widgetOriginX=static_cast<float>(pad);params.widgetOriginY=static_cast<float>(pad);
        params.widgetWidth=rect.shapeWidthPixels;params.widgetHeight=rect.shapeHeightPixels;
        params.captureCapacityWidth=static_cast<float>(captureCapacityWidth);params.captureCapacityHeight=static_cast<float>(captureCapacityHeight);
        params.captureActiveWidth=static_cast<float>(activeWidth);params.captureActiveHeight=static_cast<float>(activeHeight);
        params.radiusPixels=rect.radiusPixels;params.rimMaxDip=std::min(56.0f,materialSettings.rimMaxPixels);
        params.surfaceTension=rect.surfaceTension;params.paddingPixels=static_cast<float>(pad);
        params.dpiScale=rect.dpiScale;params.lensEnabled=materialSettings.lensEnabled?1.0f:0.0f;
        params.dispersion=materialSettings.dispersion*(1.0f-darkWeight/3.0f);params.opacity=material.opacity;
        params.saturation=material.saturation;params.brightness=material.brightness;params.contrast=material.contrast;
        params.tintScale=material.opacity;
        params.tintR=static_cast<float>((material.tintArgb>>16)&0xffu)/255.0f;
        params.tintG=static_cast<float>((material.tintArgb>>8)&0xffu)/255.0f;
        params.tintB=static_cast<float>(material.tintArgb&0xffu)/255.0f;
        params.blurCapacityWidth=static_cast<float>((graph.capacityWidth+1u)/2u);
        params.blurCapacityHeight=static_cast<float>((graph.capacityHeight+1u)/2u);
        params.blurActiveWidth=static_cast<float>((activeWidth+1u)/2u);
        params.blurActiveHeight=static_cast<float>((activeHeight+1u)/2u);
        params.keyGain=0.30f+(0.26f-0.30f)*darkWeight;
        params.counterGain=0.10f+(0.09f-0.10f)*darkWeight;
        params.occlusionAlpha=0.018f+(0.012f-0.018f)*darkWeight;
        params.darkThemeWeight=darkWeight;
        hr=UploadConstantBuffer(paramsBuffer,&params,sizeof(params));
        if(FAILED(hr))return hr;
        ID3D11RenderTargetView* target=graph.scene[0].rtv.Get();context->OMSetRenderTargets(1,&target,nullptr);
        D3D11_VIEWPORT viewport{};viewport.Width=static_cast<float>(activeWidth);viewport.Height=static_cast<float>(activeHeight);viewport.MaxDepth=1.0f;
        context->RSSetViewports(1,&viewport);context->RSSetState(rasterizerState.Get());
        context->OMSetBlendState(premultipliedBlendState.Get(),nullptr,0xffffffff);context->OMSetDepthStencilState(nullptr,0);
        context->VSSetShader(vertexShader.Get(),nullptr,0);context->PSSetShader(pixelShader.Get(),nullptr,0);
        context->IASetInputLayout(nullptr);context->IASetPrimitiveTopology(D3D11_PRIMITIVE_TOPOLOGY_TRIANGLELIST);
        context->GSSetShader(nullptr,nullptr,0);context->HSSetShader(nullptr,nullptr,0);context->DSSetShader(nullptr,nullptr,0);context->CSSetShader(nullptr,nullptr,0);
        ID3D11ShaderResourceView* inputs[3]{captureSrv.Get(),metricsSrvs[metricsIndex].Get(),graph.blur[0].srv.Get()};
        context->PSSetShaderResources(0,3,inputs);ID3D11SamplerState* samplerState=sampler.Get();context->PSSetSamplers(0,1,&samplerState);
        ID3D11Buffer* constants=paramsBuffer.Get();context->PSSetConstantBuffers(0,1,&constants);context->Draw(3,0);
        ID3D11ShaderResourceView* nullViews[3]{};context->PSSetShaderResources(0,3,nullViews);context->OMSetRenderTargets(0,nullptr,nullptr);
        (void)inkLight;
        return S_OK;
    }

    HRESULT DrawShapeLayer(OutputBundle::RenderGraph& graph,UINT activeWidth,UINT activeHeight,
        LONG pad,CaptureRect const& rect,LiquidGlassMaterialSettings const& material,
        float darkWeight,uint32_t layer,OutputBundle::RenderTexture& lower,
        OutputBundle::RenderTexture& targetTexture,uint32_t& submittedCount) noexcept
    {
        submittedCount=0;
        context->CopyResource(targetTexture.texture.Get(),lower.texture.Get());
        const float sigmaDip=layer==2u?6.0f+(3.8f-6.0f)*darkWeight:2.6f+(1.6f-2.6f)*darkWeight;
        HRESULT hr=DrawGaussian(lower.srv.Get(),graph.capacityWidth,graph.capacityHeight,
            activeWidth,activeHeight,graph,graph.blur[layer-1u],sigmaDip*rect.dpiScale);
        if(FAILED(hr))return hr;
        ID3D11RenderTargetView* layerTarget=targetTexture.rtv.Get();
        if(!layerTarget)return Fail(L"layer-target-null",E_POINTER);
        context->OMSetRenderTargets(1,&layerTarget,nullptr);
        D3D11_VIEWPORT viewport{};viewport.Width=static_cast<float>(activeWidth);viewport.Height=static_cast<float>(activeHeight);viewport.MaxDepth=1.0f;
        context->RSSetViewports(1,&viewport);context->RSSetState(scissorRasterizerState.Get());
        context->OMSetBlendState(premultipliedBlendState.Get(),nullptr,0xffffffff);context->OMSetDepthStencilState(nullptr,0);
        context->VSSetShader(vertexShader.Get(),nullptr,0);context->PSSetShader(layerPixelShader.Get(),nullptr,0);
        context->IASetInputLayout(nullptr);context->IASetPrimitiveTopology(D3D11_PRIMITIVE_TOPOLOGY_TRIANGLELIST);
        context->GSSetShader(nullptr,nullptr,0);context->HSSetShader(nullptr,nullptr,0);context->DSSetShader(nullptr,nullptr,0);context->CSSetShader(nullptr,nullptr,0);
        ID3D11ShaderResourceView* inputs[2]{lower.srv.Get(),graph.blur[layer-1u].srv.Get()};
        context->PSSetShaderResources(0,2,inputs);ID3D11SamplerState* samplerState=sampler.Get();context->PSSetSamplers(0,1,&samplerState);
        ID3D11Buffer* constants=layerParamsBuffer.Get();context->PSSetConstantBuffers(0,1,&constants);
        const float dpi=rect.dpiScale>0.0f?rect.dpiScale:1.0f;
        const float layerGain=layer==2u?0.8f:0.65f;
        const float baseKey=0.30f*layerGain;
        const float baseCounter=0.10f*layerGain;
        const float key=baseKey+(baseKey*0.8666667f-baseKey)*darkWeight;
        const float counter=baseCounter+(baseCounter*0.9f-baseCounter)*darkWeight;
        const float occlusion=(layer==2u?0.0144f:0.0117f)+((layer==2u?0.0096f:0.0078f)-(layer==2u?0.0144f:0.0117f))*darkWeight;
        const float bandDip=layer==2u?28.0f:14.0f;
        const float tintAlpha=(layer==2u?0.08f:0.035f)+((layer==2u?0.035f:0.018f)-(layer==2u?0.08f:0.035f))*darkWeight;
        for(GDGlassShapeV1 const& shape:sceneShapes)
        {
            if(shape.layer!=layer||shape.enabled==0||shape.materialize<=0.0f)continue;
            const float x=static_cast<float>(pad)+shape.xDip*dpi;
            const float y=static_cast<float>(pad)+shape.yDip*dpi;
            const float width=shape.widthDip*dpi,height=shape.heightDip*dpi;
            const float radius=shape.radiusDip*dpi;
            const float shortDip=std::min(shape.widthDip,shape.heightDip);
            const float longDip=std::max(shape.widthDip,shape.heightDip);
            const bool smallCapsule=shortDip>0.0f&&longDip>=shortDip*1.6f&&shape.radiusDip>=shortDip*0.35f;
            const float shapeBandDip=smallCapsule?12.0f:bandDip;
            const float shapeDisplacementDip=smallCapsule?6.0f:(layer==2u?12.0f:7.0f);
            const float margin=(layer==2u?30.0f:16.0f)*dpi+2.0f;
            const LONG left=std::max<LONG>(0,static_cast<LONG>(std::floor(x-margin)));
            const LONG top=std::max<LONG>(0,static_cast<LONG>(std::floor(y-margin)));
            const LONG right=std::min<LONG>(static_cast<LONG>(activeWidth),static_cast<LONG>(std::ceil(x+width+margin)));
            const LONG bottom=std::min<LONG>(static_cast<LONG>(activeHeight),static_cast<LONG>(std::ceil(y+height+margin)));
            if(right<=left||bottom<=top)continue;
            D3D11_RECT scissor{left,top,right,bottom};context->RSSetScissorRects(1,&scissor);
            LayerParamsV1 params{};
            params.shapeX=x;params.shapeY=y;params.shapeWidth=width;params.shapeHeight=height;
            params.captureCapacityWidth=static_cast<float>(graph.capacityWidth);params.captureCapacityHeight=static_cast<float>(graph.capacityHeight);
            params.captureActiveWidth=static_cast<float>(activeWidth);params.captureActiveHeight=static_cast<float>(activeHeight);
            params.radiusPixels=radius;params.bandDip=shapeBandDip;params.displacementDip=shapeDisplacementDip;params.materialize=shape.materialize;
            params.dpiScale=dpi;params.dispersion=materialSettings.dispersion*(1.0f-darkWeight/3.0f);params.press=shape.press;params.surfaceTension=rect.surfaceTension;
            params.tintR=static_cast<float>((material.tintArgb>>16)&0xffu)/255.0f;
            params.tintG=static_cast<float>((material.tintArgb>>8)&0xffu)/255.0f;
            params.tintB=static_cast<float>(material.tintArgb&0xffu)/255.0f;
            params.tintAlpha=tintAlpha*std::clamp(material.opacity,0.0f,1.5f);
            params.blurCapacityWidth=static_cast<float>((graph.capacityWidth+1u)/2u);
            params.blurCapacityHeight=static_cast<float>((graph.capacityHeight+1u)/2u);
            params.blurActiveWidth=static_cast<float>((activeWidth+1u)/2u);params.blurActiveHeight=static_cast<float>((activeHeight+1u)/2u);
            params.saturation=material.saturation;params.brightness=material.brightness;params.contrast=material.contrast;params.darkThemeWeight=darkWeight;
            params.keyGain=key;params.counterGain=counter;params.occlusionAlpha=occlusion;params.layer=static_cast<float>(layer);
            hr=UploadConstantBuffer(layerParamsBuffer,&params,sizeof(params));
            if(FAILED(hr))break;
            context->Draw(3,0);
            ++submittedCount;
        }
        ID3D11ShaderResourceView* nullViews[2]{};context->PSSetShaderResources(0,2,nullViews);context->OMSetRenderTargets(0,nullptr,nullptr);
        context->RSSetState(rasterizerState.Get());
        return hr;
    }

    HRESULT PresentGraph(OutputBundle& frameOutputs,OutputBundle::RenderGraph const& graph,
        ID3D11ShaderResourceView* finalScene,UINT activeWidth,UINT activeHeight,CaptureRect const& rect,LONG pad) noexcept
    {
        PresentParams params{};
        params.sourceCapacityWidth=static_cast<float>(graph.capacityWidth);params.sourceCapacityHeight=static_cast<float>(graph.capacityHeight);
        params.sourceActiveWidth=static_cast<float>(activeWidth);params.sourceActiveHeight=static_cast<float>(activeHeight);
        params.apronX=static_cast<float>(pad);params.apronY=static_cast<float>(pad);
        params.outputWidth=static_cast<float>(rect.widthPixels);params.outputHeight=static_cast<float>(rect.heightPixels);
        HRESULT hr=UploadConstantBuffer(presentParamsBuffer,&params,sizeof(params));
        if(FAILED(hr))return hr;
        ID3D11RenderTargetView* target=frameOutputs.outputRtv.Get();context->OMSetRenderTargets(1,&target,nullptr);
        D3D11_VIEWPORT viewport{};viewport.Width=static_cast<float>(rect.widthPixels);viewport.Height=static_cast<float>(rect.heightPixels);viewport.MaxDepth=1.0f;
        context->RSSetViewports(1,&viewport);context->RSSetState(rasterizerState.Get());context->OMSetBlendState(nullptr,nullptr,0xffffffff);
        context->OMSetDepthStencilState(nullptr,0);context->VSSetShader(vertexShader.Get(),nullptr,0);
        context->PSSetShader(presentPixelShader.Get(),nullptr,0);context->IASetInputLayout(nullptr);
        context->IASetPrimitiveTopology(D3D11_PRIMITIVE_TOPOLOGY_TRIANGLELIST);
        ID3D11ShaderResourceView* input=finalScene;context->PSSetShaderResources(0,1,&input);
        ID3D11SamplerState* samplerState=sampler.Get();context->PSSetSamplers(0,1,&samplerState);
        ID3D11Buffer* constants=presentParamsBuffer.Get();context->PSSetConstantBuffers(0,1,&constants);
        context->Draw(3,0);
        ID3D11ShaderResourceView* nullView=nullptr;context->PSSetShaderResources(0,1,&nullView);context->OMSetRenderTargets(0,nullptr,nullptr);
        return frameOutputs.output->Present(0,0);
    }

    HRESULT RenderFrame(CaptureRect const& rect) noexcept
    {
        try
        {
            lastFailureStage = CaptureFailureStage::AcquireFrame;
            if (!framePool) return S_FALSE;
            auto nextFrame = framePool.TryGetNextFrame();
            const double now = static_cast<double>(GetTickCount64());
            const bool resting = !rect.motionActive && now - lastMotionMs >= 3000.0 && !themeTransitionActive;
            // WGC frame arrival is metadata, not pixel readback. Animated
            // backgrounds remain at full rate even when the glass is still.
            if (!nextFrame && resting && lastFrameMs != 0.0 && now - lastFrameMs < kStaticFrameIntervalMs)
                return S_OK;
            if (nextFrame)
            {
                if (retainedFrame) retainedFrame.Close();
                retainedFrame = std::move(nextFrame);
            }
            // Retain one pool frame so settings and theme transitions redraw
            // even when a completely static desktop produces no new WGC frame.
            auto frame = retainedFrame;
            if (!frame)
            {
                if(!waitingFrameLogged)
                {
                    wchar_t fields[256]{};
                    swprintf_s(fields,std::size(fields),L"stage=acquire-frame backend=WGC captureCandidate=1 capturedPresented=0 materialState=waiting fallbackReason=first-frame-pending captureGeneration=%llu geometrySequence=%llu dpi=%.3f",
                        static_cast<unsigned long long>(sourceGeneration),static_cast<unsigned long long>(rect.sequence),rect.dpiScale);
                    diagnostics::Log(L"capture-pending",fields);
                    waitingFrameLogged=true;
                }
                return S_FALSE;
            }
            auto surface = frame.Surface();
            if (!surface) return S_FALSE;
            lastFailureStage = CaptureFailureStage::ReadCaptureSurface;
            auto access = surface.as<::Windows::Graphics::DirectX::Direct3D11::IDirect3DDxgiInterfaceAccess>();
            ComPtr<IDXGISurface> dxgiSurface;
            HRESULT hr = access->GetInterface(IID_PPV_ARGS(&dxgiSurface));
            if (FAILED(hr)) return Fail(L"dxgi-surface-access",hr);
            ComPtr<ID3D11Texture2D> sourceTexture;
            hr = dxgiSurface.As(&sourceTexture);
            if (FAILED(hr)) return Fail(L"dxgi-surface-as-texture",hr);
            D3D11_TEXTURE2D_DESC sourceDesc{};
            sourceTexture->GetDesc(&sourceDesc);

            if(sourceDesc.Format!=DXGI_FORMAT_B8G8R8A8_UNORM)
            {
                lastFailureStage=CaptureFailureStage::ReadCaptureSurface;
                return Fail(L"unsupported-source-format",DXGI_ERROR_UNSUPPORTED);
            }

            const auto contentSize = frame.ContentSize();
            if (contentSize.Width <= 0 || contentSize.Height <= 0)
            {
                if(!waitingFrameLogged)
                {
                    wchar_t fields[256]{};
                    swprintf_s(fields,std::size(fields),L"stage=acquire-frame backend=WGC captureCandidate=1 capturedPresented=0 materialState=waiting fallbackReason=invalid-content-size captureGeneration=%llu geometrySequence=%llu dpi=%.3f",
                        static_cast<unsigned long long>(sourceGeneration),static_cast<unsigned long long>(rect.sequence),rect.dpiScale);
                    diagnostics::Log(L"capture-pending",fields);
                    waitingFrameLogged=true;
                }
                return S_FALSE;
            }
            if (static_cast<UINT>(contentSize.Width) > sourceDesc.Width ||
                static_cast<UINT>(contentSize.Height) > sourceDesc.Height)
            {
                sourceTexture.Reset();
                dxgiSurface.Reset();
                access = nullptr;
                surface = nullptr;
                frame.Close();
                retainedFrame = nullptr;
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
            RefreshSystemTheme(now);
            const LiquidGlassMaterialSettings material = ActiveMaterial(now);
            const float darkThemeWeight=ActiveDarkWeight(now);
            const float shapeWidth = rect.shapeWidthPixels > 0 ? rect.shapeWidthPixels : float(rect.widthPixels);
            const float shapeHeight = rect.shapeHeightPixels > 0 ? rect.shapeHeightPixels : float(rect.heightPixels);
            const bool hasLayer2=std::any_of(sceneShapes.begin(),sceneShapes.end(),[](GDGlassShapeV1 const& s){return s.layer==2&&s.enabled!=0&&s.materialize>0.0f;});
            const bool hasLayer3=std::any_of(sceneShapes.begin(),sceneShapes.end(),[](GDGlassShapeV1 const& s){return s.layer==3&&s.enabled!=0&&s.materialize>0.0f;});
            const float sigma2=6.0f+(3.8f-6.0f)*darkThemeWeight;
            const float sigma3=2.6f+(1.6f-2.6f)*darkThemeWeight;
            // CPU crop and all shader sampling share this conservative reach.
            // The recursive contribution is included only for active layers.
            const float reachDip=22.0f+0.45f+3.0f*material.blur+
                (hasLayer2?12.0f+0.45f+3.0f*sigma2:0.0f)+
                (hasLayer3?7.0f+0.45f+3.0f*sigma3:0.0f);
            const LONG pad=static_cast<LONG>(std::ceil(reachDip*dpiScale+2.0f));
            lastPaddingPixels=pad;
            const int64_t captureWidth64=static_cast<int64_t>(rect.widthPixels)+static_cast<int64_t>(pad)*2;
            const int64_t captureHeight64=static_cast<int64_t>(rect.heightPixels)+static_cast<int64_t>(pad)*2;
            lastFailureStage = CaptureFailureStage::ValidateCaptureGeometry;
            // The software WGC active-extent limit applies after adding the
            // optical apron too; otherwise an allowed panel could silently
            // allocate an oversized capture surface near the 4096px edge.
            if(captureWidth64<=0||captureHeight64<=0||captureWidth64>MaxActiveDimension()||
                captureHeight64>MaxActiveDimension()||captureWidth64>MaxTextureDimension()||
                captureHeight64>MaxTextureDimension())return Fail(L"capture-extent-bounds",E_BOUNDS);
            const UINT captureWidth=static_cast<UINT>(captureWidth64),captureHeight=static_cast<UINT>(captureHeight64);
            lastCaptureActiveWidth=captureWidth;lastCaptureActiveHeight=captureHeight;
            OutputBundle* frameOutputs=&outputs;
            bool replacingOutputs=!outputs.output||!GraphHasCapacity(outputs,captureWidth,captureHeight);
            DXGI_SWAP_CHAIN_DESC1 activeOutputDesc{};
            if(outputs.output)
            {
                hr=outputs.output->GetDesc1(&activeOutputDesc);if(FAILED(hr))return Fail(L"output-desc",hr);
                replacingOutputs=replacingOutputs||activeOutputDesc.Width<static_cast<UINT>(rect.widthPixels)||
                    activeOutputDesc.Height<static_cast<UINT>(rect.heightPixels);
            }
            if(replacingOutputs)frameOutputs=&spareOutputs;
            lastFailureStage = CaptureFailureStage::CreateCaptureTexture;
            hr=EnsureGraphResources(*frameOutputs,captureWidth,captureHeight);
            if(FAILED(hr))return Fail(L"ensure-graph",hr);
            lastCaptureCapacityWidth=frameOutputs->graph.capacityWidth;
            lastCaptureCapacityHeight=frameOutputs->graph.capacityHeight;
            hr=EnsureCaptureTexture(frameOutputs->graph.capacityWidth,frameOutputs->graph.capacityHeight);
            if(FAILED(hr))return Fail(L"ensure-capture-texture",hr);
            const HMONITOR monitor = captureMonitor != nullptr ? captureMonitor : MonitorFromWindow(widget, MONITOR_DEFAULTTONEAREST);
            MONITORINFO info{};
            info.cbSize = sizeof(info);
            lastFailureStage = CaptureFailureStage::ReadMonitorBounds;
            if (!GetMonitorInfoW(monitor, &info)) return Fail(L"monitor-info-frame",HRESULT_FROM_WIN32(GetLastError()));
            const LONG monitorWidth = info.rcMonitor.right - info.rcMonitor.left;
            const LONG monitorHeight = info.rcMonitor.bottom - info.rcMonitor.top;
            if (monitorWidth <= 0 || monitorHeight <= 0) return Fail(L"monitor-size-invalid",E_INVALIDARG);
            lastMonitorWidth=monitorWidth;lastMonitorHeight=monitorHeight;
            if (!frameSourceSrv || frameSourceSrvTexture != sourceTexture.Get())
            {
                frameSourceSrv.Reset();
                hr = device->CreateShaderResourceView(sourceTexture.Get(), nullptr, frameSourceSrv.ReleaseAndGetAddressOf());
                if (FAILED(hr)) return Fail(L"create-source-srv",hr);
                frameSourceSrvTexture = sourceTexture.Get();
            }
            const bool sourceMetadataChanged=lastSourceFormat!=sourceDesc.Format||lastSourceWidth!=sourceDesc.Width||
                lastSourceHeight!=sourceDesc.Height||lastContentWidth!=static_cast<UINT>(contentSize.Width)||
                lastContentHeight!=static_cast<UINT>(contentSize.Height);
            if(sourceMetadataChanged&&lastSourceFormat!=DXGI_FORMAT_UNKNOWN)++sourceGeneration;
            if(sourceMetadataChanged)
            {
                wchar_t fields[384]{};
                swprintf_s(fields,std::size(fields),L"stage=source-ready backend=WGC captureCandidate=1 capturedPresented=0 captureGeneration=%llu geometrySequence=%llu sourceFormat=B8G8R8A8_UNORM sourceSizePx=%ux%u contentSizePx=%ux%u dpi=%.3f alphaContract=opaque-sdr-linear-intermediate",
                    static_cast<unsigned long long>(sourceGeneration),static_cast<unsigned long long>(rect.sequence),
                    sourceDesc.Width,sourceDesc.Height,static_cast<UINT>(contentSize.Width),static_cast<UINT>(contentSize.Height),rect.dpiScale);
                diagnostics::Log(L"capture-source",fields);
                waitingFrameLogged=false;
                activeFrameLogged=false;
            }
            lastSourceFormat=sourceDesc.Format;lastSourceWidth=sourceDesc.Width;lastSourceHeight=sourceDesc.Height;
            lastContentWidth=static_cast<UINT>(contentSize.Width);lastContentHeight=static_cast<UINT>(contentSize.Height);
            CropParams crop{};
            crop.screenOriginX = rect.screenX - static_cast<float>(pad);
            crop.screenOriginY = rect.screenY - static_cast<float>(pad);
            crop.monitorOriginX = static_cast<float>(info.rcMonitor.left);
            crop.monitorOriginY = static_cast<float>(info.rcMonitor.top);
            crop.monitorWidth = static_cast<float>(monitorWidth);
            crop.monitorHeight = static_cast<float>(monitorHeight);
            crop.contentWidth = static_cast<float>(contentSize.Width);
            crop.contentHeight = static_cast<float>(contentSize.Height);
            crop.sourceTextureWidth = static_cast<float>(sourceDesc.Width);
            crop.sourceTextureHeight = static_cast<float>(sourceDesc.Height);
            lastFailureStage = CaptureFailureStage::CropCapture;
            D3D11_MAPPED_SUBRESOURCE mapped{};
            hr = context->Map(cropParamsBuffer.Get(), 0, D3D11_MAP_WRITE_DISCARD, 0, &mapped);
            if (FAILED(hr)) return Fail(L"map-crop-cb",hr);
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
                if (FAILED(hr)) return Fail(L"create-capture-srv",hr);
                captureSrvTexture = captureTexture.Get();
            }
            lastFailureStage = CaptureFailureStage::ResizeOutput;
            hr = EnsureCompositionSwapChain(frameOutputs->output, frameOutputs->outputRtv,
                frameOutputs->graph.capacityWidth, frameOutputs->graph.capacityHeight);
            if (FAILED(hr)) return Fail(L"ensure-output-chain",hr);
            hr = EnsureCompositionSwapChain(frameOutputs->refraction, frameOutputs->refractionRtv,
                1, 1);
            if (FAILED(hr)) return Fail(L"ensure-refraction-chain",hr);
            hr = EnsureCompositionSwapChain(frameOutputs->tint, frameOutputs->tintRtv,
                1, 1);
            if (FAILED(hr)) return Fail(L"ensure-tint-chain",hr);
            hr = EnsureCompositionSwapChain(inkColorSwapChain, inkColorRtv, 1, 1);
            if (FAILED(hr)) return Fail(L"ensure-ink-chain",hr);
            hr = EnsureMetricsTargets();
            if (FAILED(hr)) return Fail(L"ensure-metrics-targets",hr);

            lastFailureStage = CaptureFailureStage::PrepareShader;
            const float inkLight = ActiveInkLight(now);
            const float textureWidth=static_cast<float>(captureCapacityWidth);
            const float textureHeight=static_cast<float>(captureCapacityHeight);

            const UINT outputMetricIndex = metricsInitialized ? 1U - metricsIndex : 0U;
            const UINT previousMetricIndex = 1U - outputMetricIndex;
            MetricsParams metrics{};
            lastFailureStage = CaptureFailureStage::DrawMetrics;
            metrics.widgetOriginX = static_cast<float>(pad);
            metrics.widgetOriginY = static_cast<float>(pad);
            metrics.widgetWidth = shapeWidth;
            metrics.widgetHeight = shapeHeight;
            metrics.captureWidth = textureWidth;
            metrics.captureHeight = textureHeight;
            const double metricsNow = now;
            const double elapsedMs = previousMetricsMs > 0.0 ? std::max(1.0, metricsNow - previousMetricsMs) : kFrameIntervalMs;
            metrics.smoothing = static_cast<float>(1.0 - std::exp(-elapsedMs / kDensitySmoothingMs));
            metrics.initialized = metricsInitialized ? 1.0f : 0.0f;
            metrics.autoDensity = materialSettings.autoDensity != 0 ? 1.0f : 0.0f;
            metrics.manualDensity = materialSettings.density;
            metrics.threshold = materialSettings.threshold;
            metrics.inkLight = inkLight;
            metrics.paddingX = darkThemeWeight;
            hr = context->Map(metricsParamsBuffer.Get(), 0, D3D11_MAP_WRITE_DISCARD, 0, &mapped);
            if (FAILED(hr)) return Fail(L"map-metrics-cb",hr);
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
            lastFailureStage = CaptureFailureStage::PresentFrame;
            hr = inkColorSwapChain->Present(0, 0);
            if (FAILED(hr)) return Fail(L"present-ink",hr);
            metricsIndex = outputMetricIndex;
            metricsInitialized = true;
            previousMetricsMs = metricsNow;
            inkIsDark = inkLight < 0.5f;

            lastFailureStage = CaptureFailureStage::PresentFrame;
            auto& graph=frameOutputs->graph;
            hr=DrawPanel(graph,captureWidth,captureHeight,pad,rect,material,darkThemeWeight,inkLight);
            if (FAILED(hr)) return Fail(L"draw-panel",hr);
            uint32_t layer2Count=0,layer3Count=0;
            if(hasLayer2)
                hr=DrawShapeLayer(graph,captureWidth,captureHeight,pad,rect,material,darkThemeWeight,
                    2u,graph.scene[0],graph.scene[1],layer2Count);
            else
                context->CopyResource(graph.scene[1].texture.Get(),graph.scene[0].texture.Get());
            if(FAILED(hr))return Fail(L"draw-layer2",hr);
            ID3D11ShaderResourceView* finalScene=graph.scene[1].srv.Get();
            if(hasLayer3)
            {
                // S0 has completed its crop, metrics, and T1 passes. Reuse
                // that texture for T3 so the full-resolution graph needs only
                // two intermediates while the already-presented swapchain
                // continues to retain the last good frame on failure.
                OutputBundle::RenderTexture finalTexture{};
                finalTexture.texture=captureTexture;finalTexture.rtv=captureRtv;finalTexture.srv=captureSrv;
                hr=DrawShapeLayer(graph,captureWidth,captureHeight,pad,rect,material,darkThemeWeight,
                    3u,graph.scene[1],finalTexture,layer3Count);
                finalScene=finalTexture.srv.Get();
            }
            if(FAILED(hr))return Fail(L"draw-layer3",hr);

            const ULONGLONG layerLogNow=GetTickCount64();
            const bool layerCountsChanged=layer2Count!=lastLayer2Count||layer3Count!=lastLayer3Count;
            const ULONGLONG layerLogElapsed=layerLogNow-lastLayerLogMs;
            if(lastLayerLogMs==0||(layerCountsChanged&&layerLogElapsed>=1000u)||layerLogElapsed>=5000u)
            {
                wchar_t fields[320]{};
                swprintf_s(fields,std::size(fields),L"event=layer-pass layer2Count=%u layer3Count=%u rtvBound=1 sigma2=%.2f sigma3=%.2f geometrySequence=%llu",
                    layer2Count,layer3Count,sigma2,sigma3,static_cast<unsigned long long>(rect.sequence));
                diagnostics::Log(L"layer-pass",fields);
                lastLayerLogMs=layerLogNow;lastLayer2Count=layer2Count;lastLayer3Count=layer3Count;
            }

            const bool initializeLayers=replacingOutputs||!hasPresentedFrame;
            if (initializeLayers)
            {
                auto clearPlaceholder=[&](ComPtr<IDXGISwapChain1> const& chain,
                    ComPtr<ID3D11RenderTargetView> const& renderTarget,ID3D11PixelShader* shader)->HRESULT
                {
                    context->ClearRenderTargetView(renderTarget.Get(),clearColor);
                    ID3D11RenderTargetView* target=renderTarget.Get();context->OMSetRenderTargets(1,&target,nullptr);
                    D3D11_VIEWPORT viewport{};viewport.Width=1.0f;viewport.Height=1.0f;viewport.MaxDepth=1.0f;
                    context->RSSetViewports(1,&viewport);context->VSSetShader(vertexShader.Get(),nullptr,0);
                    context->PSSetShader(shader,nullptr,0);context->Draw(3,0);
                    context->OMSetRenderTargets(0,nullptr,nullptr);return chain->Present(0,0);
                };
                hr=clearPlaceholder(frameOutputs->refraction,frameOutputs->refractionRtv,refractionPixelShader.Get());
                if(FAILED(hr))return Fail(L"clear-placeholder-refraction",hr);
                hr=clearPlaceholder(frameOutputs->tint,frameOutputs->tintRtv,tintPixelShader.Get());
                if(FAILED(hr))return Fail(L"clear-placeholder-tint",hr);
            }
            hr=PresentGraph(*frameOutputs,graph,finalScene,captureWidth,captureHeight,rect,pad);
            if(FAILED(hr))return Fail(L"present-graph",hr);
            if (replacingOutputs) std::swap(outputs, spareOutputs);
            hasPresentedFrame = true;
            lastFrameMs = now;
            if(!activeFrameLogged)
            {
                const uint64_t poolBytes=PoolMemoryBytes();
                wchar_t fields[768]{};
                swprintf_s(fields,std::size(fields),L"stage=present backend=WGC captureCandidate=1 capturedPresented=1 materialState=active fallbackReason=none activeShaderVersion=0.3 geometrySequence=%llu captureGeneration=%llu logicalDip=%.2fx%.2f activePx=%ux%u allocPx=%dx%d capacityPx=%ux%u paddingPx=%ld fractionalOffset=%.4f,%.4f monitorSizePx=%ldx%ld dpi=%.3f layer=1 shapeId=0 format=B8G8R8A8_UNORM alphaContract=opaque-linear-intermediate/final-premultiplied-sRGB theme=%.3f lensEnabled=%u B=56 D=22 edge=1.55 power=3.2 sigma=%.2f sigmaLight=2.60 sigmaDark=1.40 split=%.4f poolBytes=%llu",
                    static_cast<unsigned long long>(rect.sequence),static_cast<unsigned long long>(sourceGeneration),
                    shapeWidth/dpiScale,shapeHeight/dpiScale,captureWidth,captureHeight,rect.widthPixels,rect.heightPixels,
                    graph.capacityWidth,graph.capacityHeight,pad,rect.screenX-std::floor(rect.screenX),
                    rect.screenY-std::floor(rect.screenY),monitorWidth,monitorHeight,rect.dpiScale,darkThemeWeight,
                    materialSettings.lensEnabled?1u:0u,material.blur,materialSettings.dispersion*(1.0f-darkThemeWeight/3.0f),
                    static_cast<unsigned long long>(poolBytes));
                diagnostics::Log(L"capture-active",fields);
                activeFrameLogged=true;
            }
            return S_OK;
        }
        catch (...)
        {
            return Fail(L"render-frame-exception",winrt::to_hresult());
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
        // This HWND is the independent glass-only overlay. Never apply display
        // affinity to the content HWND, which remains capturable by screenshots.
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
    impl->lastFailureStage = CaptureFailureStage::ValidateGeometry;
    if (rect.widthPixels <= 0 || rect.heightPixels <= 0 ||
        static_cast<UINT>(rect.widthPixels)>impl->MaxActiveDimension()||static_cast<UINT>(rect.heightPixels)>impl->MaxActiveDimension()||
        !std::isfinite(rect.screenX) || !std::isfinite(rect.screenY) || !std::isfinite(rect.radiusPixels) || rect.radiusPixels < 0.0f ||
        !std::isfinite(rect.dpiScale) || rect.dpiScale < 0.25f || rect.dpiScale > 8.0f || !std::isfinite(rect.surfaceTension) ||
        std::abs(rect.surfaceTension) > 1.15f || !std::isfinite(rect.shapeWidthPixels) ||
        !std::isfinite(rect.shapeHeightPixels) || rect.shapeWidthPixels < 0 || rect.shapeHeightPixels < 0 ||
        rect.shapeWidthPixels > rect.widthPixels || rect.shapeHeightPixels > rect.heightPixels) return E_INVALIDARG;
    if(rect.glassShapeCount>32||(!rect.glassShapes&&rect.glassShapeCount!=0))return E_INVALIDARG;
    std::vector<GDGlassShapeV1> incomingShapes;
    try
    {
        incomingShapes.reserve(rect.glassShapeCount);
        for(uint32_t index=0;index<rect.glassShapeCount;++index)
        {
            GDGlassShapeV1 const& shape=rect.glassShapes[index];
            if(shape.id==0||(shape.layer!=2u&&shape.layer!=3u)||shape.enabled>1u||
                !std::isfinite(shape.xDip)||!std::isfinite(shape.yDip)||
                !std::isfinite(shape.widthDip)||!std::isfinite(shape.heightDip)||
                !std::isfinite(shape.radiusDip)||!std::isfinite(shape.materialize)||!std::isfinite(shape.press)||
                std::abs(shape.xDip)>4096.0f||std::abs(shape.yDip)>4096.0f||
                shape.widthDip<=0.0f||shape.heightDip<=0.0f||shape.widthDip>4096.0f||shape.heightDip>4096.0f||
                shape.radiusDip<0.0f||shape.radiusDip>4096.0f||shape.materialize<0.0f||shape.materialize>1.0f||
                shape.press<0.0f||shape.press>1.0f)return E_INVALIDARG;
            for(auto const& previous:incomingShapes)if(previous.id==shape.id)return E_INVALIDARG;
            incomingShapes.push_back(shape);
        }
    }
    catch(...){return E_OUTOFMEMORY;}
    const bool shapesChanged=rect.sequence!=impl->sceneSequence||incomingShapes.size()!=impl->sceneShapes.size()||
        !std::equal(incomingShapes.begin(),incomingShapes.end(),impl->sceneShapes.begin(),
            [](GDGlassShapeV1 const& a,GDGlassShapeV1 const& b){return std::memcmp(&a,&b,sizeof(a))==0;});
    if(shapesChanged)
    {
        impl->sceneShapes=std::move(incomingShapes);
        impl->sceneSequence=rect.sequence;
    }
    if (IsIconic(impl->widget)) return S_FALSE;

    RECT widgetRect{};
    impl->lastFailureStage = CaptureFailureStage::ReadWindowBounds;
    if (!GetPhysicalWindowBounds(impl->widget, widgetRect))
        return HRESULT_FROM_WIN32(GetLastError());
    impl->lastFailureStage = CaptureFailureStage::ValidateGeometry;
    const float originOffsetX = rect.screenX - static_cast<float>(widgetRect.left);
    const float originOffsetY = rect.screenY - static_cast<float>(widgetRect.top);
    const float shapeWidth = rect.shapeWidthPixels > 0.0f ? rect.shapeWidthPixels : static_cast<float>(rect.widthPixels);
    const float shapeHeight = rect.shapeHeightPixels > 0.0f ? rect.shapeHeightPixels : static_cast<float>(rect.heightPixels);
    const double widthRemainder = static_cast<double>(rect.widthPixels) - shapeWidth - originOffsetX;
    const double heightRemainder = static_cast<double>(rect.heightPixels) - shapeHeight - originOffsetY;
    if (widgetRect.right - widgetRect.left != rect.widthPixels || widgetRect.bottom - widgetRect.top != rect.heightPixels ||
        originOffsetX < 0.0f || originOffsetX >= 1.0f || originOffsetY < 0.0f || originOffsetY >= 1.0f ||
        widthRemainder < -0.0001 || widthRemainder >= 1.0001 ||
        heightRemainder < -0.0001 || heightRemainder >= 1.0001)
        return E_INVALIDARG;
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

    if (ratio >= 0.999f && !rect.motionActive)
    {
        impl->StopSession();
        return S_FALSE;
    }

    const double now = static_cast<double>(GetTickCount64());
    impl->RefreshSystemTheme(now);
    const bool rectChanged=rect.screenX!=impl->lastRect.screenX||rect.screenY!=impl->lastRect.screenY||
        rect.widthPixels!=impl->lastRect.widthPixels||rect.heightPixels!=impl->lastRect.heightPixels||
        rect.radiusPixels!=impl->lastRect.radiusPixels||rect.dpiScale!=impl->lastRect.dpiScale||
        rect.shapeWidthPixels!=impl->lastRect.shapeWidthPixels||rect.shapeHeightPixels!=impl->lastRect.shapeHeightPixels||
        rect.surfaceTension!=impl->lastRect.surfaceTension||rect.motionActive!=impl->lastRect.motionActive||
        rect.sequence!=impl->lastRect.sequence||shapesChanged;
    if (rectChanged) impl->lastMotionMs = now;

    if (!impl->sessionActive)
    {
        impl->lastFailureStage = CaptureFailureStage::StartSession;
        const HRESULT hr = impl->StartSession(rect);
        if (hr == E_PENDING) return S_OK;
        if (FAILED(hr))
        {
            impl->TripIfDeviceError(hr);
            return hr;
        }
        // The first call only starts the session; a frame arrives shortly after.
        impl->lastFailureStage = CaptureFailureStage::None;
        return S_OK;
    }
    const HRESULT hr = impl->RenderFrame(rect);
    if (FAILED(hr)) impl->TripIfDeviceError(hr);
    if (hr == S_OK)
    {
        impl->lastRect = rect;
        impl->lastRect.glassShapes=nullptr;
        impl->lastRect.glassShapeCount=0;
        impl->lastFailureStage = CaptureFailureStage::None;
    }
    return hr == S_FALSE ? S_OK : hr;
}

CaptureFailureStage LiquidGlassCapture::LastFailureStage() const noexcept
{
    return impl_ ? impl_->lastFailureStage : CaptureFailureStage::Other;
}

CaptureDiagnosticSnapshot LiquidGlassCapture::GetDiagnostics() const noexcept
{
    if(!impl_)return {};
    CaptureDiagnosticSnapshot result{};
    result.captureGeneration=impl_->sourceGeneration;
    result.sourceFormat=static_cast<uint32_t>(impl_->lastSourceFormat);
    result.sourceWidth=impl_->lastSourceWidth;result.sourceHeight=impl_->lastSourceHeight;
    result.contentWidth=impl_->lastContentWidth;result.contentHeight=impl_->lastContentHeight;
    result.activeWidth=impl_->lastCaptureActiveWidth;result.activeHeight=impl_->lastCaptureActiveHeight;
    result.capacityWidth=impl_->lastCaptureCapacityWidth;result.capacityHeight=impl_->lastCaptureCapacityHeight;
    result.monitorWidth=static_cast<uint32_t>(std::max<LONG>(0,impl_->lastMonitorWidth));
    result.monitorHeight=static_cast<uint32_t>(std::max<LONG>(0,impl_->lastMonitorHeight));
    result.paddingPixels=impl_->lastPaddingPixels;
    result.presented=impl_->hasPresentedFrame;
    return result;
}

bool LiquidGlassCapture::IsRecoverableGeometryFailure(HRESULT failure) const noexcept
{
    if (!impl_) return false;
    const bool transientGeometryResult = failure == E_INVALIDARG || failure == E_BOUNDS ||
        failure == HRESULT_FROM_WIN32(ERROR_RETRY) || failure == E_OUTOFMEMORY ||
        failure == DXGI_ERROR_INVALID_CALL;
    if (!transientGeometryResult) return false;
    switch (impl_->lastFailureStage)
    {
    case CaptureFailureStage::ValidateGeometry:
    case CaptureFailureStage::StartSession:
    case CaptureFailureStage::ValidateCaptureGeometry:
    case CaptureFailureStage::CreateCaptureTexture:
    case CaptureFailureStage::ReadMonitorBounds:
    case CaptureFailureStage::ResizeOutput:
    case CaptureFailureStage::PresentFrame:
        return true;
    default:
        return false;
    }
}

HRESULT LiquidGlassCapture::SetEnabled(bool enabled) noexcept
{
    if (impl_ == nullptr) return E_POINTER;
    impl_->enabled = enabled;
    if (!enabled)
    {
        impl_->StopSession();
        impl_->outputs = OutputBundle{};
        impl_->spareOutputs = OutputBundle{};
        impl_->hasPresentedFrame = false;
        impl_->captureRtv.Reset();
        impl_->inkColorSwapChain.Reset();
        impl_->inkColorRtv.Reset();
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
        impl_->lastCaptureActiveWidth=impl_->lastCaptureActiveHeight=0;
        impl_->lastCaptureCapacityWidth=impl_->lastCaptureCapacityHeight=0;
        impl_->lastPaddingPixels=impl_->lastMonitorWidth=impl_->lastMonitorHeight=0;
        impl_->lastSourceFormat=DXGI_FORMAT_UNKNOWN;
        impl_->lastSourceWidth=impl_->lastSourceHeight=impl_->lastContentWidth=impl_->lastContentHeight=0;
        impl_->waitingFrameLogged=false;impl_->activeFrameLogged=false;
    }
    return S_OK;
}

HRESULT LiquidGlassCapture::SetSettings(GDLiquidGlassSettings const& settings) noexcept
{
    if (impl_ == nullptr) return E_POINTER;
    if (settings.version != 1 || settings.size != sizeof(GDLiquidGlassSettings)) return E_INVALIDARG;
    auto* impl = impl_;
    const auto sanitized = impl->SanitizeSettings(settings);
    if (impl->settingsInitialized && std::memcmp(&sanitized, &impl->materialSettings, sizeof(sanitized)) == 0)
        return S_OK;
    const double now = static_cast<double>(GetTickCount64());
    const LiquidGlassMaterialSettings previousMaterial = impl->settingsInitialized
        ? impl->ActiveMaterial(now) : impl->TargetMaterial();
    const float previousInk = impl->settingsInitialized ? impl->ActiveInkLight(now) : impl->TargetInkLight();
    const float previousInkTarget = impl->settingsInitialized ? impl->TargetInkLight() : previousInk;
    const bool wasDark = impl->resolvedThemeDark;
    const float previousDarkWeight = impl->ActiveDarkWeight(now);

    impl->materialSettings = sanitized;
    impl->lastMotionMs = now;
    impl->lastFrameMs = 0.0;
    const bool nextDark = impl->DetectDarkTheme(impl->materialSettings.theme);
    impl->resolvedThemeDark = nextDark;
    const float nextInkTarget = impl->TargetInkLight();
    if (!impl->settingsInitialized)
    {
        impl->settingsInitialized = true;
        impl->themeTransitionActive = false;
        impl->themeInkFrom = nextInkTarget;
        impl->themeDarkFrom = nextDark ? 1.0f : 0.0f;
        return S_OK;
    }

    if (nextDark != wasDark || nextInkTarget != previousInkTarget)
    {
        impl->themeMaterialFrom = previousMaterial;
        impl->themeInkFrom = previousInk;
        impl->themeDarkFrom = previousDarkWeight;
        impl->themeTransitionStartMs = now;
        impl->themeTransitionActive = true;
    }
    else
    {
        // Material sliders apply at the next GPU frame without interpolation.
        impl->themeTransitionActive = false;
        impl->themeInkFrom = nextInkTarget;
        impl->themeDarkFrom = nextDark ? 1.0f : 0.0f;
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

float LiquidGlassCapture::ShadowScale() const noexcept
{
    if (impl_ == nullptr) return 1.0f;
    return const_cast<Impl*>(impl_)->ActiveMaterial(static_cast<double>(GetTickCount64())).shadow;
}

float LiquidGlassCapture::DarkThemeWeight() const noexcept
{
    if (impl_ == nullptr) return 0.0f;
    auto* impl = const_cast<Impl*>(impl_);
    return impl->ActiveDarkWeight(static_cast<double>(GetTickCount64()));
}

bool LiquidGlassCapture::FuseTripped() const noexcept
{
    return impl_ != nullptr && impl_->fuseTripped;
}

void* LiquidGlassCapture::OutputSwapChain() const noexcept
{
    if (impl_ == nullptr) return nullptr;
    return impl_->outputs.output.Get();
}

void* LiquidGlassCapture::RefractionSwapChain() const noexcept
{
    if (impl_ == nullptr) return nullptr;
    return impl_->outputs.refraction.Get();
}

void* LiquidGlassCapture::TintSwapChain() const noexcept
{
    if (impl_ == nullptr) return nullptr;
    return impl_->outputs.tint.Get();
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
    rect.screenX = static_cast<float>(screenX);
    rect.screenY = static_cast<float>(screenY);
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

extern "C" __declspec(dllexport) HRESULT __stdcall GdLiquidGlass_SetSettings(void* handle, GDLiquidGlassSettings const* settings)
{
    if (handle == nullptr || settings == nullptr) return E_POINTER;
    return static_cast<gd::liquidglass::LiquidGlassCapture*>(handle)->SetSettings(*settings);
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

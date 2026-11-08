#pragma once
#include <windows.h>
#include <cstdint>
#include <cstddef>

// This ABI contains only layout, paint commands and operating-system events.
// No music, forms, or application policy is held by the native layer.
// Liquid material is renderer state. Enabled intent survives capture fallback.
extern "C" __declspec(dllexport) HRESULT __stdcall GDSetLiquidGlassEnabled(void* context,int enabled);
extern "C" __declspec(dllexport) HRESULT __stdcall GDSetLiquidGlassMotionActive(void* context,int active);
struct GDLiquidGlassSettings;
extern "C" __declspec(dllexport) HRESULT __stdcall GDSetLiquidGlassSettings(void* context,GDLiquidGlassSettings const* settings);
extern "C" __declspec(dllexport) HRESULT __stdcall GDGetLiquidGlassStatus(void* context,wchar_t* text,unsigned capacity);
extern "C" __declspec(dllexport) HRESULT __stdcall GDSaveLiquidGlassDiagnosticFrame(void* context,wchar_t const* path);
// Explicit QA screenshot window, not pixel output. Frozen material; bounded
// affinity lease; worker restarted only by the host UI thread after restoration.
extern "C" __declspec(dllexport) HRESULT __stdcall GDBeginDiagnosticSnapshot(void* context,unsigned durationMilliseconds);
extern "C" __declspec(dllexport) HRESULT __stdcall GDEndDiagnosticSnapshot(void* context);
extern "C" __declspec(dllexport) HRESULT __stdcall GDGetDiagnosticSnapshotStatus(void* context,wchar_t* text,unsigned capacity);
struct GDFrame { float width, height, radius, blur, tint; int solid; };
extern "C" __declspec(dllexport) HRESULT __stdcall GDApplyFrameWithTension(void* context,GDFrame const* frame,float tension);
extern "C" __declspec(dllexport) HRESULT __stdcall GDApplyPositionedFrameWithTension(void* context,GDFrame const* frame,float tension,int screenX,int screenY) noexcept;
extern "C" __declspec(dllexport) HRESULT __stdcall GDApplyContinuousPositionedFrameWithTension(void* context,GDFrame const* frame,float tension,float screenX,float screenY) noexcept;
// Versioned additive scene ABI. A scene snapshot is copied and committed with
// the positioned frame; old GDFrame entry points remain valid for empty scenes.
struct GDGlassShapeV1
{
    uint32_t id,parentId,layer,groupId;
    float xDip,yDip,widthDip,heightDip,radiusDip,materialize,press;
    uint32_t enabled;
};
extern "C" __declspec(dllexport) HRESULT __stdcall GDApplySceneFrameV1(void* context,
    GDFrame const* frame,float tension,float screenX,float screenY,uint64_t sequence,
    GDGlassShapeV1 const* shapes,uint32_t count,uint32_t stride,uint32_t structSize,uint32_t version) noexcept;
struct GDContext { int x, y; float width, height; unsigned dpi; };
struct GDPoint { float x, y; };
struct GDPointer { int kind; float x, y; int screenX, screenY, controlId; double value; };
struct GDOp
{
    int kind; float x, y, width, height, radius, fontSize; unsigned color;
    int bold, center; wchar_t const* text; unsigned char const* image; unsigned imageSize;
};
// Additive x64 typography ABI. The legacy prefix and every V1 export remain
// unchanged; callers negotiate version/size before passing this structure.
struct GDOpV2
{
    GDOp legacy;
    int32_t fontWeight;
    float trackingEm;
    uint32_t textRole;
    float lineHeightDip;
    float baselineDip;
    uint32_t textFlags;
    wchar_t const* localeName;
};
struct GDNativeAbiInfo
{uint32_t size,version,v1Size,v2Size,capabilities,reserved;};
struct GDTextMetrics
{float advance,widthIncludingTrailingWhitespace,height,baseline,lineHeight;uint32_t lineCount;};
struct GDInkBounds
{float left,top,right,bottom;};
struct GDTypographyCacheStats
{uint64_t formats,layouts,formatHits,layoutHits,layoutsCreated;};
struct IInspectable;
extern "C" __declspec(dllexport) HRESULT __stdcall GDGetNativeAbiInfo(GDNativeAbiInfo* result);
extern "C" __declspec(dllexport) HRESULT __stdcall GDConfigureTypographyV2(void* context,uint32_t profile,uint32_t tokenRevision,wchar_t const* qaFontPath);
extern "C" __declspec(dllexport) HRESULT __stdcall GDMeasureTextV2(void* context,GDOpV2 const* operation,uint32_t stride,GDTextMetrics* result);
extern "C" __declspec(dllexport) HRESULT __stdcall GDMeasureTextInkBoundsV2(void* context,GDOpV2 const* operation,uint32_t stride,GDInkBounds* result);
extern "C" __declspec(dllexport) HRESULT __stdcall GDRenderSurfaceV2(void* context,float width,float height,GDOpV2 const* operations,uint32_t count,uint32_t stride,IInspectable** surface);
extern "C" __declspec(dllexport) HRESULT __stdcall GDGetTypographyCacheStats(void* context,GDTypographyCacheStats* result);
struct GDAccessible
{
    int id; wchar_t const* name; float x, y, width, height; int enabled, isRange;
    double value, minimum, maximum;
};
using GDCallback = void (__stdcall*)(void* user, GDPointer const* event);

static_assert(sizeof(GDFrame) == 24);
static_assert(sizeof(GDGlassShapeV1) == 48);
static_assert(sizeof(GDContext) == 20);
static_assert(sizeof(GDPointer) == 32);
static_assert(sizeof(GDOp) == 64);
static_assert(sizeof(void*) == 8,"GlassDesk native ABI is x64 only.");
static_assert(sizeof(GDOpV2) == 96);
static_assert(offsetof(GDOpV2,legacy)==0);
static_assert(offsetof(GDOpV2,fontWeight)==64);
static_assert(offsetof(GDOpV2,trackingEm)==68);
static_assert(offsetof(GDOpV2,textRole)==72);
static_assert(offsetof(GDOpV2,lineHeightDip)==76);
static_assert(offsetof(GDOpV2,baselineDip)==80);
static_assert(offsetof(GDOpV2,textFlags)==84);
static_assert(offsetof(GDOpV2,localeName)==88);
static_assert(sizeof(GDNativeAbiInfo)==24);
static_assert(sizeof(GDTextMetrics)==24);
static_assert(sizeof(GDInkBounds)==16);
static_assert(sizeof(GDTypographyCacheStats)==40);
static_assert(sizeof(GDAccessible) == 64);

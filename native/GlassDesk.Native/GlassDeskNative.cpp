#define WIN32_LEAN_AND_MEAN
#define NOMINMAX
#include <windows.h>
#include <windowsx.h>
#include <dwmapi.h>
#include <DispatcherQueue.h>
#include <d3d11.h>
#include <d3d11_4.h>
#include <dxgi.h>
#include <d2d1_1.h>
#include <d2d1effects.h>
#include <dwrite.h>
#include <wincodec.h>
#include <windows.ui.composition.interop.h>
#include <windows.graphics.effects.interop.h>
#include <windows.graphics.interop.h>
#include <winrt/Windows.Foundation.h>
#include <winrt/Windows.Foundation.Collections.h>
#include <winrt/Windows.Graphics.Effects.h>
#include <winrt/Windows.Graphics.h>
#include <winrt/Windows.Graphics.DirectX.h>
#include <winrt/Windows.System.h>
#include <winrt/Windows.UI.Composition.Desktop.h>
#include <winrt/Windows.UI.ViewManagement.h>
#include <cmath>
#include <memory>
#include <vector>
#include <list>
#include <string>
#include <algorithm>
#include <limits>
#include <initializer_list>
#include <iterator>
#include <utility>
#include <cstdint>
#include <cstring>
#include "NativeApi.h"
#include "PointerSurfaceGate.h"
#include "Accessibility.h"
#include "LiquidGlass.h"
#include "LiquidGlassCapture.h"
#include "SurfaceTension.h"
#include "TypographyEngine.h"
#include "DesktopMaterialApi.h"
#include "OwnedImageGlass.h"
#include "CleanupResult.h"
#include "DesktopSourceViewport.h"
#include "ArtworkFailurePolicy.h"
#include "ShadowRasterProfile.h"
#include "LiquidGlassDiagnostics.h"

using namespace winrt;
using namespace winrt::Windows::Foundation;
using namespace winrt::Windows::Graphics::Effects;
using namespace winrt::Windows::Graphics::DirectX;
using namespace winrt::Windows::UI::Composition;
using namespace winrt::Windows::UI::Composition::Desktop;
using EffectInterop = ABI::Windows::Graphics::Effects::IGraphicsEffectD2D1Interop;

namespace gd
{
constexpr UINT DpiTimer=71;
constexpr UINT LiquidTimer=72;
constexpr UINT AuthorizedShortTestTimer=73;
constexpr UINT LiquidCaptureTimer=74;
constexpr UINT RecoveredMessage=WM_APP+74;
constexpr UINT SourceRefreshMessage=WM_APP+75;
constexpr int ContextEvent=100;
constexpr wchar_t WindowClass[]=L"GlassDesk.Native.Host.0.1";
constexpr wchar_t GlassWindowClass[]=L"GlassDesk.Native.Glass.0.1";
constexpr wchar_t ShadowClass[]=L"GlassDesk.Native.Shadow.0.1";

// Never report success after a failed window operation, even if a provider
// leaves last-error at zero. Capture it before another window call can run.
HRESULT WindowCallResult(BOOL succeeded) noexcept
{
    if(succeeded)return S_OK;
    const DWORD error=GetLastError();
    return error?HRESULT_FROM_WIN32(error):E_FAIL;
}

HRESULT PhysicalFrameSize(GDFrame const& frame,unsigned dpi,bool legacyFlag,int& width,int& height) noexcept
{
    width=height=0;
    const float scale=static_cast<float>(dpi)/96.0f;
    if(!std::isfinite(scale)||scale<=0.0f||
        (legacyFlag&&(scale<0.5f||scale>4.0f)))return E_INVALIDARG;
    const float physicalWidth=frame.width*scale,physicalHeight=frame.height*scale;
    const double maxInt=static_cast<double>(std::numeric_limits<int>::max());
    if(!std::isfinite(physicalWidth)||!std::isfinite(physicalHeight)||
        physicalWidth<0.5f||physicalHeight<0.5f||physicalWidth>maxInt||physicalHeight>maxInt)
        return E_BOUNDS;
    const long roundedWidth=std::lround(physicalWidth),roundedHeight=std::lround(physicalHeight);
    if(roundedWidth<1||roundedHeight<1||roundedWidth>std::numeric_limits<int>::max()||
        roundedHeight>std::numeric_limits<int>::max())return E_BOUNDS;
    width=static_cast<int>(roundedWidth);height=static_cast<int>(roundedHeight);
    if(legacyFlag&&(width>1024||height>1024))return E_BOUNDS;
    return S_OK;
}

bool ValidFrameRequest(GDFrame const& frame,float tension) noexcept
{
    return std::isfinite(tension)&&std::abs(tension)<=1.15f&&
        std::isfinite(frame.width)&&std::isfinite(frame.height)&&std::isfinite(frame.radius)&&
        std::isfinite(frame.blur)&&std::isfinite(frame.tint)&&frame.width>=1&&frame.height>=1&&
        frame.width<=4096&&frame.height<=4096&&frame.radius>=0&&frame.blur>=0&&frame.blur<=100;
}

struct FramePixelGeometry
{
    int allocationWidth{},allocationHeight{};
    float shapeWidth{},shapeHeight{};
};

struct MaterialEffect : implements<MaterialEffect,IGraphicsEffect,IGraphicsEffectSource,EffectInterop>
{
    bool grain{};
    hstring name{L"GlassBlur"};
    IGraphicsEffectSource source{nullptr};
    hstring Name()const{return name;}
    void Name(hstring const& value){name=value;}
    HRESULT __stdcall GetEffectId(GUID* result)noexcept override
    {if(!result)return E_POINTER;*result=grain?CLSID_D2D1Border:CLSID_D2D1GaussianBlur;return S_OK;}
    HRESULT __stdcall GetNamedPropertyMapping(LPCWSTR value,UINT* index,
        ABI::Windows::Graphics::Effects::GRAPHICS_EFFECT_PROPERTY_MAPPING* mapping)noexcept override
    {
        if(!value||!index||!mapping)return E_POINTER;
        if(grain||wcscmp(value,L"BlurAmount"))return E_INVALIDARG;
        *index=D2D1_GAUSSIANBLUR_PROP_STANDARD_DEVIATION;
        *mapping=ABI::Windows::Graphics::Effects::GRAPHICS_EFFECT_PROPERTY_MAPPING_DIRECT;return S_OK;
    }
    HRESULT __stdcall GetPropertyCount(UINT* result)noexcept override {if(!result)return E_POINTER;*result=grain?2:3;return S_OK;}
    HRESULT __stdcall GetProperty(UINT index,ABI::Windows::Foundation::IPropertyValue** result)noexcept override
    {
        if(!result)return E_POINTER;*result=nullptr;
        try
        {
            IPropertyValue value{nullptr};
            if(grain)
            {if(index>1)return E_INVALIDARG;value=PropertyValue::CreateUInt32(D2D1_BORDER_EDGE_MODE_WRAP).as<IPropertyValue>();}
            else if(index==0)value=PropertyValue::CreateSingle(20).as<IPropertyValue>();
            else if(index==1)value=PropertyValue::CreateUInt32(D2D1_GAUSSIANBLUR_OPTIMIZATION_BALANCED).as<IPropertyValue>();
            else if(index==2)value=PropertyValue::CreateUInt32(D2D1_BORDER_MODE_HARD).as<IPropertyValue>();
            else return E_INVALIDARG;
            *result=static_cast<ABI::Windows::Foundation::IPropertyValue*>(detach_abi(value));return S_OK;
        }catch(...){return to_hresult();}
    }
    HRESULT __stdcall GetSource(UINT index,ABI::Windows::Graphics::Effects::IGraphicsEffectSource** result)noexcept override
    {if(!result)return E_POINTER;*result=nullptr;if(index)return E_INVALIDARG;auto copy=source;*result=static_cast<ABI::Windows::Graphics::Effects::IGraphicsEffectSource*>(detach_abi(copy));return S_OK;}
    HRESULT __stdcall GetSourceCount(UINT* result)noexcept override {if(!result)return E_POINTER;*result=1;return S_OK;}
};

struct ImageEntry {std::vector<unsigned char> bytes;com_ptr<ID2D1Bitmap1> bitmap;};

// Retain identical segment topology for GPU path interpolation, including
// collapsed polygons. No playback state or icon policy lives in this bridge.
com_ptr<ID2D1PathGeometry> RoundedPolygon(ID2D1Factory* factory,std::vector<GDPoint> const& points,float rounding)
{
    com_ptr<ID2D1PathGeometry> path;check_hresult(factory->CreatePathGeometry(path.put()));
    com_ptr<ID2D1GeometrySink> sink;check_hresult(path->Open(sink.put()));
    std::vector<D2D1_POINT_2F> incoming(points.size()),outgoing(points.size());
    for(size_t i=0;i<points.size();++i)
    {
        auto p=points[i],previous=points[(i+points.size()-1)%points.size()],next=points[(i+1)%points.size()];
        float before=std::hypot(previous.x-p.x,previous.y-p.y),after=std::hypot(next.x-p.x,next.y-p.y);
        float cut=std::min(rounding,std::min(before,after)*.4f);
        float a=before>0?cut/before:0,b=after>0?cut/after:0;
        incoming[i]={p.x+(previous.x-p.x)*a,p.y+(previous.y-p.y)*a};
        outgoing[i]={p.x+(next.x-p.x)*b,p.y+(next.y-p.y)*b};
    }
    sink->BeginFigure(incoming[0],D2D1_FIGURE_BEGIN_FILLED);
    for(size_t i=0;i<points.size();++i)
    {
        auto p=points[i];auto a=incoming[i],b=outgoing[i];
        sink->AddBezier({{a.x+(p.x-a.x)*2/3,a.y+(p.y-a.y)*2/3},
            {b.x+(p.x-b.x)*2/3,b.y+(p.y-b.y)*2/3},b});
        sink->AddLine(incoming[(i+1)%points.size()]);
    }
    sink->EndFigure(D2D1_FIGURE_END_CLOSED);check_hresult(sink->Close());return path;
}

struct GeometrySource : implements<GeometrySource,winrt::Windows::Graphics::IGeometrySource2D,ABI::Windows::Graphics::IGeometrySource2DInterop>
{
    std::vector<GDPoint> points;
    float rounding;
    com_ptr<ID2D1PathGeometry> geometry;
    GeometrySource(ID2D1Factory* factory,std::vector<GDPoint> value,float radius)
        :points(std::move(value)),rounding(radius),geometry(RoundedPolygon(factory,points,radius)){}
    HRESULT __stdcall GetGeometry(ID2D1Geometry** value)noexcept override
    {if(!value)return E_POINTER;*value=geometry.get();(*value)->AddRef();return S_OK;}
    HRESULT __stdcall TryGetGeometryUsingFactory(ID2D1Factory* factory,ID2D1Geometry** value)noexcept override
    {
        if(!factory||!value)return E_POINTER;*value=nullptr;
        try{RoundedPolygon(factory,points,rounding).as<ID2D1Geometry>().copy_to(value);return S_OK;}catch(...){return to_hresult();}
    }
};

// Composition owns the live backdrop. These paths only mask a clearer
// optical edge; no pixels leave the system compositor or enter app memory.
struct RetainedGeometrySource : implements<RetainedGeometrySource,winrt::Windows::Graphics::IGeometrySource2D,ABI::Windows::Graphics::IGeometrySource2DInterop>
{
    com_ptr<ID2D1Geometry> geometry;
    explicit RetainedGeometrySource(com_ptr<ID2D1Geometry> value):geometry(std::move(value)){}
    HRESULT __stdcall GetGeometry(ID2D1Geometry** value)noexcept override
    {if(!value)return E_POINTER;geometry.copy_to(value);return S_OK;}
    HRESULT __stdcall TryGetGeometryUsingFactory(ID2D1Factory* factory,ID2D1Geometry** value)noexcept override
    {
        if(!factory||!value)return E_POINTER;*value=nullptr;
        try
        {
            com_ptr<ID2D1PathGeometry> copy;check_hresult(factory->CreatePathGeometry(copy.put()));
            com_ptr<ID2D1GeometrySink> sink;check_hresult(copy->Open(sink.put()));
            check_hresult(geometry->Simplify(D2D1_GEOMETRY_SIMPLIFICATION_OPTION_CUBICS_AND_LINES,nullptr,sink.get()));
            check_hresult(sink->Close());copy.as<ID2D1Geometry>().copy_to(value);return S_OK;
        }catch(...){return to_hresult();}
    }
};

struct Host : std::enable_shared_from_this<Host>
{
    DWORD ownerThread{GetCurrentThreadId()};
    HWND window{},glassWindow{},shadow{};
    HANDLE systemSourceSingleton{};
    HRESULT shutdownResult{S_OK};
    GDCallback callback{};void* user{};
    GDFrame frame{232,48,24,20,.24f,0};
    unsigned dpi{96}; bool visible{},tracking{},captured{},destroying{},fallback{},restoringTopmost{};
    bool deferFrameSurfaceUpdates{},captureMotionActive{};
    float presentationOffsetX{},presentationOffsetY{},cachedShapeWidth{-1},cachedShapeHeight{-1};
    int cachedWidth{},cachedHeight{};float cachedRadius{-1},surfaceTension{},cachedTension{-2};unsigned cachedDpi{};
    winrt::Windows::System::DispatcherQueueController queue{nullptr};
    Compositor compositor{nullptr}; DesktopWindowTarget target{nullptr},glassTarget{nullptr};
    ContainerVisual root{nullptr},glassRoot{nullptr},content{nullptr};
    SpriteVisual clearEdge{nullptr};
    CompositionEffectBrush clearBackdrop{nullptr};
    CompositionPathGeometry clearEdgeGeometry{nullptr};
    bool cachedClearEdge{};
    SpriteVisual glass{nullptr},liquidOverlay{nullptr},liquidRefractionOverlay{nullptr},tint{nullptr},grain{nullptr},opticalLight{nullptr};
    ShapeVisual border{nullptr},bevel{nullptr};
    CompositionPathGeometry tensionGeometry{nullptr};
    CompositionRoundedRectangleGeometry clipGeometry{nullptr},borderGeometry{nullptr},shoulderGeometry{nullptr},meniscusGeometry{nullptr};
    CompositionSpriteShape borderShape{nullptr},shoulderShape{nullptr},meniscusShape{nullptr};
    CompositionEffectBrush backdrop{nullptr};
    CompositionLinearGradientBrush tintBrush{nullptr},borderBrush{nullptr},shoulderBrush{nullptr},meniscusBrush{nullptr};
    CompositionColorBrush solidTintBrush{nullptr},solidBorderBrush{nullptr};
    float materialTint{-1},materialBlur{-1};
    COLORREF materialWindowColor{CLR_INVALID},materialBorderColor{CLR_INVALID};
    bool materialApplied{},materialSolid{},materialGrainVisible{},materialLiquidCapture{};
    CompositionGraphicsDevice graphics{nullptr};CompositionDrawingSurface grainSurface{nullptr};
    com_ptr<ID3D11Device> d3d;com_ptr<ID3D11DeviceContext> d3dContext;
    com_ptr<ID2D1Factory1> d2dFactory;com_ptr<ID2D1Device> d2d;com_ptr<ID2D1DeviceContext> cacheContext;
    com_ptr<ID2D1Factory> geometryFactory;
    com_ptr<IDWriteFactory> textFactory;com_ptr<IWICImagingFactory> wic;
    std::unique_ptr<typography::Engine> typographyEngine;
    bool liquidEnabled{},liquidReady{},liquidPresented{};
    bool liquidCaptureCandidate{},liquidCapturePresented{},liquidCaptureFuseTripped{};
    ULONGLONG lastGeometryFailureLogMs{};
    wchar_t const* lastGeometryFailureLogStage{L""};
    HRESULT lastGeometryFailureLogCode{S_OK};
    ULONGLONG liquidCaptureRetryStartedMs{};
    UINT liquidCaptureRetryDelayMs{16};
    bool liquidCaptureGeometryRetrying{},liquidCaptureSurfaceRetrying{};
    HRESULT liquidCaptureFailure{S_OK};
    GDLiquidGlassSettings liquidSettings{sizeof(GDLiquidGlassSettings),1};
    std::vector<GDGlassShapeV1> glassSceneShapes;
    uint64_t glassSceneSequence{};
    uint64_t appliedGlassSceneSequence{};
    uint32_t renderOpIndex{std::numeric_limits<uint32_t>::max()};
    ULONGLONG lastRenderErrorLogMs{};
    HRESULT lastRenderErrorLogHr{S_OK};
    uint32_t lastRenderErrorLogIndex{std::numeric_limits<uint32_t>::max()};
    wchar_t const* lastRenderErrorLogStage{L""};
    bool opticalReady{};
    CompositionSurfaceBrush opticalBrush{nullptr};CompositionDrawingSurface opticalSurface{nullptr};
    std::unique_ptr<LiquidGlass> liquid;
    bool authorizedIncidentShortTest{};
    uint32_t materialBackend{1}; // 0 solid, 1 legacy, 2 owned-image candidate; immutable.
    bool graphicsCreated{},ownedFaulted{},ownedImageSet{},ownedEligible{};
    uint64_t ownedSequence{};
    std::vector<float> ownedFrameKey;
    GDOwnedGlassImageMapping ownedMapping{};
    std::unique_ptr<OwnedImageGlass> ownedRenderer;
    CompositionDrawingSurface ownedSurface{nullptr};
    CompositionSurfaceBrush ownedBrush{nullptr};
    CompositionSurfaceBrush liquidBrush{nullptr};CompositionDrawingSurface liquidSurface{nullptr};
    winrt::Windows::UI::Composition::ICompositionSurface liquidCaptureSurface{nullptr},liquidRefractionSurface{nullptr},liquidTintSurface{nullptr},liquidInkSurface{nullptr};
    CompositionSurfaceBrush liquidCaptureBrush{nullptr},liquidRefractionBrush{nullptr},liquidTintCaptureBrush{nullptr};
    struct FallbackLayer
    {
        uint32_t id{};
        SpriteVisual body{nullptr};
        CompositionRoundedRectangleGeometry clipShape{nullptr};
        SpriteVisual veil{nullptr};
        CompositionColorBrush veilBrush{nullptr};
        ShapeVisual rim{nullptr};
        CompositionRoundedRectangleGeometry rimShape{nullptr};
        CompositionSpriteShape rimSprite{nullptr};
    };
    ContainerVisual fallbackLayers{nullptr};
    CompositionEffectBrush fallbackBlurL2{nullptr},fallbackBlurL3{nullptr};
    CompositionLinearGradientBrush fallbackRimBrush{nullptr};
    std::vector<FallbackLayer> fallbackLayerPool;
    uint64_t fallbackKey{~0ull};
    mutable bool fallbackDarkCached{};
    mutable ULONGLONG fallbackDarkCheckedMs{std::numeric_limits<ULONGLONG>::max()};
    void* attachedLiquidSwapChain{};void* attachedLiquidRefractionSwapChain{};void* attachedLiquidTintSwapChain{};void* attachedLiquidInkSwapChain{};
    uint64_t liquidInkSurfaceGeneration{};
    std::unique_ptr<liquidglass::LiquidGlassCapture> liquidCapture;
    std::list<ImageEntry> images;
    HDC shadowDc{};HBITMAP shadowBitmap{},shadowRetiredBitmap{};void* shadowBits{};
    int shadowWidth{},shadowHeight{};float shadowRadius{-1},shadowTension{-2};unsigned shadowDpi{};
    float shadowMaterialScale{-1},shadowThemeWeight{-1};
    float shadowShapeWidth{-1},shadowShapeHeight{-1},shadowOffsetX{-1},shadowOffsetY{-1};
    bool shadowGlassCss{};
    unsigned shadowCapacityWidth{},shadowCapacityHeight{};bool shadowOpticalProfile{};
    int shadowX{std::numeric_limits<int>::min()},shadowY{std::numeric_limits<int>::min()};bool shadowShown{};
    std::shared_ptr<AccessibleState> accessibility=std::make_shared<AccessibleState>();
    ComPtr<ElementProvider> provider;

    ~Host(){Shutdown();}
    float Scale()const{return dpi/96.0f;}
    HRESULT ComputeFramePixelGeometry(GDFrame const& value,unsigned targetDpi,float offsetX,float offsetY,
        FramePixelGeometry& geometry)const noexcept
    {
        geometry={};
        if(!liquidCaptureCandidate)
        {
            const HRESULT hr=PhysicalFrameSize(value,targetDpi,false,geometry.allocationWidth,geometry.allocationHeight);
            if(FAILED(hr))return hr;
            geometry.shapeWidth=float(geometry.allocationWidth);geometry.shapeHeight=float(geometry.allocationHeight);
            return S_OK;
        }
        const float scale=static_cast<float>(targetDpi)/96.0f;
        if(!std::isfinite(scale)||scale<=0.0f||!std::isfinite(offsetX)||!std::isfinite(offsetY)||
            offsetX<0.0f||offsetX>=1.0f||offsetY<0.0f||offsetY>=1.0f)return E_INVALIDARG;
        // Round the same float geometry consumed by Composition and HLSL.
        const float shapeWidth=value.width*scale,shapeHeight=value.height*scale;
        const double allocationWidth=std::ceil(double(shapeWidth)+offsetX),allocationHeight=std::ceil(double(shapeHeight)+offsetY);
        if(!std::isfinite(shapeWidth)||!std::isfinite(shapeHeight)||!std::isfinite(allocationWidth)||!std::isfinite(allocationHeight)||
            shapeWidth<0.5||shapeHeight<0.5||allocationWidth<1||allocationHeight<1||
            allocationWidth>std::numeric_limits<int>::max()||allocationHeight>std::numeric_limits<int>::max())return E_BOUNDS;
        geometry.allocationWidth=static_cast<int>(allocationWidth);geometry.allocationHeight=static_cast<int>(allocationHeight);
        geometry.shapeWidth=static_cast<float>(shapeWidth);geometry.shapeHeight=static_cast<float>(shapeHeight);
        return S_OK;
    }
    void EnsureTopmost()
    {
        if(destroying||restoringTopmost||!visible||!IsWindowVisible(window))return;
        restoringTopmost=true;
        HRESULT result=WindowCallResult(SetWindowPos(window,HWND_TOPMOST,0,0,0,0,SWP_NOMOVE|SWP_NOSIZE|SWP_NOACTIVATE|SWP_NOOWNERZORDER));
        if(SUCCEEDED(result)&&glassWindow)
            result=WindowCallResult(SetWindowPos(glassWindow,window,0,0,0,0,SWP_NOMOVE|SWP_NOSIZE|SWP_NOACTIVATE|SWP_NOOWNERZORDER));
        restoringTopmost=false;
        check_hresult(result);
    }
    HRESULT PositionHostWindows(int x,int y,int width,int height,UINT flags) noexcept
    {
        if(!window)return E_HANDLE;
        RECT current{};
        if((flags&(SWP_NOMOVE|SWP_NOSIZE))&&!GetWindowRect(window,&current))return HRESULT_FROM_WIN32(GetLastError());
        if(flags&SWP_NOMOVE){x=current.left;y=current.top;}
        if(flags&SWP_NOSIZE){width=current.right-current.left;height=current.bottom-current.top;}
        if(!desktopviewport::ClampDesktopPlacement(GetSystemMetrics(SM_XVIRTUALSCREEN),GetSystemMetrics(SM_YVIRTUALSCREEN),
            GetSystemMetrics(SM_CXVIRTUALSCREEN),GetSystemMetrics(SM_CYVIRTUALSCREEN),width,height,x,y))return E_INVALIDARG;
        flags|=SWP_NOACTIVATE|SWP_NOZORDER;
        if(!glassWindow){
            const bool previous=deferFrameSurfaceUpdates;
            deferFrameSurfaceUpdates=true;
            const BOOL result=SetWindowPos(window,nullptr,x,y,width,height,flags);
            deferFrameSurfaceUpdates=previous;
            return WindowCallResult(result);
        }
        HDWP batch=BeginDeferWindowPos(2);
        if(!batch)return HRESULT_FROM_WIN32(GetLastError());
        batch=DeferWindowPos(batch,glassWindow,nullptr,x,y,width,height,flags);
        if(!batch)return HRESULT_FROM_WIN32(GetLastError());
        batch=DeferWindowPos(batch,window,nullptr,x,y,width,height,flags);
        if(!batch)return HRESULT_FROM_WIN32(GetLastError());
        const bool previous=deferFrameSurfaceUpdates;
        deferFrameSurfaceUpdates=true;
        const BOOL result=EndDeferWindowPos(batch);
        deferFrameSurfaceUpdates=previous;
        return WindowCallResult(result);
    }
    void SyncGlassWindowBounds() noexcept
    {
        if(!window||!glassWindow)return;
        RECT contentBounds{},glassBounds{};
        if(!GetWindowRect(window,&contentBounds)||!GetWindowRect(glassWindow,&glassBounds))return;
        if(contentBounds.left==glassBounds.left&&contentBounds.top==glassBounds.top&&
           contentBounds.right==glassBounds.right&&contentBounds.bottom==glassBounds.bottom)return;
        SetWindowPos(glassWindow,nullptr,contentBounds.left,contentBounds.top,
            contentBounds.right-contentBounds.left,contentBounds.bottom-contentBounds.top,SWP_NOACTIVATE|SWP_NOZORDER);
    }
    HRESULT CheckThread()const{return ownerThread==GetCurrentThreadId()?S_OK:RPC_E_WRONG_THREAD;}
    void TripOwned(HRESULT failure=E_ABORT) noexcept
    {
        if(materialBackend!=2||ownedFaulted)return;
        ownedFaulted=true;ownedEligible=false;
        visible=false;ShowWindow(window,SW_HIDE);if(glassWindow)ShowWindow(glassWindow,SW_HIDE);if(shadow)ShowWindow(shadow,SW_HIDE);
        try{UpdateAccessibleGeometry();}catch(...){}
        // The managed callback persists the fuse before any possible later UI
        // event. No attempt to replace/recreate the device or backend follows.
        Emit(12,0,0,0,0,-3,double(static_cast<uint32_t>(failure)));
        // No new material/property submission after the first fault. The host
        // is hidden; only explicit teardown and read-only diagnostics remain.
    }
    void Emit(int kind,float x=0,float y=0,int screenX=0,int screenY=0,int control=0,double value=0)noexcept
    {
        if(callback&&!destroying){GDPointer event{kind,x,y,screenX,screenY,control,value};callback(user,&event);}
    }
    void EmitPointer(int kind,LPARAM point,bool inside,double value=0)
    {
        POINT screen{GET_X_LPARAM(point),GET_Y_LPARAM(point)};ClientToScreen(window,&screen);
        auto x=(static_cast<float>(GET_X_LPARAM(point))-presentationOffsetX)/Scale();
        auto y=(static_cast<float>(GET_Y_LPARAM(point))-presentationOffsetY)/Scale();
        const int control=inside?Hit(x,y):0;
        Emit(kind,x,y,screen.x,screen.y,input::ControlIdForSurface(inside,control),value);
    }
    int Hit(float x,float y)
    {
        std::lock_guard lock(accessibility->mutex);
        for(auto item=accessibility->items.rbegin();item!=accessibility->items.rend();++item)
            if(x>=item->x&&y>=item->y&&x<item->x+item->width&&y<item->y+item->height)return item->id;
        return 0;
    }
    float EffectiveTension()const noexcept
    {return liquidEnabled&&!frame.solid&&!fallback?surfaceTension:0;}
    bool Inside(float x,float y)const
    {
        auto radius=SurfaceRadius(frame.width,frame.height,frame.radius,EffectiveTension());
        return SurfaceDistance(x,y,frame.width,frame.height,radius,1,EffectiveTension())<=0;
    }
    GDContext Context()const
    {RECT rectangle{};GetWindowRect(window,&rectangle);return {rectangle.left,rectangle.top,frame.width,frame.height,dpi};}
    void UpdateAccessibleGeometry()
    {
        RECT rectangle{};GetWindowRect(window,&rectangle);
        std::lock_guard lock(accessibility->mutex);
        accessibility->rectangle=rectangle;accessibility->offsetX=presentationOffsetX;accessibility->offsetY=presentationOffsetY;accessibility->dpi=dpi;accessibility->visible=visible;
        accessibility->surfaceShape={frame.width,frame.height,frame.radius,EffectiveTension()};
    }
    unsigned CurrentMonitorDpi()const
    {
        RECT rect{};GetWindowRect(window,&rect);
        auto previous=SetThreadDpiAwarenessContext(DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2);
        auto probe=CreateWindowExW(WS_EX_TOOLWINDOW|WS_EX_NOACTIVATE,L"STATIC",L"",WS_POPUP,
            (rect.left+rect.right)/2,(rect.top+rect.bottom)/2,1,1,nullptr,nullptr,GetModuleHandleW(nullptr),nullptr);
        auto value=probe?GetDpiForWindow(probe):GetDpiForWindow(window);
        if(probe)DestroyWindow(probe);if(previous)SetThreadDpiAwarenessContext(previous);
        return value?value:dpi;
    }
    void ApplyDpi(unsigned value,int x,int y)
    {
        if(ownedFaulted&&(materialBackend==2))return;
        const unsigned nextDpi=value?value:96;
        FramePixelGeometry geometry{};
        check_hresult(ComputeFramePixelGeometry(frame,nextDpi,presentationOffsetX,presentationOffsetY,geometry));
        const unsigned previousDpi=dpi;dpi=nextDpi;
        const HRESULT positioned=PositionHostWindows(x,y,geometry.allocationWidth,geometry.allocationHeight,SWP_NOACTIVATE);
        if(FAILED(positioned)){dpi=previousDpi;check_hresult(positioned);}
        if(ownedFaulted&&(materialBackend==2))return;
        Layout();UpdateAccessibleGeometry();Emit(9);
    }
    void ReconcileDpi()
    {
        auto value=CurrentMonitorDpi();RECT rectangle{};GetWindowRect(window,&rectangle);
        FramePixelGeometry expected{};
        if(FAILED(ComputeFramePixelGeometry(frame,value,presentationOffsetX,presentationOffsetY,expected)))return;
        // The resident GetDpiForWindow can lag a display-settings broadcast.
        // Keep the presented DIP frame (including an interrupted animation).
        if(value!=dpi||rectangle.right-rectangle.left!=expected.allocationWidth||rectangle.bottom-rectangle.top!=expected.allocationHeight)
            ApplyDpi(value,rectangle.left,rectangle.top);
    }
    void CreateGraphics()
    {
        // Candidate creation is single-attempt. Foreground surface errors must
        // not silently rebuild a graphics device after the experiment trips.
        if((materialBackend==2)&&(graphicsCreated||ownedFaulted))throw hresult_error(E_ABORT);
        graphicsCreated=true;
        if(liquid){liquid.reset();liquidReady=false;liquidPresented=false;opticalReady=false;liquidSurface=nullptr;liquidBrush=nullptr;
            opticalSurface=nullptr;opticalBrush=nullptr;if(opticalLight)opticalLight.Brush(nullptr);if(glass)glass.Brush(backdrop);}
        {
        images.clear();cacheContext=nullptr;d2d=nullptr;d3dContext=nullptr;d3d=nullptr;
        UINT flags=D3D11_CREATE_DEVICE_BGRA_SUPPORT;D3D_FEATURE_LEVEL level{};
        auto hr=D3D11CreateDevice(nullptr,D3D_DRIVER_TYPE_HARDWARE,nullptr,flags,nullptr,0,D3D11_SDK_VERSION,d3d.put(),&level,d3dContext.put());
        if(FAILED(hr)){if(materialBackend==2)check_hresult(hr);else check_hresult(D3D11CreateDevice(nullptr,D3D_DRIVER_TYPE_WARP,nullptr,flags,nullptr,0,D3D11_SDK_VERSION,d3d.put(),&level,d3dContext.put()));}
        d3dContext.as<ID3D11Multithread>()->SetMultithreadProtected(TRUE);
        if(!d2dFactory)check_hresult(D2D1CreateFactory(D2D1_FACTORY_TYPE_SINGLE_THREADED,__uuidof(ID2D1Factory1),nullptr,d2dFactory.put_void()));
        auto dxgi=d3d.as<IDXGIDevice>();check_hresult(d2dFactory->CreateDevice(dxgi.get(),d2d.put()));
        }
        check_hresult(d2d->CreateDeviceContext(D2D1_DEVICE_CONTEXT_OPTIONS_NONE,cacheContext.put()));
        if(graphics)check_hresult(graphics.as<ABI::Windows::UI::Composition::ICompositionGraphicsDeviceInterop>()->SetRenderingDevice(d2d.get()));
        else check_hresult(compositor.as<ABI::Windows::UI::Composition::ICompositorInterop>()->CreateGraphicsDevice(d2d.get(),reinterpret_cast<ABI::Windows::UI::Composition::ICompositionGraphicsDevice**>(put_abi(graphics))));
        if(!textFactory)check_hresult(DWriteCreateFactory(DWRITE_FACTORY_TYPE_SHARED,__uuidof(IDWriteFactory),reinterpret_cast<::IUnknown**>(textFactory.put())));
        if(!wic)check_hresult(CoCreateInstance(CLSID_WICImagingFactory2,nullptr,CLSCTX_INPROC_SERVER,IID_PPV_ARGS(wic.put())));
        if(shadow&&materialBackend==1){liquid=std::make_unique<LiquidGlass>(window,shadow,d3d.get(),d3dContext.get(),graphics);liquid->SetEnabled(false);}
    }
    static D2D1_COLOR_F Color(unsigned argb)
    {return D2D1::ColorF(((argb>>16)&255)/255.0f,((argb>>8)&255)/255.0f,(argb&255)/255.0f,((argb>>24)&255)/255.0f);}
    CompositionDrawingSurface CreateSurface(int width,int height)
    {return graphics.CreateDrawingSurface({float(width),float(height)},DirectXPixelFormat::B8G8R8A8UIntNormalized,DirectXAlphaMode::Premultiplied);}
    template<typename Painter> void Draw(CompositionDrawingSurface const& surface,float targetDpi,Painter paint)
    {
        auto interop=surface.as<ABI::Windows::UI::Composition::ICompositionDrawingSurfaceInterop>();
        com_ptr<ID2D1DeviceContext> context;POINT offset{};
        check_hresult(interop->BeginDraw(nullptr,__uuidof(ID2D1DeviceContext),context.put_void(),&offset));
        try
        {
            context->SetDpi(targetDpi,targetDpi);
            context->SetTransform(D2D1::Matrix3x2F::Translation(offset.x*96.0f/targetDpi,offset.y*96.0f/targetDpi));
            context->SetTextAntialiasMode(D2D1_TEXT_ANTIALIAS_MODE_GRAYSCALE);
            context->Clear(D2D1::ColorF(0,0,0,0));paint(context.get());
        }
        catch(...){interop->EndDraw();throw;}
        check_hresult(interop->EndDraw());
    }
    void CreateGrain()
    {
        grainSurface=CreateSurface(64,64);
        Draw(grainSurface,96,[&](ID2D1DeviceContext* context)
        {
            // The opaque, fine-grained tile contributes less than 0.2% of
            // luminance variation after compositing. It must not compete
            // with small text or reveal the repeating texture.
            com_ptr<ID2D1SolidColorBrush> brush;check_hresult(context->CreateSolidColorBrush(D2D1::ColorF(.5f,.5f,.5f,1),brush.put()));
            unsigned random=0x91E10DA5;
            context->SetAntialiasMode(D2D1_ANTIALIAS_MODE_ALIASED);
            for(int y=0;y<64;++y)for(int x=0;x<64;++x)
            {random=random*1664525+1013904223;float gray=.42f+.16f*(random>>24)/255.0f;brush->SetColor(D2D1::ColorF(gray,gray,gray,1));context->FillRectangle(D2D1::RectF(float(x),float(y),float(x+1),float(y+1)),brush.get());}
        });
        auto effect=make_self<MaterialEffect>();effect->grain=true;effect->name=L"GlassGrain";effect->source=CompositionEffectSourceParameter(L"Texture");
        auto brush=compositor.CreateEffectFactory(effect.as<IGraphicsEffect>()).CreateBrush();
        auto texture=compositor.CreateSurfaceBrush(grainSurface);texture.Stretch(CompositionStretch::None);
        brush.SetSourceParameter(L"Texture",texture);grain.Brush(brush);grain.Opacity(.006f);
    }
    void Initialize(HWND hwnd)
    {
        window=hwnd;dpi=GetDpiForWindow(hwnd);if(!dpi)dpi=96;
        glassWindow=CreateWindowExW(WS_EX_TOOLWINDOW|WS_EX_NOACTIVATE|WS_EX_TOPMOST|WS_EX_NOREDIRECTIONBITMAP|WS_EX_TRANSPARENT,
            GlassWindowClass,L"GlassDesk Liquid Glass",WS_POPUP,0,0,1,1,nullptr,nullptr,GetModuleHandleW(nullptr),this);
        check_bool(glassWindow!=nullptr);
        // The owned-image backend never requests or creates a system backdrop.
        if(materialBackend==1){
            BOOL enabled=TRUE;auto backdropResult=DwmSetWindowAttribute(glassWindow,DWMWA_USE_HOSTBACKDROPBRUSH,&enabled,sizeof(enabled));
            MARGINS margins{-1,-1,-1,-1};auto frameResult=DwmExtendFrameIntoClientArea(glassWindow,&margins);
            fallback=FAILED(backdropResult)||FAILED(frameResult);
        }
        {
            compositor=Compositor();
            check_hresult(compositor.as<ABI::Windows::UI::Composition::Desktop::ICompositorDesktopInterop>()->CreateDesktopWindowTarget(hwnd,TRUE,reinterpret_cast<ABI::Windows::UI::Composition::Desktop::IDesktopWindowTarget**>(put_abi(target))));
            check_hresult(compositor.as<ABI::Windows::UI::Composition::Desktop::ICompositorDesktopInterop>()->CreateDesktopWindowTarget(glassWindow,TRUE,reinterpret_cast<ABI::Windows::UI::Composition::Desktop::IDesktopWindowTarget**>(put_abi(glassTarget))));
        }
        root=compositor.CreateContainerVisual();root.RelativeSizeAdjustment(winrt::Windows::Foundation::Numerics::float2{1,1});
        glassRoot=compositor.CreateContainerVisual();glassRoot.RelativeSizeAdjustment(winrt::Windows::Foundation::Numerics::float2{1,1});
        target.Root(root);
        glassTarget.Root(glassRoot);
        tensionGeometry=compositor.CreatePathGeometry();
        clipGeometry=compositor.CreateRoundedRectangleGeometry();root.Clip(compositor.CreateGeometricClip(clipGeometry));glassRoot.Clip(compositor.CreateGeometricClip(clipGeometry));
        glass=compositor.CreateSpriteVisual();glassRoot.Children().InsertAtTop(glass);
        clearEdge=compositor.CreateSpriteVisual();clearEdge.IsVisible(false);
        clearEdgeGeometry=compositor.CreatePathGeometry();clearEdge.Clip(compositor.CreateGeometricClip(clearEdgeGeometry));
        // Both backdrop brushes must sample behind the same HWND. Putting the
        // edge on the content HWND can sample the glass and shadow underneath
        // a second time as other windows move behind the widget.
        glassRoot.Children().InsertAtTop(clearEdge);
        if(materialBackend==1){
        auto effect=make_self<MaterialEffect>();effect->source=CompositionEffectSourceParameter(L"Backdrop");
        backdrop=compositor.CreateEffectFactory(effect.as<IGraphicsEffect>(),{L"GlassBlur.BlurAmount"}).CreateBrush();
        backdrop.SetSourceParameter(L"Backdrop",compositor.CreateHostBackdropBrush());
        glass.Brush(backdrop);
        // A low-blur meniscus around the frosted body supplies optical depth
        // without screen capture, duplicated wallpaper, or a painted white rim.
        auto edgeEffect=make_self<MaterialEffect>();edgeEffect->name=L"GlassEdgeBlur";
        edgeEffect->source=CompositionEffectSourceParameter(L"Backdrop");
        clearBackdrop=compositor.CreateEffectFactory(edgeEffect.as<IGraphicsEffect>(),{L"GlassEdgeBlur.BlurAmount"}).CreateBrush();
        clearBackdrop.SetSourceParameter(L"Backdrop",compositor.CreateHostBackdropBrush());
        clearEdge.Brush(clearBackdrop);
        }
        // Capture material outputs stay on independent GPU surfaces so the
        // base, lens, and tint can be tuned without flattening them together.
        liquidOverlay=compositor.CreateSpriteVisual();liquidOverlay.Opacity(0);
        liquidOverlay.IsVisible(false);glassRoot.Children().InsertAtTop(liquidOverlay);
        liquidRefractionOverlay=compositor.CreateSpriteVisual();liquidRefractionOverlay.IsVisible(false);
        glassRoot.Children().InsertAtTop(liquidRefractionOverlay);
        // The real host backdrop supplies the material color and blur. Keep
        // its tint to a single veil at the caller's exact opacity; edge light
        // belongs to the thin border rather than another white shell layer.
        tintBrush=compositor.CreateLinearGradientBrush();tintBrush.MappingMode(CompositionMappingMode::Relative);
        tintBrush.StartPoint({0,0});tintBrush.EndPoint({0,1});
        tintBrush.ColorStops().Append(compositor.CreateColorGradientStop(0,{0,255,255,255}));
        tintBrush.ColorStops().Append(compositor.CreateColorGradientStop(.45f,{0,250,251,252}));
        tintBrush.ColorStops().Append(compositor.CreateColorGradientStop(1,{0,246,248,250}));
        solidTintBrush=compositor.CreateColorBrush();solidBorderBrush=compositor.CreateColorBrush();
        tint=compositor.CreateSpriteVisual();tint.Brush(tintBrush);glassRoot.Children().InsertAtTop(tint);
        if(materialBackend==1)
        {
            fallbackLayers=compositor.CreateContainerVisual();
            fallbackLayers.RelativeSizeAdjustment(winrt::Windows::Foundation::Numerics::float2{1,1});
            fallbackLayers.IsVisible(false);
            glassRoot.Children().InsertAtTop(fallbackLayers);
            auto makeBlur=[&](wchar_t const* name)
            {
                auto effect=make_self<MaterialEffect>();effect->name=name;
                effect->source=CompositionEffectSourceParameter(L"Backdrop");
                auto brush=compositor.CreateEffectFactory(effect.as<IGraphicsEffect>(),
                    {hstring(name)+L".BlurAmount"}).CreateBrush();
                brush.SetSourceParameter(L"Backdrop",compositor.CreateHostBackdropBrush());
                return brush;
            };
            fallbackBlurL2=makeBlur(L"LayerBlur2");
            fallbackBlurL3=makeBlur(L"LayerBlur3");
            fallbackRimBrush=compositor.CreateLinearGradientBrush();
            fallbackRimBrush.MappingMode(CompositionMappingMode::Relative);
            fallbackRimBrush.StartPoint({0,0});fallbackRimBrush.EndPoint({1,1});
            fallbackRimBrush.ColorStops().Append(compositor.CreateColorGradientStop(0,{140,255,255,255}));
            fallbackRimBrush.ColorStops().Append(compositor.CreateColorGradientStop(.5f,{36,255,255,255}));
            fallbackRimBrush.ColorStops().Append(compositor.CreateColorGradientStop(1,{26,255,255,255}));
        }
        grain=compositor.CreateSpriteVisual();glassRoot.Children().InsertAtTop(grain);
        // One optical edge for BOTH backdrop modes. No bitmap, extra blur,
        // capture or idle animation: only retained compositor geometry. The
        // center is empty, so material light cannot wash out content.
        auto edgeBrush=[&](std::initializer_list<std::pair<float,winrt::Windows::UI::Color>> stops)
        {
            auto brush=compositor.CreateLinearGradientBrush();brush.MappingMode(CompositionMappingMode::Absolute);
            brush.StartPoint({0,0});brush.EndPoint({1,1});
            for(auto const& stop:stops)brush.ColorStops().Append(compositor.CreateColorGradientStop(stop.first,stop.second));
            return brush;
        };
        bevel=compositor.CreateShapeVisual();
        shoulderGeometry=compositor.CreateRoundedRectangleGeometry();shoulderShape=compositor.CreateSpriteShape(shoulderGeometry);
        shoulderBrush=edgeBrush({{0,{38,255,255,255}},{.32f,{10,255,255,255}},{.52f,{0,255,255,255}},{1,{12,36,43,52}}});
        shoulderShape.StrokeBrush(shoulderBrush);bevel.Shapes().Append(shoulderShape);
        meniscusGeometry=compositor.CreateRoundedRectangleGeometry();meniscusShape=compositor.CreateSpriteShape(meniscusGeometry);
        meniscusBrush=edgeBrush({{0,{12,36,43,52}},{.42f,{0,36,43,52}},{.68f,{0,255,255,255}},{1,{34,255,255,255}}});
        meniscusShape.StrokeBrush(meniscusBrush);bevel.Shapes().Append(meniscusShape);
        root.Children().InsertAtTop(bevel);
        opticalLight=compositor.CreateSpriteVisual();opticalLight.IsVisible(false);root.Children().InsertAtTop(opticalLight);
        border=compositor.CreateShapeVisual();borderGeometry=compositor.CreateRoundedRectangleGeometry();borderShape=compositor.CreateSpriteShape(borderGeometry);
        borderBrush=edgeBrush({{0,{148,255,255,255}},{.22f,{64,255,255,255}},{.5f,{20,255,255,255}},{.72f,{18,36,43,52}},{1,{40,36,43,52}}});
        borderShape.StrokeBrush(borderBrush);border.Shapes().Append(borderShape);root.Children().InsertAtTop(border);
        content=compositor.CreateContainerVisual();root.Children().InsertAtTop(content);
        try{CreateGraphics();if(materialBackend==1)CreateGrain();}catch(...){if(materialBackend==2)throw;fallback=true;}
        shadow=CreateWindowExW(WS_EX_TOOLWINDOW|WS_EX_NOACTIVATE|WS_EX_TOPMOST|WS_EX_TRANSPARENT|WS_EX_LAYERED,
            ShadowClass,L"GlassDesk Shadow",WS_POPUP,0,0,1,1,nullptr,nullptr,GetModuleHandleW(nullptr),this);
        check_bool(shadow!=nullptr);
        if(d3d&&graphics&&(materialBackend==1))liquid=std::make_unique<LiquidGlass>(window,shadow,d3d.get(),d3dContext.get(),graphics);
        provider=MakeElement(accessibility,0,true);
        {std::lock_guard lock(accessibility->mutex);accessibility->hwnd=hwnd;accessibility->root=static_cast<IRawElementProviderFragment*>(provider.Get());}
        FramePixelGeometry initialGeometry{};
        check_hresult(ComputeFramePixelGeometry(frame,dpi,presentationOffsetX,presentationOffsetY,initialGeometry));
        check_hresult(PositionHostWindows(0,0,initialGeometry.allocationWidth,initialGeometry.allocationHeight,SWP_NOMOVE));
        check_hresult(WindowCallResult(SetWindowPos(window,HWND_TOPMOST,0,0,0,0,SWP_NOMOVE|SWP_NOSIZE|SWP_NOACTIVATE|SWP_NOOWNERZORDER)));
        check_hresult(WindowCallResult(SetWindowPos(glassWindow,window,0,0,0,0,SWP_NOMOVE|SWP_NOSIZE|SWP_NOACTIVATE|SWP_NOOWNERZORDER)));
        if(ownedFaulted&&(materialBackend==2))throw_hresult(E_ABORT);
        Layout();UpdateAccessibleGeometry();
        if(authorizedIncidentShortTest)check_bool(SetTimer(window,89,authorizedIncidentShortTest?100:500,nullptr)!=0);
        if(authorizedIncidentShortTest)check_bool(SetTimer(window,AuthorizedShortTestTimer,60000,nullptr)!=0);
    }
    void ApplyMaterial()
    {
        if(!tint||ownedFaulted&&(materialBackend==2))return;
        const bool previousMaterialApplied=materialApplied;
        const bool previousSolid=materialSolid;
        const bool previousCapture=materialLiquidCapture;
        if(materialBackend!=1){
            bool active=materialBackend==2&&!ownedFaulted&&ownedImageSet&&ownedEligible&&liquidEnabled&&!frame.solid&&ownedSurface!=nullptr;
            glass.IsVisible(active);clearEdge.IsVisible(false);liquidOverlay.IsVisible(false);
            bevel.IsVisible(false);opticalLight.IsVisible(false);grain.IsVisible(false);border.IsVisible(false);
            auto windowColor=frame.solid?GetSysColor(COLOR_WINDOW):RGB(245,246,248);
            solidTintBrush.Color({255,GetRValue(windowColor),GetGValue(windowColor),GetBValue(windowColor)});tint.Brush(solidTintBrush);tint.IsVisible(!active);
            if(frame.solid){auto edge=GetSysColor(COLOR_WINDOWFRAME);solidBorderBrush.Color({255,GetRValue(edge),GetGValue(edge),GetBValue(edge)});borderShape.StrokeBrush(solidBorderBrush);border.IsVisible(true);}
            return;
        }
        // Do not replace the legacy backdrop until a frame has actually been
        // presented and its swap chain is attached to the Composition visual.
        // Merely constructing the capture object can precede borderless access
        // and the first WGC frame by several timer ticks.
        bool captureActive=liquidCaptureCandidate&&liquidEnabled&&liquidCapture!=nullptr&&
            liquidCapturePresented&&attachedLiquidSwapChain!=nullptr&&
            SUCCEEDED(liquidCaptureFailure)&&!liquidCaptureFuseTripped&&visible&&!frame.solid;
        bool solid=(frame.solid||fallback)&&!captureActive;
        bool grainVisible=!solid&&grainSurface!=nullptr;
        if(!materialApplied||solid!=materialSolid||captureActive!=materialLiquidCapture)
        {
            if(liquidCaptureCandidate){
                if(std::abs(EffectiveTension())>.0001f)glassRoot.Clip(compositor.CreateGeometricClip(tensionGeometry));
                else glassRoot.Clip(compositor.CreateGeometricClip(clipGeometry));
            }
            glass.IsVisible(!solid&&!captureActive&&!liquidPresented);
            clearEdge.IsVisible(!solid&&!captureActive&&liquidEnabled);
            bevel.IsVisible(!solid&&!captureActive&&!opticalReady);opticalLight.IsVisible(!solid&&!captureActive&&opticalReady);
            liquidOverlay.IsVisible(!solid&&(captureActive||liquidPresented));liquidOverlay.Opacity(captureActive?1.0f:0.0f);
            liquidRefractionOverlay.IsVisible(false);
            tint.IsVisible(!solid&&!captureActive);
            // The capture shader owns both diagonal highlights on its SDF.
            // A second rounded-rectangle stroke would disagree under strain.
            if(liquidCaptureCandidate)border.IsVisible(!captureActive);
            tint.Brush(solid?solidTintBrush.as<CompositionBrush>():captureActive&&liquidTintCaptureBrush
                ?liquidTintCaptureBrush.as<CompositionBrush>():tintBrush.as<CompositionBrush>());
            borderShape.StrokeBrush(solid?solidBorderBrush.as<CompositionBrush>():borderBrush.as<CompositionBrush>());
        }
        if(!materialApplied||grainVisible!=materialGrainVisible||captureActive!=materialLiquidCapture)grain.IsVisible(grainVisible&&!captureActive);
        if(solid)
        {
            auto rgb=GetSysColor(COLOR_WINDOW);
            auto edge=GetSysColor(COLOR_WINDOWFRAME);
            if(rgb!=materialWindowColor)
            {solidTintBrush.Color({255,GetRValue(rgb),GetGValue(rgb),GetBValue(rgb)});materialWindowColor=rgb;}
            if(edge!=materialBorderColor)
            {solidBorderBrush.Color({255,GetRValue(edge),GetGValue(edge),GetBValue(edge)});materialBorderColor=edge;}
        }
        else
        {
            auto opacity=std::clamp(frame.tint,0.0f,1.0f);
            if(opacity!=materialTint)
            {
                auto alpha=[](float value){return static_cast<unsigned char>(std::lround(std::clamp(value,0.0f,1.0f)*255));};
                // Do not silently increase white coverage above TintOpacity:
                // wallpaper hues must remain visible through the frosted shell.
                auto tintAlpha=alpha(opacity);
                tintBrush.ColorStops().GetAt(0).Color({tintAlpha,255,255,255});
                tintBrush.ColorStops().GetAt(1).Color({tintAlpha,250,251,252});
                tintBrush.ColorStops().GetAt(2).Color({tintAlpha,246,248,250});
                materialTint=opacity;
            }
            auto blur=std::max(0.0f,frame.blur)*Scale();
            if(blur!=materialBlur)
            {backdrop.Properties().InsertScalar(L"GlassBlur.BlurAmount",blur);clearBackdrop.Properties().InsertScalar(L"GlassEdgeBlur.BlurAmount",std::min(frame.blur,2.5f)*Scale());materialBlur=blur;}
        }
        materialSolid=solid;materialGrainVisible=grainVisible;materialLiquidCapture=captureActive;materialApplied=true;
        UpdateFallbackLayers();
        if(!previousMaterialApplied||previousSolid!=solid||previousCapture!=captureActive)
        {
            const wchar_t* state=captureActive?L"active":solid?L"solid":L"fallback";
            const wchar_t* reason=captureActive?L"none":frame.solid?L"solid-requested":fallback?L"graphics-fallback":
                !liquidEnabled?L"disabled":!visible?L"host-hidden":!liquidCapturePresented?L"waiting-for-capture":L"capture-inactive";
            wchar_t fields[640]{};
            swprintf_s(fields,std::size(fields),L"stage=material-transition backend=%u captureCandidate=%u capturedPresented=%u materialState=%ls fallbackReason=%ls activeShaderVersion=0.3 geometrySequence=%llu logicalDip=%.2fx%.2f activePx=%.1fx%.1f dpi=%u theme=%d themeBlend=%.3f solid=%u lensEnabled=%u B=28 D=12 sigma=%.2f split=%.4f",
                materialBackend,liquidCaptureCandidate?1u:0u,liquidCapturePresented?1u:0u,state,reason,
                static_cast<unsigned long long>(glassSceneSequence),frame.width,frame.height,
                frame.width*Scale(),frame.height*Scale(),dpi,liquidSettings.theme,
                liquidCapture?liquidCapture->DarkThemeWeight():0.0f,solid?1u:0u,
                liquidSettings.lensEnabled?1u:0u,frame.blur,liquidSettings.dispersion);
            liquidglass::diagnostics::Log(L"material-transition",fields);
        }
    }
private:
    bool FallbackDarkTheme()const noexcept
    {
        if(liquidSettings.theme==1)return true;
        if(liquidSettings.theme==0)return false;
        const ULONGLONG now=GetTickCount64();
        if(fallbackDarkCheckedMs!=std::numeric_limits<ULONGLONG>::max()&&
            now-fallbackDarkCheckedMs<1000)return fallbackDarkCached;
        try{
            winrt::Windows::UI::ViewManagement::UISettings settings;
            auto c=settings.GetColorValue(winrt::Windows::UI::ViewManagement::UIColorType::Background);
            fallbackDarkCached=.299f*c.R+.587f*c.G+.114f*c.B<127.5f;
            fallbackDarkCheckedMs=now;
            return fallbackDarkCached;
        }catch(...){}
        COLORREF b=GetSysColor(COLOR_WINDOW);
        fallbackDarkCached=(.299f*GetRValue(b)+.587f*GetGValue(b)+.114f*GetBValue(b))<127.5f;
        fallbackDarkCheckedMs=now;
        return fallbackDarkCached;
    }
public:
    void UpdateFallbackLayers()
    {
        if(!fallbackLayers)return;
        const bool show=materialBackend==1&&materialApplied&&!materialSolid&&!materialLiquidCapture&&visible&&!glassSceneShapes.empty();
        fallbackLayers.IsVisible(show);if(!show)return;
        const bool dark=FallbackDarkTheme();
        const float s=Scale();
        uint64_t key=14695981039346656037ull;
        auto hashField=[&](auto const& value)
        {
            unsigned char bytes[sizeof(value)]{};
            std::memcpy(bytes,&value,sizeof(value));
            for(unsigned char byte:bytes){key^=byte;key*=1099511628211ull;}
        };
        hashField(s);hashField(frame.blur);hashField(dark);hashField(glassSceneSequence);
        for(auto const& shape:glassSceneShapes)
        {
            hashField(shape.id);hashField(shape.layer);hashField(shape.xDip);hashField(shape.yDip);
            hashField(shape.widthDip);hashField(shape.heightDip);hashField(shape.radiusDip);
            hashField(shape.materialize);hashField(shape.press);hashField(shape.enabled);
        }
        if(key==fallbackKey)return;
        const float panelSigma=std::max(0.0f,frame.blur)*s;
        const float extra2=(dark?7.0f:10.0f)*s, extra3=(dark?4.0f:6.0f)*s;
        const float sigma2=std::sqrt(panelSigma*panelSigma+extra2*extra2);
        const float sigma3=std::sqrt(sigma2*sigma2+extra3*extra3);
        fallbackBlurL2.Properties().InsertScalar(L"LayerBlur2.BlurAmount",std::min(sigma2,250.0f));
        fallbackBlurL3.Properties().InsertScalar(L"LayerBlur3.BlurAmount",std::min(sigma3,250.0f));

        std::vector<GDGlassShapeV1 const*> selected;selected.reserve(16);
        for(uint32_t layer:{2u,3u})
        {
            for(auto const& shape:glassSceneShapes)
            {
                if(shape.layer!=layer||shape.enabled==0||shape.materialize<=0||shape.widthDip<8||shape.heightDip<8)continue;
                if(selected.size()>=16)break;
                selected.push_back(&shape);
            }
            if(selected.size()>=16)break;
        }
        fallbackLayers.Children().RemoveAll();
        std::vector<uint32_t> activeIds;activeIds.reserve(selected.size());
        for(auto const* shape:selected)
        {
            auto found=std::find_if(fallbackLayerPool.begin(),fallbackLayerPool.end(),
                [&](FallbackLayer const& item){return item.id==shape->id;});
            if(found==fallbackLayerPool.end())
            {
                FallbackLayer item{};item.id=shape->id;
                item.body=compositor.CreateSpriteVisual();
                item.clipShape=compositor.CreateRoundedRectangleGeometry();
                item.body.Clip(compositor.CreateGeometricClip(item.clipShape));
                item.veilBrush=compositor.CreateColorBrush();
                item.veil=compositor.CreateSpriteVisual();item.veil.Brush(item.veilBrush);
                item.veil.RelativeSizeAdjustment({1,1});item.body.Children().InsertAtTop(item.veil);
                item.rim=compositor.CreateShapeVisual();item.rim.RelativeSizeAdjustment({1,1});
                item.rimShape=compositor.CreateRoundedRectangleGeometry();
                item.rimSprite=compositor.CreateSpriteShape(item.rimShape);
                item.rimSprite.StrokeBrush(fallbackRimBrush);
                item.rim.Shapes().Append(item.rimSprite);item.body.Children().InsertAtTop(item.rim);
                fallbackLayerPool.emplace_back(std::move(item));
                found=std::prev(fallbackLayerPool.end());
            }
            const float x=shape->xDip*s,y=shape->yDip*s,w=shape->widthDip*s,h=shape->heightDip*s;
            const float r=std::clamp(shape->radiusDip*s,0.0f,std::min(w,h)*.5f);
            found->body.Offset({x,y,0});found->body.Size({w,h});
            found->body.Brush(shape->layer==3?fallbackBlurL3:fallbackBlurL2);
            found->body.Opacity(std::clamp(shape->materialize,0.0f,1.0f));
            found->clipShape.Size({w,h});found->clipShape.CornerRadius({r,r});
            const float t=std::max(1.0f,s);
            found->rimShape.Offset({t*.5f,t*.5f});
            found->rimShape.Size({std::max(0.0f,w-t),std::max(0.0f,h-t)});
            found->rimShape.CornerRadius({std::max(0.0f,r-t*.5f),std::max(0.0f,r-t*.5f)});
            found->rimSprite.StrokeThickness(t);
            found->rim.Opacity(dark?.55f:1.0f);
            unsigned char veilAlpha=dark?(shape->layer==3?26:15):(shape->layer==3?64:46);
            veilAlpha=static_cast<unsigned char>(std::min(255.0f,float(veilAlpha)+shape->press*20.0f));
            found->veilBrush.Color({veilAlpha,255,255,255});
            activeIds.push_back(shape->id);
        }
        fallbackLayerPool.erase(std::remove_if(fallbackLayerPool.begin(),fallbackLayerPool.end(),
            [&](FallbackLayer const& item){return std::find(activeIds.begin(),activeIds.end(),item.id)==activeIds.end();}),fallbackLayerPool.end());
        for(auto const* shape:selected)
        {
            auto found=std::find_if(fallbackLayerPool.begin(),fallbackLayerPool.end(),
                [&](FallbackLayer const& item){return item.id==shape->id;});
            if(found!=fallbackLayerPool.end())fallbackLayers.Children().InsertAtTop(found->body);
        }
        fallbackKey=key;
    }
    std::vector<GDPoint> TensionContour(float width,float height,float radius,float strain,float inset=0)
    {
        std::vector<GDPoint> points;points.reserve(112);
        constexpr float pi=3.14159265358979323846f;
        float cornerRadius=std::max(0.0f,radius-inset);
        float centers[4][2]={{width-inset-cornerRadius,inset+cornerRadius},{width-inset-cornerRadius,height-inset-cornerRadius},{inset+cornerRadius,height-inset-cornerRadius},{inset+cornerRadius,inset+cornerRadius}};
        auto append=[&](float x,float y){float factor=TensionFactor(y,width,height,Scale(),strain);points.push_back({width*.5f+(x-width*.5f)*factor,y});};
        for(int corner=0;corner<4;++corner)
        {
            float angle=-pi*.5f+corner*pi*.5f;
            for(int step=0;step<16;++step){float a=angle+pi*.5f*step/16;append(centers[corner][0]+cornerRadius*std::cos(a),centers[corner][1]+cornerRadius*std::sin(a));}
            float a=angle+pi*.5f;int next=(corner+1)%4;
            float x=centers[corner][0]+cornerRadius*std::cos(a),y=centers[corner][1]+cornerRadius*std::sin(a);
            float nx=centers[next][0]+cornerRadius*std::cos(a),ny=centers[next][1]+cornerRadius*std::sin(a);
            for(int step=0;step<12;++step){float t=step/12.0f;append(x+(nx-x)*t,y+(ny-y)*t);}
        }
        return points;
    }
    void UpdateClearEdge(float width,float height,float radius,float strain)
    {
        if(!geometryFactory)check_hresult(D2D1CreateFactory(D2D1_FACTORY_TYPE_MULTI_THREADED,geometryFactory.put()));
        float inset=std::min({6*Scale(),width*.12f,height*.12f});
        auto outer=RoundedPolygon(geometryFactory.get(),TensionContour(width,height,radius,strain),.2f*Scale());
        auto inner=RoundedPolygon(geometryFactory.get(),TensionContour(width,height,radius,strain,inset),.2f*Scale());
        com_ptr<ID2D1PathGeometry> ring;check_hresult(geometryFactory->CreatePathGeometry(ring.put()));
        com_ptr<ID2D1GeometrySink> sink;check_hresult(ring->Open(sink.put()));
        check_hresult(outer->CombineWithGeometry(inner.get(),D2D1_COMBINE_MODE_EXCLUDE,nullptr,sink.get()));check_hresult(sink->Close());
        auto source=make<RetainedGeometrySource>(ring.as<ID2D1Geometry>());
        clearEdgeGeometry.Path(CompositionPath(source));
    }
    void Layout(bool submitSurfaces=true)
    {
        if(!root||deferFrameSurfaceUpdates||ownedFaulted&&(materialBackend==2))return;
        RECT client{};check_hresult(WindowCallResult(GetClientRect(window,&client)));int width=client.right,height=client.bottom;
        if(false)root.Size({float(width),float(height)});
        float scale=Scale();
        FramePixelGeometry geometry{};
        check_hresult(ComputeFramePixelGeometry(frame,dpi,presentationOffsetX,presentationOffsetY,geometry));
        const float shapeWidth=liquidCaptureCandidate?geometry.shapeWidth:float(width);
        const float shapeHeight=liquidCaptureCandidate?geometry.shapeHeight:float(height);
        root.Offset({presentationOffsetX,presentationOffsetY,0});
        glassRoot.Offset({presentationOffsetX,presentationOffsetY,0});
        float radius=SurfaceRadius(shapeWidth,shapeHeight,frame.radius*scale,EffectiveTension());
        float strain=EffectiveTension();bool deforming=std::abs(strain)>.0001f;
        bool enhanced=materialBackend==1&&liquidEnabled&&!frame.solid&&!fallback;
        if(width!=cachedWidth||height!=cachedHeight||shapeWidth!=cachedShapeWidth||shapeHeight!=cachedShapeHeight||radius!=cachedRadius||dpi!=cachedDpi||strain!=cachedTension||enhanced!=cachedClearEdge)
        {
            auto size=winrt::Windows::Foundation::Numerics::float2{shapeWidth,shapeHeight};
            glass.Size(size);clearEdge.Size(size);liquidOverlay.Size(size);liquidRefractionOverlay.Size(size);tint.Size(size);grain.Size(size);border.Size(size);bevel.Size(size);opticalLight.Size(size);
            // The material surface is an unscaled physical-pixel viewport. Its SDF
            // supplies fractional-size coverage; only HWND allocation is integral.
            if(liquidCaptureCandidate)liquidOverlay.Size({float(width),float(height)});
            clipGeometry.Size(size);clipGeometry.CornerRadius({radius,radius});
            borderGeometry.Offset({scale*.5f,scale*.5f});borderGeometry.Size({std::max(0.0f,shapeWidth-scale),std::max(0.0f,shapeHeight-scale)});
            borderGeometry.CornerRadius({std::max(0.0f,radius-scale*.5f),std::max(0.0f,radius-scale*.5f)});borderShape.StrokeThickness(scale);
            // All insets scale in DIP and follow the live silhouette. Clamp for
            // tiny intermediate frames; no negative size/corner can reach DWM.
            float edgeUnit=std::min(scale,std::min(shapeWidth,shapeHeight)/12.0f);
            auto edgeGeometry=[&](CompositionRoundedRectangleGeometry const& geometry,float inset)
            {
                geometry.Offset({inset,inset});geometry.Size({std::max(0.0f,shapeWidth-inset*2),std::max(0.0f,shapeHeight-inset*2)});
                float innerRadius=std::max(0.0f,radius-inset);geometry.CornerRadius({innerRadius,innerRadius});
            };
            edgeGeometry(shoulderGeometry,edgeUnit*2.75f);shoulderShape.StrokeThickness(edgeUnit*3.5f);
            edgeGeometry(meniscusGeometry,edgeUnit*1.3f);meniscusShape.StrokeThickness(edgeUnit*.7f);
            // Project the same top-left light vector in physical coordinates.
            // Relative diagonal gradients would rotate as a capsule expands.
            float extent=(width*.55f+height)/1.3025f;
            for(auto const& brush:{borderBrush,shoulderBrush,meniscusBrush})brush.EndPoint({extent*.55f,extent});
            content.Size({frame.width,frame.height});content.Scale({scale,scale,1});
            // The visual boundary remains floating point and antialiased.
            // Give it one physical pixel of rounded HWND coverage so integer
            // region quantization does not amputate partially covered pixels.
            // Corners remain outside the HWND region for cross-process input.
            HRGN region{};
            if(deforming)
            {
                if(!geometryFactory)check_hresult(D2D1CreateFactory(D2D1_FACTORY_TYPE_MULTI_THREADED,geometryFactory.put()));
                auto points=TensionContour(shapeWidth,shapeHeight,radius,strain);
                auto source=make<GeometrySource>(geometryFactory.get(),points,.35f*scale);
                tensionGeometry.Path(CompositionPath(source));root.Clip(compositor.CreateGeometricClip(tensionGeometry));glassRoot.Clip(compositor.CreateGeometricClip(tensionGeometry));
                std::vector<POINT> polygon;polygon.reserve(points.size());
                for(auto p:points){float dx=p.x-width*.5f,dy=p.y-height*.5f;float length=std::max(1.0f,std::hypot(dx,dy));polygon.push_back({LONG(std::lround(p.x+dx/length)),LONG(std::lround(p.y+dy/length))});}
                region=CreatePolygonRgn(polygon.data(),static_cast<int>(polygon.size()),WINDING);
                HRGN glassRegion=CreatePolygonRgn(polygon.data(),static_cast<int>(polygon.size()),WINDING);
                if(glassRegion&&!SetWindowRgn(glassWindow,glassRegion,TRUE))DeleteObject(glassRegion);
            }
            else
            {
                root.Clip(compositor.CreateGeometricClip(clipGeometry));
                glassRoot.Clip(compositor.CreateGeometricClip(clipGeometry));
                region=CreateRoundRectRgn(-1,-1,width+2,height+2,int(std::ceil(radius*2)),int(std::ceil(radius*2)));
                HRGN glassRegion=CreateRoundRectRgn(-1,-1,width+2,height+2,int(std::ceil(radius*2)),int(std::ceil(radius*2)));
                if(glassRegion&&!SetWindowRgn(glassWindow,glassRegion,TRUE))DeleteObject(glassRegion);
            }
            // Composition owns the exact fractional contour. The HWND region
            // is only a conservative allocation/input envelope during morphs.
            if(liquidCaptureCandidate){
                if(region)DeleteObject(region);
                region=CreateRectRgn(0,0,width,height);
                HRGN glassRegion=CreateRectRgn(0,0,width,height);
                if(glassRegion&&!SetWindowRgn(glassWindow,glassRegion,TRUE))DeleteObject(glassRegion);
            }
            if(region&&!SetWindowRgn(window,region,TRUE))DeleteObject(region);
            cachedWidth=width;cachedHeight=height;cachedShapeWidth=shapeWidth;cachedShapeHeight=shapeHeight;cachedRadius=radius;cachedDpi=dpi;cachedTension=strain;cachedClearEdge=enhanced;
            if(enhanced&&!liquidCaptureCandidate)UpdateClearEdge(float(width),float(height),radius,strain);
        }
        // DIP animation dimensions may change while rounded physical size is
        // unchanged; the C# visual tree still receives the exact DIP frame.
        // The capture shader leaves a one-pixel color apron, and this shared
        // Composition clip supplies the outer antialiased contour.
        if(materialBackend==2){root.Clip(nullptr);glassRoot.Clip(nullptr);}
        content.Size({frame.width,frame.height});ApplyMaterial();UpdateFallbackLayers();if(submitSurfaces){UpdateShadow();UpdateLiquid();}
    }
    void StopLiquidCapture() noexcept
    {
        if(window)KillTimer(window,LiquidCaptureTimer);
        liquidCaptureRetryStartedMs=0;liquidCaptureRetryDelayMs=16;
        liquidCaptureGeometryRetrying=false;liquidCaptureSurfaceRetrying=false;
        try{if(liquidOverlay){liquidOverlay.StopAnimation(L"Opacity");liquidOverlay.IsVisible(false);liquidOverlay.Opacity(0);liquidOverlay.Brush(nullptr);}}
        catch(...){}
        try{if(liquidRefractionOverlay){liquidRefractionOverlay.IsVisible(false);liquidRefractionOverlay.Brush(nullptr);}}
        catch(...){}
        try{if(tint&&tintBrush)tint.Brush(tintBrush);}
        catch(...){}
        liquidCaptureSurface=nullptr;liquidRefractionSurface=nullptr;liquidTintSurface=nullptr;liquidInkSurface=nullptr;
        liquidCaptureBrush=nullptr;liquidRefractionBrush=nullptr;liquidTintCaptureBrush=nullptr;
        attachedLiquidSwapChain=nullptr;attachedLiquidRefractionSwapChain=nullptr;attachedLiquidTintSwapChain=nullptr;attachedLiquidInkSwapChain=nullptr;
        ++liquidInkSurfaceGeneration;liquidCapturePresented=false;
        if(liquidCapture)liquidCapture->SetEnabled(false);
        liquidCapture.reset();
    }
    void ResetLiquidCaptureRetry() noexcept
    {
        liquidCaptureRetryStartedMs=0;liquidCaptureRetryDelayMs=16;
        liquidCaptureGeometryRetrying=false;liquidCaptureSurfaceRetrying=false;
    }
    bool ScheduleLiquidCaptureRetry(bool geometry,bool surface=false) noexcept
    {
        if(!window)return false;
        const ULONGLONG now=GetTickCount64();
        if(liquidCaptureRetryStartedMs==0)
        {liquidCaptureRetryStartedMs=now;liquidCaptureRetryDelayMs=16;}
        else liquidCaptureRetryDelayMs=std::min<UINT>(liquidCaptureRetryDelayMs*2u,250u);
        liquidCaptureGeometryRetrying=geometry;
        liquidCaptureSurfaceRetrying=surface;
        if(SetTimer(window,LiquidCaptureTimer,liquidCaptureRetryDelayMs,nullptr)!=0)return true;
        ResetLiquidCaptureRetry();return false;
    }
    void AttachLiquidSwapChains(IDXGISwapChain1* baseSwapChain,IDXGISwapChain1* refractionSwapChain,
                                IDXGISwapChain1* tintSwapChain,IDXGISwapChain1* inkSwapChain)
    {
        if(!baseSwapChain||!refractionSwapChain||!tintSwapChain||!inkSwapChain)return;
        if(attachedLiquidSwapChain==baseSwapChain&&attachedLiquidRefractionSwapChain==refractionSwapChain&&
           attachedLiquidTintSwapChain==tintSwapChain&&attachedLiquidInkSwapChain==inkSwapChain)return;
        auto interop=compositor.as<ABI::Windows::UI::Composition::ICompositorInterop>();
        auto createSurface=[&](IDXGISwapChain1* swapChain)
        {
            com_ptr<ABI::Windows::UI::Composition::ICompositionSurface> nativeSurface;
            check_hresult(interop->CreateCompositionSurfaceForSwapChain(swapChain,nativeSurface.put()));
            ICompositionSurface surface{nullptr};copy_from_abi(surface,nativeSurface.get());return surface;
        };
        liquidCaptureSurface=createSurface(baseSwapChain);
        liquidRefractionSurface=createSurface(refractionSwapChain);
        liquidTintSurface=createSurface(tintSwapChain);
        liquidInkSurface=createSurface(inkSwapChain);
        liquidCaptureBrush=compositor.CreateSurfaceBrush(liquidCaptureSurface);liquidCaptureBrush.Stretch(CompositionStretch::None);
        liquidCaptureBrush.HorizontalAlignmentRatio(0);liquidCaptureBrush.VerticalAlignmentRatio(0);
        liquidRefractionBrush=compositor.CreateSurfaceBrush(liquidRefractionSurface);liquidRefractionBrush.Stretch(CompositionStretch::Fill);
        liquidTintCaptureBrush=compositor.CreateSurfaceBrush(liquidTintSurface);liquidTintCaptureBrush.Stretch(CompositionStretch::Fill);
        liquidOverlay.Brush(liquidCaptureBrush);liquidRefractionOverlay.Brush(liquidRefractionBrush);
        attachedLiquidSwapChain=baseSwapChain;attachedLiquidRefractionSwapChain=refractionSwapChain;
        attachedLiquidTintSwapChain=tintSwapChain;attachedLiquidInkSwapChain=inkSwapChain;
        ++liquidInkSurfaceGeneration;
        wchar_t fields[384]{};
        swprintf_s(fields,std::size(fields),L"stage=attach-surface component=CompositionSwapChain backend=WGC captureCandidate=%u capturedPresented=%u materialState=%ls fallbackReason=none activeShaderVersion=0.3 geometrySequence=%llu logicalDip=%.2fx%.2f activePx=%.1fx%.1f dpi=%u outputGeneration=%llu",
            liquidCaptureCandidate?1u:0u,liquidCapture&&liquidCapture->HasPresentedFrame()?1u:0u,
            liquidCapture&&liquidCapture->HasPresentedFrame()?L"active":L"waiting",
            static_cast<unsigned long long>(glassSceneSequence),frame.width,frame.height,
            frame.width*Scale(),frame.height*Scale(),dpi,static_cast<unsigned long long>(liquidInkSurfaceGeneration));
        liquidglass::diagnostics::Log(L"surface-attached",fields);
    }
    HRESULT SynchronizeLiquidCaptureGeometry(liquidglass::CaptureRect& captureRect,RECT& contentBounds,RECT& glassBounds,
        bool& repaired) noexcept
    {
        repaired=false;
        captureRect.shapeWidthPixels=frame.width*Scale();
        captureRect.shapeHeightPixels=frame.height*Scale();
        captureRect.dpiScale=Scale();captureRect.surfaceTension=EffectiveTension();
        if(!GetWindowRect(window,&contentBounds)||!GetWindowRect(glassWindow,&glassBounds))
            return HRESULT_FROM_WIN32(GetLastError());
        if(glassSceneSequence!=appliedGlassSceneSequence)return HRESULT_FROM_WIN32(ERROR_RETRY);
        const unsigned geometryDpi=dpi;
        const GDFrame geometryFrame=frame;
        const float geometryOffsetX=presentationOffsetX,geometryOffsetY=presentationOffsetY;
        const float geometryTension=surfaceTension;
        const uint64_t geometrySequence=appliedGlassSceneSequence;
        const bool geometryEnabled=liquidEnabled,geometrySolid=frame.solid!=0,geometryFallback=fallback;
        FramePixelGeometry expected{};
        HRESULT hr=ComputeFramePixelGeometry(frame,dpi,presentationOffsetX,presentationOffsetY,expected);
        if(FAILED(hr))return hr;
        captureRect.shapeWidthPixels=expected.shapeWidth;captureRect.shapeHeightPixels=expected.shapeHeight;
        captureRect.sequence=appliedGlassSceneSequence;
        captureRect.glassShapes=glassSceneShapes.empty()?nullptr:glassSceneShapes.data();
        captureRect.glassShapeCount=static_cast<uint32_t>(glassSceneShapes.size());
        auto sizeMatches=[&](RECT const& bounds)
        {return bounds.right-bounds.left==expected.allocationWidth&&bounds.bottom-bounds.top==expected.allocationHeight;};
        const bool windowsMatch=contentBounds.left==glassBounds.left&&contentBounds.top==glassBounds.top&&
            contentBounds.right==glassBounds.right&&contentBounds.bottom==glassBounds.bottom;
        if(!windowsMatch||!sizeMatches(contentBounds)||!sizeMatches(glassBounds))
        {
            hr=PositionHostWindows(contentBounds.left,contentBounds.top,expected.allocationWidth,expected.allocationHeight,SWP_NOACTIVATE);
            if(FAILED(hr))return hr;
            repaired=true;
            if(!GetWindowRect(window,&contentBounds)||!GetWindowRect(glassWindow,&glassBounds))
                return HRESULT_FROM_WIN32(GetLastError());
            // Moving between monitors can synchronously dispatch WM_DPICHANGED.
            // Reject that superseded snapshot and recompute before rendering.
            if(dpi!=geometryDpi||frame.width!=geometryFrame.width||frame.height!=geometryFrame.height||
                frame.radius!=geometryFrame.radius||presentationOffsetX!=geometryOffsetX||presentationOffsetY!=geometryOffsetY||
                surfaceTension!=geometryTension||glassSceneSequence!=geometrySequence||
                appliedGlassSceneSequence!=geometrySequence||liquidEnabled!=geometryEnabled||
                (frame.solid!=0)!=geometrySolid||fallback!=geometryFallback)
                return HRESULT_FROM_WIN32(ERROR_RETRY);
            // Rebuild both foreground and glass from the completed allocation.
            try{Layout(false);}catch(...){return to_hresult();}
        }
        const bool synchronized=contentBounds.left==glassBounds.left&&contentBounds.top==glassBounds.top&&
            contentBounds.right==glassBounds.right&&contentBounds.bottom==glassBounds.bottom&&
            sizeMatches(contentBounds)&&sizeMatches(glassBounds);
        if(!synchronized)return HRESULT_FROM_WIN32(ERROR_RETRY);
        captureRect.screenX=float(glassBounds.left)+presentationOffsetX;
        captureRect.screenY=float(glassBounds.top)+presentationOffsetY;
        captureRect.widthPixels=glassBounds.right-glassBounds.left;
        captureRect.heightPixels=glassBounds.bottom-glassBounds.top;
        captureRect.radiusPixels=cachedRadius;
        captureRect.dpiScale=Scale();
        captureRect.surfaceTension=EffectiveTension();
        captureRect.motionActive=captureMotionActive;
        captureRect.sequence=appliedGlassSceneSequence;
        captureRect.glassShapes=glassSceneShapes.empty()?nullptr:glassSceneShapes.data();
        captureRect.glassShapeCount=static_cast<uint32_t>(glassSceneShapes.size());
        return S_OK;
    }
    void LogLiquidCaptureError(HRESULT failure,wchar_t const* stage,liquidglass::CaptureRect const* captureRect,
        RECT const& contentBounds,RECT const& glassBounds,bool geometryRetry) noexcept
    {
        const ULONGLONG now=GetTickCount64();
        if(geometryRetry&&lastGeometryFailureLogMs!=0&&wcscmp(lastGeometryFailureLogStage,stage)==0&&
            lastGeometryFailureLogCode==failure&&now-lastGeometryFailureLogMs<1000)return;
        if(geometryRetry){lastGeometryFailureLogStage=stage;lastGeometryFailureLogCode=failure;lastGeometryFailureLogMs=now;}
        const liquidglass::CaptureDiagnosticSnapshot capture=liquidCapture?liquidCapture->GetDiagnostics():liquidglass::CaptureDiagnosticSnapshot{};
        const float scale=dpi?static_cast<float>(dpi)/96.0f:0.0f;
        const float originX=captureRect?captureRect->screenX:0.0f,originY=captureRect?captureRect->screenY:0.0f;
        const float fractionalX=originX-std::floor(originX),fractionalY=originY-std::floor(originY);
        const unsigned long long sequence=captureRect?static_cast<unsigned long long>(captureRect->sequence):0ull;
        wchar_t fields[1024]{};
        swprintf_s(fields,std::size(fields),
            L"stage=%ls component=liquid-capture hr=0x%08X exceptionType=HRESULT stack=unavailable backend=WGC captureCandidate=%u capturedPresented=%u materialState=%ls fallbackReason=%ls logicalDip=%.2fx%.2f panelPx=%dx%d activePx=%ux%u allocPx=%ux%u capacityPx=%ux%u paddingPx=%d originPx=fractional-only fractionalOffset=%.4f,%.4f monitorSizePx=%ux%u hostWindowPx=%ldx%ld glassWindowPx=%ldx%ld dpi=%u scale=%.4f geometrySequence=%llu captureGeneration=%llu sourceFormatCode=%u sourceSizePx=%ux%u contentSizePx=%ux%u layer=1 shapeId=0 opIndex=unknown opKind=unknown opRect=none opTextRole=none theme=%d lensEnabled=%u B=28 D=12 sigma=%.2f split=%.4f activeShaderVersion=%ls tension=%.5f alphaContract=opaque-linear-intermediate",
            stage?stage:L"unknown",static_cast<unsigned>(failure),liquidCaptureCandidate?1u:0u,
            liquidCapturePresented?1u:0u,liquidCapturePresented?L"active":L"fallback",
            geometryRetry?L"retryable-geometry":L"capture-or-surface-failure",frame.width,frame.height,
            captureRect?captureRect->widthPixels:0,captureRect?captureRect->heightPixels:0,
            capture.activeWidth?capture.activeWidth:static_cast<uint32_t>(captureRect?captureRect->widthPixels:0),
            capture.activeHeight?capture.activeHeight:static_cast<uint32_t>(captureRect?captureRect->heightPixels:0),
            capture.capacityWidth,capture.capacityHeight,capture.capacityWidth,capture.capacityHeight,capture.paddingPixels,
            fractionalX,fractionalY,capture.monitorWidth,capture.monitorHeight,
            contentBounds.right-contentBounds.left,contentBounds.bottom-contentBounds.top,
            glassBounds.right-glassBounds.left,glassBounds.bottom-glassBounds.top,dpi,scale,sequence,
            static_cast<unsigned long long>(capture.captureGeneration),capture.sourceFormat,capture.sourceWidth,capture.sourceHeight,
            capture.contentWidth,capture.contentHeight,liquidSettings.theme,liquidSettings.lensEnabled?1u:0u,
            frame.blur,liquidSettings.dispersion,capture.presented?L"0.3":L"none",EffectiveTension());
        liquidglass::diagnostics::Log(L"capture-error",fields);
    }
    void UpdateLiquidCapture(bool allowImmediateGeometryRetry=true) noexcept
    {
        if(!liquidCaptureCandidate||!window||destroying||deferFrameSurfaceUpdates)return;
        if(!liquidEnabled)
        {
            StopLiquidCapture();liquidCaptureFailure=S_OK;liquidCaptureFuseTripped=false;try{ApplyMaterial();}catch(...){}return;
        }
        if(!visible||frame.solid)
        {StopLiquidCapture();try{ApplyMaterial();}catch(...){}return;}
        if(FAILED(liquidCaptureFailure)){try{ApplyMaterial();}catch(...){}return;}
        liquidglass::CaptureRect rect{};RECT contentBounds{},glassBounds{};bool geometryRepaired{};
        const HRESULT geometry=SynchronizeLiquidCaptureGeometry(rect,contentBounds,glassBounds,geometryRepaired);
        if(FAILED(geometry))
        {
            const bool retry=geometry==E_INVALIDARG||geometry==E_BOUNDS||geometry==HRESULT_FROM_WIN32(ERROR_RETRY);
            LogLiquidCaptureError(geometry,L"synchronize-window-geometry",&rect,contentBounds,glassBounds,retry);
            if(geometry==HRESULT_FROM_WIN32(ERROR_RETRY)&&allowImmediateGeometryRetry)
            {UpdateLiquidCapture(false);return;}
            // Also retry when synchronization failed before a capture existed.
            if(retry&&!ScheduleLiquidCaptureRetry(true))
                LogLiquidCaptureError(WindowCallResult(FALSE),L"schedule-geometry-retry",&rect,contentBounds,glassBounds,false);
            else if(!retry)
            {liquidCaptureFailure=geometry;StopLiquidCapture();}
            try{ApplyMaterial();}catch(...){}
            return;
        }
        wchar_t const* stage=L"create-capture";
        try
        {
            if(!liquidCapture)
            {
                if(!d3d||!d3dContext)throw_hresult(E_FAIL);
                liquidCapture=std::make_unique<liquidglass::LiquidGlassCapture>(glassWindow,window,shadow,true,d3d.get(),d3dContext.get());
                check_hresult(liquidCapture->SetSettings(liquidSettings));
                if(!SetTimer(window,LiquidCaptureTimer,16,nullptr))
                    LogLiquidCaptureError(WindowCallResult(FALSE),L"schedule-capture-retry",&rect,contentBounds,glassBounds,false);
            }
            stage=L"update-capture";
            const HRESULT update=liquidCapture->Update(rect);
            try{UpdateShadow();}catch(...){}
            if(liquidCapture->FuseTripped())
            {liquidCaptureFuseTripped=true;throw_hresult(FAILED(update)?update:E_ABORT);}
            if(FAILED(update))
            {
                if(liquidCapture->IsRecoverableGeometryFailure(update))
                {
                    LogLiquidCaptureError(update,liquidglass::CaptureFailureStageName(liquidCapture->LastFailureStage()),
                        &rect,contentBounds,glassBounds,true);
                    if(!ScheduleLiquidCaptureRetry(true))
                        LogLiquidCaptureError(WindowCallResult(FALSE),L"schedule-geometry-retry",&rect,contentBounds,glassBounds,false);
                    try{ApplyMaterial();}catch(...){}
                    if(allowImmediateGeometryRetry)
                    {
                        bool repaired{};
                        liquidglass::CaptureRect retryRect{};RECT retryContent{},retryGlass{};
                        const HRESULT sync=SynchronizeLiquidCaptureGeometry(retryRect,retryContent,retryGlass,repaired);
                        if(SUCCEEDED(sync)&&repaired)UpdateLiquidCapture(false);
                    }
                    return;
                }
                throw_hresult(update);
            }
            lastGeometryFailureLogMs=0;
            ResetLiquidCaptureRetry();
            auto* swapChain=static_cast<IDXGISwapChain1*>(liquidCapture->OutputSwapChain());
            auto* refractionSwapChain=static_cast<IDXGISwapChain1*>(liquidCapture->RefractionSwapChain());
            auto* tintSwapChain=static_cast<IDXGISwapChain1*>(liquidCapture->TintSwapChain());
            auto* inkSwapChain=static_cast<IDXGISwapChain1*>(liquidCapture->InkColorSwapChain());
            stage=L"attach-swap-chains";
            if(swapChain&&refractionSwapChain&&tintSwapChain&&inkSwapChain)
                AttachLiquidSwapChains(swapChain,refractionSwapChain,tintSwapChain,inkSwapChain);
            liquidCapturePresented=liquidCapture->HasPresentedFrame();
            if(liquidCapturePresented&&(!swapChain||!refractionSwapChain||!tintSwapChain||!inkSwapChain))throw_hresult(E_POINTER);
            stage=L"apply-material";ApplyMaterial();
        }
        catch(...)
        {
            const HRESULT failure=to_hresult();
            if(wcscmp(stage,L"attach-swap-chains")==0)
            {
                LogLiquidCaptureError(failure,stage,&rect,contentBounds,glassBounds,true);
                if(!ScheduleLiquidCaptureRetry(false,true))
                    LogLiquidCaptureError(WindowCallResult(FALSE),L"schedule-surface-retry",&rect,contentBounds,glassBounds,false);
                try{ApplyMaterial();}catch(...){}
                return;
            }
            liquidCaptureFailure=failure;
            if(liquidCapture&&wcscmp(stage,L"update-capture")==0)
                stage=liquidglass::CaptureFailureStageName(liquidCapture->LastFailureStage());
            LogLiquidCaptureError(liquidCaptureFailure,stage,&rect,contentBounds,glassBounds,false);
            StopLiquidCapture();try{ApplyMaterial();}catch(...){}
        }
    }
    void UpdateLiquid()
    {
        if(!window||destroying||ownedFaulted&&(materialBackend==2))return;
        if(materialBackend!=1){KillTimer(window,LiquidTimer);ApplyMaterial();return;}
        if(liquidCaptureCandidate){UpdateLiquidCapture();return;}
        // Keep the legacy system-frost fallback capture-free when no capture
        // candidate was selected for this host.
        KillTimer(window,LiquidTimer);
        if(liquid)liquid->SetEnabled(false);
        liquidOverlay.StopAnimation(L"Opacity");liquidOverlay.IsVisible(false);
        liquidOverlay.Opacity(0);liquidOverlay.Brush(nullptr);
        liquidSurface=nullptr;if(liquidBrush)liquidBrush.Surface(nullptr);
        liquidReady=false;liquidPresented=false;
        bool materialVisible=visible&&!frame.solid&&!fallback;
        glass.IsVisible(materialVisible);clearEdge.IsVisible(materialVisible&&liquidEnabled);
        if(liquid&&materialVisible&&liquidEnabled)
        {
            float radius=cachedRadius;
            auto light=liquid->OpticalSurface(static_cast<unsigned>(std::max(0,cachedWidth)),static_cast<unsigned>(std::max(0,cachedHeight)),radius,Scale(),EffectiveTension());
            if(light!=opticalSurface)
            {
                if(!opticalBrush){opticalBrush=compositor.CreateSurfaceBrush();opticalBrush.Stretch(CompositionStretch::None);}
                opticalBrush.Surface(light);opticalSurface=light;opticalLight.Brush(opticalBrush);
            }
            opticalReady=light!=nullptr;
        }
        opticalLight.IsVisible(materialVisible&&liquidEnabled&&opticalReady);
        bevel.IsVisible(materialVisible&&std::abs(EffectiveTension())<.0001f&&(!liquidEnabled||!opticalReady));
        border.IsVisible(std::abs(EffectiveTension())<.0001f);
        border.Opacity(materialVisible&&liquidEnabled&&opticalReady?.12f:1.0f);
    }
    static float RoundedDistance(float x,float y,float width,float height,float radius)
    {
        float qx=std::abs(x-width/2)-width/2+radius,qy=std::abs(y-height/2)-height/2+radius;
        float px=std::max(qx,0.0f),py=std::max(qy,0.0f);
        return std::sqrt(px*px+py*py)+std::min(std::max(qx,qy),0.0f)-radius;
    }
    HRESULT ReleaseShadowBitmap() noexcept
    {
        HRESULT first=S_OK;
        auto recordFailure=[&]() noexcept {
            const DWORD error=GetLastError();
            if(SUCCEEDED(first))first=HRESULT_FROM_WIN32(error?error:ERROR_GEN_FAILURE);
        };
        // Destroy our private DC before its bitmap: successful DeleteDC removes
        // the selection. If it fails, retain both handles and never delete a
        // possibly selected bitmap. Still release independent retired objects.
        if(shadowDc){if(DeleteDC(shadowDc))shadowDc=nullptr;else recordFailure();}
        if(!shadowDc&&shadowBitmap){
            if(DeleteObject(shadowBitmap)){shadowBitmap=nullptr;shadowBits=nullptr;}
            else recordFailure();
        }
        if(shadowRetiredBitmap){
            if(DeleteObject(shadowRetiredBitmap))shadowRetiredBitmap=nullptr;
            else recordFailure();
        }
        if(!shadowDc&&!shadowBitmap&&!shadowRetiredBitmap){
            shadowBits=nullptr;shadowWidth=shadowHeight=0;
            shadowCapacityWidth=shadowCapacityHeight=0;shadowOpticalProfile=false;
        }
        return first;
    }
    void UpdateOpticalShadow(RECT const& rectangle,int panelWidth,int panelHeight,bool forceOrder)
    {
        // App-generated black alpha only. This DIB never receives desktop pixels.
        // A failed retirement is a first fault, never another allocation/retry.
        if(shadowRetiredBitmap)throw hresult_error(E_UNEXPECTED);
        auto scale=Scale();auto tension=EffectiveTension();
        gd::shadow::RasterProfile profile{};
        check_bool(gd::shadow::MakeRasterProfile(panelWidth/scale,panelHeight/scale,scale,profile));
        int margin=int(profile.marginPixels),width=panelWidth+2*margin,height=panelHeight+2*margin;
        float radius=SurfaceRadius(float(panelWidth),float(panelHeight),frame.radius*scale,tension);
        bool repaint=!shadowOpticalProfile||width!=shadowWidth||height!=shadowHeight
            ||radius!=shadowRadius||dpi!=shadowDpi||tension!=shadowTension;
        int positionX=rectangle.left-margin,positionY=rectangle.top-margin;
        bool moved=positionX!=shadowX||positionY!=shadowY;
        if(!forceOrder&&!repaint&&!moved&&visible==shadowShown)return;

        gd::shadow::CapacityPlan plan{};
        check_bool(gd::shadow::PlanCapacity({shadowCapacityWidth,shadowCapacityHeight},unsigned(width),unsigned(height),plan));
        if(plan.reallocate)
        {
            if(!shadowDc){shadowDc=CreateCompatibleDC(nullptr);check_bool(shadowDc!=nullptr);}
            BITMAPINFO info{};info.bmiHeader.biSize=sizeof(BITMAPINFOHEADER);
            info.bmiHeader.biWidth=LONG(plan.capacity.width);info.bmiHeader.biHeight=-LONG(plan.capacity.height);
            info.bmiHeader.biPlanes=1;info.bmiHeader.biBitCount=32;info.bmiHeader.biCompression=BI_RGB;
            void* nextBits{};auto next=CreateDIBSection(shadowDc,&info,DIB_RGB_COLORS,&nextBits,nullptr,0);
            check_bool(next!=nullptr);
            auto replaced=SelectObject(shadowDc,next);
            if(!replaced||replaced==HGDI_ERROR){
                if(!DeleteObject(next))shadowRetiredBitmap=next;
                throw hresult_error(E_FAIL,L"Shadow bitmap selection failed");
            }
            const auto retired=shadowBitmap;
            // Publish ownership before any fallible retirement. The old bitmap
            // is now detached; retain its handle if deletion fails for teardown.
            shadowBitmap=next;shadowBits=nextBits;
            shadowCapacityWidth=plan.capacity.width;shadowCapacityHeight=plan.capacity.height;
            if(retired&&!DeleteObject(retired)){
                const DWORD error=GetLastError();shadowRetiredBitmap=retired;
                throw hresult_error(HRESULT_FROM_WIN32(error?error:ERROR_GEN_FAILURE));
            }
            repaint=true;
        }
        if(repaint)
        {
            check_bool(gd::shadow::RasterAlpha(static_cast<unsigned*>(shadowBits),shadowCapacityWidth,shadowCapacityHeight,
                unsigned(width),unsigned(height),unsigned(panelWidth),unsigned(panelHeight),profile,radius,scale,tension));
        }
        POINT position{positionX,positionY},source{};SIZE size{width,height};BLENDFUNCTION blend{AC_SRC_OVER,0,255,AC_SRC_ALPHA};
        if(repaint)check_bool(UpdateLayeredWindow(shadow,nullptr,&position,&size,shadowDc,&source,0,&blend,ULW_ALPHA)!=FALSE);
        if(ownedFaulted||destroying)return;
        check_bool(SetWindowPos(shadow,glassWindow?glassWindow:window,position.x,position.y,width,height,SWP_NOACTIVATE|(visible?SWP_SHOWWINDOW:SWP_HIDEWINDOW))!=FALSE);
        if(ownedFaulted||destroying)return;
        shadowWidth=width;shadowHeight=height;shadowRadius=radius;shadowDpi=dpi;shadowTension=tension;
        shadowX=positionX;shadowY=positionY;shadowShown=visible;shadowOpticalProfile=true;
    }
    void UpdateShadow(bool forceOrder=false)
    {
        if(!shadow||destroying||ownedFaulted&&(materialBackend==2))return;
        RECT rectangle{};GetWindowRect(window,&rectangle);
        int panelWidth=rectangle.right-rectangle.left,panelHeight=rectangle.bottom-rectangle.top;
        if(panelWidth<1||panelHeight<1)return;
        auto scale=Scale();int margin=int(std::ceil(24*scale));
        int width=panelWidth+margin*2,height=panelHeight+margin*2;
        FramePixelGeometry geometry{};
        check_hresult(ComputeFramePixelGeometry(frame,dpi,presentationOffsetX,presentationOffsetY,geometry));
        const float shapeWidth=liquidCaptureCandidate?geometry.shapeWidth:float(panelWidth);
        const float shapeHeight=liquidCaptureCandidate?geometry.shapeHeight:float(panelHeight);
        float radius=SurfaceRadius(shapeWidth,shapeHeight,frame.radius*scale,EffectiveTension());
        float darkWeight=0.0f,shadowScale=1.0f;
        const bool glassCssShadow=materialBackend==1&&liquidCaptureCandidate&&liquidEnabled&&liquidCapturePresented;
        if(glassCssShadow)
        {
            if(liquidCapture)
            {
                darkWeight=liquidCapture->DarkThemeWeight();
                shadowScale=liquidCapture->ShadowScale();
            }
            else
            {
                darkWeight=liquidSettings.theme==1?1.0f:liquidSettings.theme==0?0.0f:0.0f;
                if(liquidSettings.theme==2)
                {
                    COLORREF systemBackground=GetSysColor(COLOR_WINDOW);
                    float luminance=(.299f*GetRValue(systemBackground)+.587f*GetGValue(systemBackground)+.114f*GetBValue(systemBackground))/255.0f;
                    darkWeight=luminance<.5f?1.0f:0.0f;
                }
                shadowScale=darkWeight>.5f?liquidSettings.dark.shadow:liquidSettings.light.shadow;
            }
        }
        bool repaint=shadowShapeWidth!=shapeWidth||shadowShapeHeight!=shapeHeight||shadowOffsetX!=presentationOffsetX||shadowOffsetY!=presentationOffsetY
            ||shadowOpticalProfile||width!=shadowWidth||height!=shadowHeight||radius!=shadowRadius||dpi!=shadowDpi||EffectiveTension()!=shadowTension
            ||shadowScale!=shadowMaterialScale||darkWeight!=shadowThemeWeight||glassCssShadow!=shadowGlassCss;
        int positionX=rectangle.left-margin,positionY=rectangle.top-margin;
        bool moved=positionX!=shadowX||positionY!=shadowY;
        if(!forceOrder&&!repaint&&!moved&&visible==shadowShown)return;
        if(shadowOpticalProfile||width!=shadowWidth||height!=shadowHeight)
        {
            check_hresult(ReleaseShadowBitmap());shadowDc=CreateCompatibleDC(nullptr);check_bool(shadowDc!=nullptr);
            BITMAPINFO info{};info.bmiHeader.biSize=sizeof(BITMAPINFOHEADER);info.bmiHeader.biWidth=width;
            info.bmiHeader.biHeight=-height;info.bmiHeader.biPlanes=1;info.bmiHeader.biBitCount=32;info.bmiHeader.biCompression=BI_RGB;
            shadowBitmap=CreateDIBSection(shadowDc,&info,DIB_RGB_COLORS,&shadowBits,nullptr,0);check_bool(shadowBitmap!=nullptr);
            auto replaced=SelectObject(shadowDc,shadowBitmap);
            if(!replaced||replaced==HGDI_ERROR)throw hresult_error(E_FAIL,L"Shadow bitmap selection failed");
            shadowWidth=width;shadowHeight=height;
            shadowCapacityWidth=unsigned(width);shadowCapacityHeight=unsigned(height);
        }
        if(repaint)
        {
            auto pixels=static_cast<unsigned*>(shadowBits);std::fill(pixels,pixels+size_t(width)*height,0u);
            float sigma=std::lerp(8.0f,7.0f,darkWeight)*scale,offset=4*scale;
            for(int y=0;y<height;++y)for(int x=0;x<width;++x)
            {
                float localX=static_cast<float>(x-margin)+.5f-presentationOffsetX;
                float localY=static_cast<float>(y-margin)+.5f-presentationOffsetY;
                if(glassCssShadow)
                {
                    float edgeDistance=SurfaceDistance(localX,localY,shapeWidth,shapeHeight,radius,scale,EffectiveTension());
                    if(edgeDistance<=0)continue;
                    float shadowDistance=std::max(0.0f,SurfaceDistance(localX,localY-offset,shapeWidth,shapeHeight,radius,scale,EffectiveTension()));
                    // The two exterior glass.css layers: the half-pixel outline
                    // and the offset 16px (light) / 14px (dark) soft shadow.
                    float outline=std::clamp((.12f+(.28f-.12f)*darkWeight)*shadowScale,0.0f,.95f);
                    float soft=std::clamp((.10f+(.22f-.10f)*darkWeight)*shadowScale,0.0f,.95f)
                        *std::exp(-shadowDistance*shadowDistance/(2*sigma*sigma));
                    float outlineCoverage=std::clamp(.5f*scale+.5f-edgeDistance,0.0f,1.0f);
                    float alpha=1.0f-(1.0f-outline*outlineCoverage)*(1.0f-soft);
                    pixels[size_t(y)*width+x]=static_cast<unsigned>(std::lround(alpha*255.0f))<<24; // Premultiplied black, transparent center.
                }
                else
                {
                    if(SurfaceDistance(localX,localY,shapeWidth,shapeHeight,radius,scale,EffectiveTension())<=0)continue;
                    float distance=std::max(0.0f,SurfaceDistance(localX,localY-offset,shapeWidth,shapeHeight,radius,scale,EffectiveTension()));
                    auto alpha=static_cast<unsigned>(std::lround(24*std::exp(-distance*distance/(2*sigma*sigma))));
                    pixels[size_t(y)*width+x]=alpha<<24;
                }
            }
            shadowRadius=radius;shadowDpi=dpi;shadowTension=EffectiveTension();
            shadowShapeWidth=shapeWidth;shadowShapeHeight=shapeHeight;shadowOffsetX=presentationOffsetX;shadowOffsetY=presentationOffsetY;
            shadowOpticalProfile=false;
            shadowMaterialScale=shadowScale;shadowThemeWeight=darkWeight;
            shadowGlassCss=glassCssShadow;
        }
        POINT position{positionX,positionY},source{};SIZE size{width,height};BLENDFUNCTION blend{AC_SRC_OVER,0,255,AC_SRC_ALPHA};
        if(repaint)check_bool(UpdateLayeredWindow(shadow,nullptr,&position,&size,shadowDc,&source,0,&blend,ULW_ALPHA)!=FALSE);
        check_bool(SetWindowPos(shadow,glassWindow?glassWindow:window,position.x,position.y,width,height,SWP_NOACTIVATE|(visible?SWP_SHOWWINDOW:SWP_HIDEWINDOW))!=FALSE);
        shadowX=positionX;shadowY=positionY;shadowShown=visible;
    }
    com_ptr<ID2D1Bitmap1> Decode(GDOp const& op)
    {
        if(!op.image||!op.imageSize||op.imageSize>32u*1024*1024)return nullptr;
        auto found=std::find_if(images.begin(),images.end(),[&](auto const& entry)
        {return entry.bytes.size()==op.imageSize&&std::equal(entry.bytes.begin(),entry.bytes.end(),op.image);});
        if(found!=images.end()){auto bitmap=found->bitmap;images.splice(images.begin(),images,found);return bitmap;}
        com_ptr<IWICStream> stream;check_hresult(wic->CreateStream(stream.put()));
        check_hresult(stream->InitializeFromMemory(const_cast<BYTE*>(op.image),op.imageSize));
        com_ptr<IWICBitmapDecoder> decoder;check_hresult(wic->CreateDecoderFromStream(stream.get(),nullptr,WICDecodeMetadataCacheOnDemand,decoder.put()));
        com_ptr<IWICBitmapFrameDecode> bitmap;check_hresult(decoder->GetFrame(0,bitmap.put()));
        UINT width{},height{};check_hresult(bitmap->GetSize(&width,&height));
        if(!width||!height||width>65535||height>65535)throw hresult_invalid_argument();
        double ratio=std::min(1.0,768.0/std::max(width,height));
        com_ptr<IWICBitmapSource> source=bitmap.as<IWICBitmapSource>();
        if(ratio<1)
        {com_ptr<IWICBitmapScaler> scaler;check_hresult(wic->CreateBitmapScaler(scaler.put()));check_hresult(scaler->Initialize(bitmap.get(),std::max(1u,unsigned(width*ratio)),std::max(1u,unsigned(height*ratio)),WICBitmapInterpolationModeFant));source=scaler.as<IWICBitmapSource>();}
        com_ptr<IWICFormatConverter> converter;check_hresult(wic->CreateFormatConverter(converter.put()));
        check_hresult(converter->Initialize(source.get(),GUID_WICPixelFormat32bppPBGRA,WICBitmapDitherTypeNone,nullptr,0,WICBitmapPaletteTypeCustom));
        com_ptr<ID2D1Bitmap1> result;check_hresult(cacheContext->CreateBitmapFromWicBitmap(converter.get(),nullptr,result.put()));
        images.push_front({std::vector<unsigned char>(op.image,op.image+op.imageSize),result});
        size_t retainedBytes{};for(auto const& image:images)retainedBytes+=image.bytes.size();
        while(images.size()>3||retainedBytes>12u*1024*1024)
        {retainedBytes-=images.back().bytes.size();images.pop_back();}
        return result;
    }
    typography::Engine& Typography()
    {
        if(!textFactory)check_hresult(DWriteCreateFactory(DWRITE_FACTORY_TYPE_SHARED,__uuidof(IDWriteFactory),reinterpret_cast<::IUnknown**>(textFactory.put())));
        if(!typographyEngine)typographyEngine=std::make_unique<typography::Engine>(textFactory.get());
        return *typographyEngine;
    }
    static void ValidateV2(GDOpV2 const& operation)
    {
        auto const& op=operation.legacy;
        if(op.kind<0||op.kind>7||!std::isfinite(op.x)||!std::isfinite(op.y)||!std::isfinite(op.width)||!std::isfinite(op.height)
            ||!std::isfinite(op.radius)||!std::isfinite(op.fontSize)||(op.kind!=4&&(op.width<0||op.height<0))
            ||operation.fontWeight<0||operation.fontWeight>999||!std::isfinite(operation.trackingEm)
            ||!std::isfinite(operation.lineHeightDip)||!std::isfinite(operation.baselineDip)
            ||operation.lineHeightDip<0||operation.baselineDip<0||operation.textRole>22||(operation.textFlags&~0x303u))throw hresult_invalid_argument();
        if((operation.textFlags&0x300u)&&operation.textRole!=1&&operation.textRole!=2&&operation.textRole!=4)throw hresult_invalid_argument();
        bool newText=(op.kind==2||op.kind==7)&&operation.textRole!=0;
        if(!newText&&(operation.textRole!=0||operation.fontWeight!=0||operation.trackingEm!=0||operation.lineHeightDip!=0
            ||operation.baselineDip!=0||operation.textFlags!=0||(operation.localeName&&operation.localeName[0])))throw hresult_invalid_argument();
        if(newText&&(op.fontSize<=0||op.fontSize>512))throw hresult_invalid_argument();
        if(op.text&&wcsnlen_s(op.text,65537)>65536)throw hresult_invalid_argument();
        if(operation.localeName&&wcsnlen_s(operation.localeName,LOCALE_NAME_MAX_LENGTH)>=LOCALE_NAME_MAX_LENGTH)throw hresult_invalid_argument();
    }
    typography::Request TextRequest(GDOpV2 const& operation)const
    {
        auto const& op=operation.legacy;typography::Request request;
        request.text=op.text?op.text:L"";request.locale=operation.localeName?operation.localeName:L"";
        request.role=operation.textRole;request.fontWeight=uint32_t(operation.fontWeight);request.flags=operation.textFlags;
        request.fontSize=operation.textRole?op.fontSize:std::max(1.0f,op.fontSize);request.lineHeight=operation.lineHeightDip;
        request.baseline=operation.baselineDip;request.trackingEm=operation.trackingEm;
        request.width=op.width;request.height=op.height;request.dpi=float(dpi);request.bold=op.bold!=0;request.icon=op.kind==6;
        request.wrap=op.kind==7;request.center=op.center!=0;request.trim=op.kind!=6;request.paragraphCenter=true;return request;
    }
    void Paint(ID2D1DeviceContext* context,GDOp const* operations,unsigned count,ID2D1SolidColorBrush* sharedBrush=nullptr)
    {
        com_ptr<ID2D1SolidColorBrush> brush;
        if(sharedBrush)brush.copy_from(sharedBrush);else check_hresult(context->CreateSolidColorBrush(D2D1::ColorF(0,0,0,1),brush.put()));
        for(unsigned index=0;index<count;++index)
        {
            auto const& op=operations[index];auto rect=D2D1::RectF(op.x,op.y,op.x+op.width,op.y+op.height);
            brush->SetColor(Color(op.color));
            switch(op.kind)
            {
            case 0:context->FillRectangle(rect,brush.get());break;
            case 1:context->FillRoundedRectangle({rect,std::max(0.0f,op.radius),std::max(0.0f,op.radius)},brush.get());break;
            case 2: case 6: case 7:
                if(op.text&&op.width>0&&op.height>0)
                {
                    com_ptr<IDWriteTextFormat> format;check_hresult(textFactory->CreateTextFormat(op.kind==6?L"Segoe Fluent Icons":L"Segoe UI Variable Text",nullptr,op.bold?DWRITE_FONT_WEIGHT_SEMI_BOLD:DWRITE_FONT_WEIGHT_NORMAL,DWRITE_FONT_STYLE_NORMAL,DWRITE_FONT_STRETCH_NORMAL,std::max(1.0f,op.fontSize),L"",format.put()));
                    check_hresult(format->SetWordWrapping(op.kind==7?DWRITE_WORD_WRAPPING_WRAP:DWRITE_WORD_WRAPPING_NO_WRAP));
                    check_hresult(format->SetTextAlignment(op.center?DWRITE_TEXT_ALIGNMENT_CENTER:DWRITE_TEXT_ALIGNMENT_LEADING));
                    check_hresult(format->SetParagraphAlignment(DWRITE_PARAGRAPH_ALIGNMENT_CENTER));
                    std::wstring text(op.text);
                    if(op.kind!=7)
                        for(auto& character:text)if(character==L'\r'||character==L'\n'||character==L'\t')character=L' ';
                    com_ptr<IDWriteTextLayout> layout;
                    check_hresult(textFactory->CreateTextLayout(text.c_str(),static_cast<UINT32>(text.size()),format.get(),op.width,op.height,layout.put()));
                    if(text.size()==5&&text[2]==L':'&&text[0]>=L'0'&&text[0]<=L'9'&&text[1]>=L'0'&&text[1]<=L'9'&&text[3]>=L'0'&&text[3]<=L'9'&&text[4]>=L'0'&&text[4]<=L'9')
                    {
                        com_ptr<IDWriteTypography> typography;check_hresult(textFactory->CreateTypography(typography.put()));
                        check_hresult(typography->AddFontFeature({DWRITE_FONT_FEATURE_TAG_TABULAR_FIGURES,1}));
                        check_hresult(layout->SetTypography(typography.get(),{0,static_cast<UINT32>(text.size())}));
                    }
                    if(op.kind!=6)
                    {
                        com_ptr<IDWriteInlineObject> ellipsis;check_hresult(textFactory->CreateEllipsisTrimmingSign(format.get(),ellipsis.put()));
                        DWRITE_TRIMMING trimming{DWRITE_TRIMMING_GRANULARITY_CHARACTER,0,0};check_hresult(layout->SetTrimming(&trimming,ellipsis.get()));
                    }
                    context->DrawTextLayout({op.x,op.y},layout.get(),brush.get(),D2D1_DRAW_TEXT_OPTIONS_CLIP);
                }
                break;
            case 3:
                try
                {
                    auto bitmap=Decode(op);
                    if(bitmap&&op.width>0&&op.height>0)
                    {
                        // Cover rather than stretch: preserve the artwork's
                        // proportions for portrait and landscape thumbnails.
                        auto size=bitmap->GetSize();auto source=D2D1::RectF(0,0,size.width,size.height);
                        auto aspect=op.width/op.height;
                        if(size.width>size.height*aspect)
                        {auto cropWidth=size.height*aspect;source.left=(size.width-cropWidth)/2;source.right=source.left+cropWidth;}
                        else
                        {auto cropHeight=size.width/aspect;source.top=(size.height-cropHeight)/2;source.bottom=source.top+cropHeight;}
                        if(op.radius>0)
                        {
                            com_ptr<ID2D1RoundedRectangleGeometry> mask;
                            float radius=std::min(op.radius,std::min(op.width,op.height)/2);
                            check_hresult(d2dFactory->CreateRoundedRectangleGeometry({rect,radius,radius},mask.put()));
                            context->PushLayer(D2D1::LayerParameters1(rect,mask.get()),nullptr);
                            context->DrawBitmap(bitmap.get(),rect,1,D2D1_INTERPOLATION_MODE_HIGH_QUALITY_CUBIC,&source);
                            context->PopLayer();
                        }
                        else context->DrawBitmap(bitmap.get(),rect,1,D2D1_INTERPOLATION_MODE_HIGH_QUALITY_CUBIC,&source);
                    }
                }
                catch(hresult_error const& error){if(PropagateArtworkFailure(materialBackend,error.code()))throw;}
                catch(...){if(materialBackend==2)throw;/* Legacy input fallback. */}
                break;
            case 4:context->DrawLine({op.x,op.y},{op.x+op.width,op.y+op.height},brush.get(),std::max(.5f,op.radius));break;
            case 5:context->FillEllipse({{op.x+op.width/2,op.y+op.height/2},op.width/2,op.height/2},brush.get());break;
            default:throw hresult_invalid_argument();
            }
        }
    }
    bool DeviceLost(HRESULT error)const
    {return error==D2DERR_RECREATE_TARGET||error==DXGI_ERROR_DEVICE_REMOVED||error==DXGI_ERROR_DEVICE_RESET||(d3d&&FAILED(d3d->GetDeviceRemovedReason()));}
    void PaintV2(ID2D1DeviceContext* context,GDOpV2 const* operations,unsigned count)
    {
        com_ptr<ID2D1SolidColorBrush> brush;check_hresult(context->CreateSolidColorBrush(D2D1::ColorF(0,0,0,1),brush.put()));
        for(unsigned index=0;index<count;++index)
        {
            renderOpIndex=index;
            auto const& extension=operations[index];auto const& op=extension.legacy;
            if(extension.textRole&&(op.kind==2||op.kind==7))
            {
                if(op.text&&op.width>0&&op.height>0)
                {
                    auto layout=Typography().CreateLayout(TextRequest(extension));brush->SetColor(Color(op.color));
                    context->DrawTextLayout({op.x,op.y},layout->textLayout.get(),brush.get(),D2D1_DRAW_TEXT_OPTIONS(D2D1_DRAW_TEXT_OPTIONS_CLIP|D2D1_DRAW_TEXT_OPTIONS_ENABLE_COLOR_FONT));
                }
            }
            else Paint(context,&op,1,brush.get()); // Exact legacy/icon/non-text branch.
        }
    }
    void LogRenderV2Failure(wchar_t const* stage,HRESULT failure,wchar_t const* exceptionType,
        float width,float height,GDOpV2 const* operations,unsigned count) noexcept
    {
        const uint32_t index=renderOpIndex;
        const ULONGLONG now=GetTickCount64();
        if(lastRenderErrorLogMs!=0&&lastRenderErrorLogHr==failure&&lastRenderErrorLogIndex==index&&
            wcscmp(lastRenderErrorLogStage,stage)==0&&now-lastRenderErrorLogMs<1000)return;
        lastRenderErrorLogMs=now;lastRenderErrorLogHr=failure;lastRenderErrorLogIndex=index;lastRenderErrorLogStage=stage;
        int kind=-1;uint32_t textRole=0;float x=0,y=0,w=0,h=0;
        if(index<count&&operations)
        {
            const GDOpV2& operation=operations[index];kind=operation.legacy.kind;textRole=operation.textRole;
            x=operation.legacy.x;y=operation.legacy.y;w=operation.legacy.width;h=operation.legacy.height;
        }
        wchar_t fields[768]{};
        swprintf_s(fields,std::size(fields),L"stage=%ls component=GDRenderSurfaceV2 hr=0x%08X exceptionType=%ls stack=unavailable backend=native-surface captureCandidate=%u capturedPresented=%u materialState=%ls fallbackReason=surface-error geometrySequence=%llu logicalDip=%.2fx%.2f activePx=%.1fx%.1f allocPx=unknown capacityPx=unknown paddingPx=unknown originPx=not-applicable fractionalOffset=%.4f,%.4f dpi=%u layer=0 shapeId=0 opIndex=%u opKind=%d opRect=%.3f,%.3f,%.3f,%.3f opTextRole=%u theme=%d activeShaderVersion=0.3",
            stage?stage:L"unknown",static_cast<unsigned>(failure),exceptionType?exceptionType:L"unknown",
            liquidCaptureCandidate?1u:0u,liquidCapturePresented?1u:0u,
            fallback?L"fallback":L"active",static_cast<unsigned long long>(glassSceneSequence),width,height,
            width*Scale(),height*Scale(),presentationOffsetX,presentationOffsetY,dpi,index,kind,x,y,w,h,textRole,liquidSettings.theme);
        liquidglass::diagnostics::Log(L"render-surface-error",fields);
    }
    CompositionDrawingSurface Render(float width,float height,GDOp const* operations,unsigned count)
    {
        if(!std::isfinite(width)||!std::isfinite(height)||width<=0||height<=0||width>4096||height>4096||count>4096||(!operations&&count))throw hresult_invalid_argument();
        for(unsigned index=0;index<count;++index)
        {
            auto const& op=operations[index];
            if(!std::isfinite(op.x)||!std::isfinite(op.y)||!std::isfinite(op.width)||!std::isfinite(op.height)||!std::isfinite(op.radius)||!std::isfinite(op.fontSize)||(op.kind!=4&&(op.width<0||op.height<0)))throw hresult_invalid_argument();
        }
        for(int attempt=0;attempt<2;++attempt)
        {
            try
            {
                if(ownedFaulted)throw hresult_error(E_ABORT);
                if(!graphics){CreateGraphics();if(materialBackend==1)CreateGrain();}
                auto surface=CreateSurface(int(std::ceil(width*Scale())),int(std::ceil(height*Scale())));
                Draw(surface,float(dpi),[&](auto context){Paint(context,operations,count);});return surface;
            }
            catch(hresult_error const& error)
            {
                if(materialBackend==2){TripOwned(error.code());throw;}
                if(attempt||!DeviceLost(error.code()))throw;
                fallback=true;UpdateAccessibleGeometry();ApplyMaterial();
                CreateGraphics();CreateGrain();fallback=false;UpdateAccessibleGeometry();ApplyMaterial();
                PostMessageW(window,RecoveredMessage,0,0); // C# recreates its content after this call has returned.
            }
        }
        throw hresult_error(E_FAIL);
    }
    CompositionDrawingSurface RenderV2(float width,float height,GDOpV2 const* operations,unsigned count)
    {
        if(!std::isfinite(width)||!std::isfinite(height)||width<=0||height<=0||width>4096||height>4096||count>4096||(!operations&&count))
        {renderOpIndex=std::numeric_limits<uint32_t>::max();LogRenderV2Failure(L"validate-frame",E_INVALIDARG,L"hresult_error",width,height,operations,count);throw hresult_invalid_argument();}
        for(unsigned index=0;index<count;++index)
        {
            renderOpIndex=index;
            try{ValidateV2(operations[index]);}
            catch(hresult_error const& error){LogRenderV2Failure(L"validate-v2",error.code(),L"hresult_error",width,height,operations,count);renderOpIndex=std::numeric_limits<uint32_t>::max();throw;}
        }
        renderOpIndex=std::numeric_limits<uint32_t>::max();
        for(int attempt=0;attempt<2;++attempt)
        {
            try
            {
                if(ownedFaulted)throw hresult_error(E_ABORT);
                if(!graphics){CreateGraphics();if(materialBackend==1)CreateGrain();}
                auto surface=CreateSurface(int(std::ceil(width*Scale())),int(std::ceil(height*Scale())));
                Draw(surface,float(dpi),[&](auto context){PaintV2(context,operations,count);});
                renderOpIndex=std::numeric_limits<uint32_t>::max();return surface;
            }
            catch(hresult_error const& error)
            {
                LogRenderV2Failure(L"render-v2",error.code(),L"hresult_error",width,height,operations,count);
                if(materialBackend==2){TripOwned(error.code());throw;}
                if(attempt||!DeviceLost(error.code()))throw;
                fallback=true;UpdateAccessibleGeometry();ApplyMaterial();CreateGraphics();CreateGrain();
                fallback=false;UpdateAccessibleGeometry();ApplyMaterial();
                PostMessageW(window,RecoveredMessage,0,0);
            }
            catch(...)
            {
                LogRenderV2Failure(L"render-v2",to_hresult(),L"unknown",width,height,operations,count);
                throw;
            }
        }
        throw hresult_error(E_FAIL);
    }
    HRESULT Shutdown()noexcept
    {
        if(destroying)return shutdownResult;destroying=true;callback=nullptr;
        cleanup::Result result(shutdownResult);
        auto attempt=[&](auto&& operation) noexcept {
            result.Attempt(operation,[]() noexcept {return to_hresult();});
        };
        auto recordWin32Failure=[&]() noexcept {
            const DWORD error=GetLastError();
            result.Record(HRESULT_FROM_WIN32(error==ERROR_SUCCESS?ERROR_GEN_FAILURE:error));
        };
        if(window)KillTimer(window,DpiTimer);
        if(window)KillTimer(window,LiquidTimer);
        if(window)KillTimer(window,LiquidCaptureTimer);
        if(window)KillTimer(window,AuthorizedShortTestTimer);
        if(window)KillTimer(window,89);
        StopLiquidCapture();
        attempt([&]{if(opticalLight)opticalLight.Brush(nullptr);if(opticalBrush)opticalBrush.Surface(nullptr);});
        opticalSurface=nullptr;
        if(liquid)result.Record(liquid->Close());liquid.reset();liquidSurface=nullptr;liquidBrush=nullptr;
        if(captured&&GetCapture()==window&&!ReleaseCapture())recordWin32Failure();captured=false;
        result.Record(DisconnectAccessibleProvider(accessibility,provider.Get(),
            [](IRawElementProviderSimple* value) noexcept {return UiaDisconnectProvider(value);}));
        provider.Reset();
        // Invalidate the HWND under the same lock used by UIA posting before
        // draining requests; no other COM thread can post another allocation.
        MSG message{};
        if(window)while(PeekMessageW(&message,window,RangeMessage,RangeMessage,PM_REMOVE))delete reinterpret_cast<RangeRequest*>(message.lParam);
        if(IsWindow(shadow)&&!DestroyWindow(shadow))recordWin32Failure();shadow=nullptr;result.Record(ReleaseShadowBitmap());
        attempt([&]{if(target)target.Root(nullptr);if(glassTarget)glassTarget.Root(nullptr);});
        if(IsWindow(window)&&!DestroyWindow(window))recordWin32Failure();window=nullptr;
        if(IsWindow(glassWindow)&&!DestroyWindow(glassWindow))recordWin32Failure();glassWindow=nullptr;
        attempt([&]{if(root)root.Children().RemoveAll();});
        attempt([&]{if(glassRoot)glassRoot.Children().RemoveAll();});
        fallbackLayerPool.clear();fallbackLayers=nullptr;fallbackBlurL2=nullptr;fallbackBlurL3=nullptr;fallbackRimBrush=nullptr;
        ownedRenderer.reset();ownedSurface=nullptr;ownedBrush=nullptr;
        images.clear();grainSurface=nullptr;graphics=nullptr;
        opticalSurface=nullptr;opticalBrush=nullptr;opticalLight=nullptr;
        content=nullptr;clearEdge=nullptr;clearBackdrop=nullptr;clearEdgeGeometry=nullptr;glass=nullptr;liquidOverlay=nullptr;liquidRefractionOverlay=nullptr;tint=nullptr;grain=nullptr;border=nullptr;bevel=nullptr;
        borderGeometry=nullptr;borderShape=nullptr;shoulderShape=nullptr;meniscusShape=nullptr;shoulderGeometry=nullptr;meniscusGeometry=nullptr;clipGeometry=nullptr;tensionGeometry=nullptr;
        tintBrush=nullptr;borderBrush=nullptr;shoulderBrush=nullptr;meniscusBrush=nullptr;solidTintBrush=nullptr;solidBorderBrush=nullptr;
        backdrop=nullptr;root=nullptr;glassRoot=nullptr;target=nullptr;glassTarget=nullptr;
        attempt([&]{if(compositor)compositor.Close();});
        compositor=nullptr;
        typographyEngine.reset();cacheContext=nullptr;wic=nullptr;textFactory=nullptr;d2d=nullptr;d2dFactory=nullptr;geometryFactory=nullptr;d3dContext=nullptr;d3d=nullptr;
        bool sawQuit=false;WPARAM quitCode=0;
        try
        {
            if(queue)
            {
                auto shutdown=queue.ShutdownQueueAsync();auto deadline=GetTickCount64()+2000;
                while(shutdown.Status()==winrt::Windows::Foundation::AsyncStatus::Started&&GetTickCount64()<deadline)
                {
                    for(unsigned batch=0;batch<256&&GetTickCount64()<deadline&&PeekMessageW(&message,nullptr,0,0,PM_REMOVE);++batch){if(message.message==WM_QUIT){sawQuit=true;quitCode=message.wParam;break;}TranslateMessage(&message);DispatchMessageW(&message);}
                    if(MsgWaitForMultipleObjectsEx(0,nullptr,10,QS_ALLINPUT,MWMO_INPUTAVAILABLE)==WAIT_FAILED){recordWin32Failure();break;}
                }
                using winrt::Windows::Foundation::AsyncStatus;
                switch(shutdown.Status()){
                case AsyncStatus::Completed:
                    shutdown.GetResults();
                    result.RecordAsync(cleanup::AsyncOutcome::completed);break;
                case AsyncStatus::Started:
                    result.RecordAsync(cleanup::AsyncOutcome::pending);break;
                case AsyncStatus::Canceled:
                    result.RecordAsync(cleanup::AsyncOutcome::canceled);break;
                case AsyncStatus::Error:
                    result.RecordAsync(cleanup::AsyncOutcome::error,shutdown.ErrorCode());break;
                }
            }
        }catch(...){result.Record(to_hresult());}

        queue=nullptr;
        if(systemSourceSingleton){if(!CloseHandle(systemSourceSingleton))recordWin32Failure();systemSourceSingleton=nullptr;}
        if(sawQuit)PostQuitMessage(static_cast<int>(quitCode));
        shutdownResult=result.Value();return shutdownResult;
    }
};
using HostRef=std::shared_ptr<Host>;
HostRef FromContext(void* context){return context?*static_cast<HostRef*>(context):HostRef{};}

LRESULT CALLBACK ShadowProc(HWND hwnd,UINT message,WPARAM wp,LPARAM lp)
{
    if(message==WM_NCCREATE)
        SetWindowLongPtrW(hwnd,GWLP_USERDATA,reinterpret_cast<LONG_PTR>(reinterpret_cast<CREATESTRUCTW*>(lp)->lpCreateParams));
    if(message==WM_DESTROY)
    {
        auto host=reinterpret_cast<Host*>(GetWindowLongPtrW(hwnd,GWLP_USERDATA));
        if(host&&host->liquid)host->liquid->Suspend(true,true);
    }
    if(message==WM_NCDESTROY)SetWindowLongPtrW(hwnd,GWLP_USERDATA,0);
    if(message==WM_NCHITTEST)return HTTRANSPARENT;
    if(message==WM_MOUSEACTIVATE)return MA_NOACTIVATE;
    if(message==WM_ERASEBKGND)return 1;
    return DefWindowProcW(hwnd,message,wp,lp);
}
LRESULT CALLBACK GlassWindowProc(HWND hwnd,UINT message,WPARAM wp,LPARAM lp)
{
    if(message==WM_NCHITTEST)return HTTRANSPARENT;
    if(message==WM_MOUSEACTIVATE)return MA_NOACTIVATE;
    if(message==WM_ERASEBKGND)return 1;
    return DefWindowProcW(hwnd,message,wp,lp);
}
LRESULT CALLBACK WindowProc(HWND hwnd,UINT message,WPARAM wp,LPARAM lp)
{
    auto host=reinterpret_cast<Host*>(GetWindowLongPtrW(hwnd,GWLP_USERDATA));
    if(message==WM_NCCREATE)
    {host=static_cast<Host*>(reinterpret_cast<CREATESTRUCTW*>(lp)->lpCreateParams);host->window=hwnd;SetWindowLongPtrW(hwnd,GWLP_USERDATA,reinterpret_cast<LONG_PTR>(host));}
    if(!host)return DefWindowProcW(hwnd,message,wp,lp);
    // Managed callbacks may synchronously quit and release the external host
    // handle. Keep this dispatch alive until the callback and WndProc return.
    auto dispatchLifetime=host->shared_from_this();
    try
    {
        switch(message)
        {
        case WM_WINDOWPOSCHANGING:
            if(!host->destroying){
                auto placement=reinterpret_cast<WINDOWPOS*>(lp);RECT bounds{};
                if(GetWindowRect(hwnd,&bounds)){
                    int width=(placement->flags&SWP_NOSIZE)?bounds.right-bounds.left:placement->cx;
                    int height=(placement->flags&SWP_NOSIZE)?bounds.bottom-bounds.top:placement->cy;
                    int x=(placement->flags&SWP_NOMOVE)?bounds.left:placement->x;
                    int y=(placement->flags&SWP_NOMOVE)?bounds.top:placement->y;
                    int clampedX=x,clampedY=y;
                    if(desktopviewport::ClampDesktopPlacement(GetSystemMetrics(SM_XVIRTUALSCREEN),GetSystemMetrics(SM_YVIRTUALSCREEN),
                        GetSystemMetrics(SM_CXVIRTUALSCREEN),GetSystemMetrics(SM_CYVIRTUALSCREEN),width,height,clampedX,clampedY)&&
                        (clampedX!=x||clampedY!=y)){
                        placement->x=clampedX;placement->y=clampedY;placement->flags&=~SWP_NOMOVE;
                    }
                }
                return 0;
            }break;
        case WM_MOUSEACTIVATE:return MA_NOACTIVATE;
        case WM_ERASEBKGND:return 1;
        case WM_SIZE:host->Layout();host->UpdateAccessibleGeometry();host->Emit(ContextEvent);return 0;
        case WM_WINDOWPOSCHANGED:
            if(!(GetWindowLongPtrW(hwnd,GWL_EXSTYLE)&WS_EX_TOPMOST))host->EnsureTopmost();
        {
            auto const& placement=*reinterpret_cast<WINDOWPOS*>(lp);
            if(!host->deferFrameSurfaceUpdates){
                // EndDeferWindowPos can dispatch while its two HWNDs are still
                // being notified. Never overwrite the batch with old bounds.
                host->SyncGlassWindowBounds();
                host->UpdateShadow((placement.flags&SWP_NOZORDER)==0);
                host->UpdateLiquid();
            }
            host->UpdateAccessibleGeometry();host->Emit(ContextEvent);break;
        }
        case WM_DPICHANGED:
        {auto rectangle=reinterpret_cast<RECT*>(lp);host->ApplyDpi(LOWORD(wp),rectangle->left,rectangle->top);return 0;}
        case WM_SETTINGCHANGE:
            host->ReconcileDpi();SetTimer(hwnd,DpiTimer,300,nullptr);host->ApplyMaterial();host->Emit(12);break;
        case WM_TIMECHANGE:host->Emit(12);return 0;
        case WM_DISPLAYCHANGE:host->ReconcileDpi();SetTimer(hwnd,DpiTimer,300,nullptr);host->Emit(10);break;
        case WM_TIMER:
            if(wp==DpiTimer){KillTimer(hwnd,DpiTimer);host->ReconcileDpi();return 0;}
            if(wp==LiquidTimer){host->UpdateLiquid();return 0;}
            if(wp==LiquidCaptureTimer){host->UpdateLiquidCapture();return 0;}
            if(wp==AuthorizedShortTestTimer){KillTimer(hwnd,AuthorizedShortTestTimer);host->Emit(14);return 0;}break;
        case WM_POWERBROADCAST:if(wp==PBT_APMRESUMEAUTOMATIC||wp==PBT_APMRESUMESUSPEND){host->ReconcileDpi();host->Emit(11);}break;
        case WM_MOUSEMOVE:
        {
            if(!host->visible)return 0;
            const float x=(static_cast<float>(GET_X_LPARAM(lp))-host->presentationOffsetX)/host->Scale();
            const float y=(static_cast<float>(GET_Y_LPARAM(lp))-host->presentationOffsetY)/host->Scale();
            const bool inside=host->Inside(x,y);
            if(inside&&!host->tracking){host->tracking=true;TRACKMOUSEEVENT track{sizeof(track),TME_LEAVE,hwnd,0};TrackMouseEvent(&track);host->EmitPointer(1,lp,inside);}
            else if(!inside&&host->tracking){host->tracking=false;host->EmitPointer(2,lp,inside);}
            host->EmitPointer(3,lp,inside);return 0;
        }
        case WM_MOUSELEAVE:if(host->tracking){host->tracking=false;host->Emit(2,0,0,0,0,input::kOutsideSurfaceControlId);}return 0;
        case WM_LBUTTONDOWN:
        {
            if(!host->visible)return 0;
            const bool inside=host->Inside((static_cast<float>(GET_X_LPARAM(lp))-host->presentationOffsetX)/host->Scale(),
                (static_cast<float>(GET_Y_LPARAM(lp))-host->presentationOffsetY)/host->Scale());
            if(!inside)return 0;
            host->captured=true;SetCapture(hwnd);host->EmitPointer(4,lp,inside);return 0;
        }
        case WM_LBUTTONUP:
        {
            if(!host->visible)return 0;
            const bool inside=host->Inside((static_cast<float>(GET_X_LPARAM(lp))-host->presentationOffsetX)/host->Scale(),
                (static_cast<float>(GET_Y_LPARAM(lp))-host->presentationOffsetY)/host->Scale());
            host->EmitPointer(5,lp,inside);host->captured=false;if(GetCapture()==hwnd)ReleaseCapture();return 0;
        }
        case WM_CAPTURECHANGED:if(host->captured){host->captured=false;host->Emit(15);}return 0;
        case WM_MOUSEWHEEL:
        {
            if(!host->visible)return 0;
            POINT point{GET_X_LPARAM(lp),GET_Y_LPARAM(lp)};ScreenToClient(hwnd,&point);
            const float x=(static_cast<float>(point.x)-host->presentationOffsetX)/host->Scale();
            const float y=(static_cast<float>(point.y)-host->presentationOffsetY)/host->Scale();
            const bool inside=host->Inside(x,y);
            const int control=inside?host->Hit(x,y):0;
            host->Emit(6,x,y,GET_X_LPARAM(lp),GET_Y_LPARAM(lp),input::ControlIdForSurface(inside,control),
                double(GET_WHEEL_DELTA_WPARAM(wp))/WHEEL_DELTA);return 0;
        }
        case WM_SETCURSOR:if(LOWORD(lp)==HTCLIENT){SetCursor(LoadCursor(nullptr,IDC_ARROW));return TRUE;}break;
        case WM_NCHITTEST:
        {
            POINT point{GET_X_LPARAM(lp),GET_Y_LPARAM(lp)};ScreenToClient(hwnd,&point);
            return host->Inside((static_cast<float>(point.x)-host->presentationOffsetX)/host->Scale(),
                (static_cast<float>(point.y)-host->presentationOffsetY)/host->Scale())?HTCLIENT:HTTRANSPARENT;
        }
        case WM_GETOBJECT:
            if(!host->destroying&&static_cast<LONG>(lp)==UiaRootObjectId&&host->provider)return UiaReturnRawElementProvider(hwnd,wp,lp,host->provider.Get());break;
        case InvokeMessage:
        {
            bool enabled=false;{std::lock_guard lock(host->accessibility->mutex);for(auto const& item:host->accessibility->items)if(item.id==static_cast<int>(wp))enabled=item.enabled;}
            if(enabled)host->Emit(7,0,0,0,0,static_cast<int>(wp));return 0;
        }
        case RangeMessage:
        {
            auto request=std::unique_ptr<RangeRequest>(reinterpret_cast<RangeRequest*>(lp));if(!request)return 0;
            bool enabled=false;{std::lock_guard lock(host->accessibility->mutex);for(auto const& item:host->accessibility->items)if(item.id==request->id){enabled=item.enabled&&item.range;request->value=std::clamp(request->value,item.minimum,item.maximum);}}
            if(enabled)host->Emit(8,0,0,0,0,request->id,request->value);return 0;
        }
        case RecoveredMessage:host->Emit(12,0,0,0,0,-1);return 0;
        case WM_CLOSE:host->Emit(14);return 0;
        case WM_PAINT:{PAINTSTRUCT paint{};BeginPaint(hwnd,&paint);EndPaint(hwnd,&paint);return 0;}
        case WM_DESTROY:
            // Also cover an external DestroyWindow path: join the QA affinity
            // watchdog while the HWNDs are still valid, before handle reuse.
            if(host->liquid)host->liquid->Suspend(true,true);
            KillTimer(hwnd,DpiTimer);KillTimer(hwnd,LiquidTimer);KillTimer(hwnd,LiquidCaptureTimer);KillTimer(hwnd,AuthorizedShortTestTimer);return 0;
        case WM_NCDESTROY:SetWindowLongPtrW(hwnd,GWLP_USERDATA,0);break;
        }
    }
    catch(...)
    {
        auto error=to_hresult();host->fallback=true;
        try{host->UpdateAccessibleGeometry();}catch(...){}
        host->TripOwned(error);
        try{host->ApplyMaterial();}catch(...){}
        host->Emit(12,0,0,0,0,-2,static_cast<unsigned>(error));
    }
    return DefWindowProcW(hwnd,message,wp,lp);
}
void RegisterWindows()
{
    WNDCLASSEXW cls{sizeof(cls)};cls.hInstance=GetModuleHandleW(nullptr);cls.hCursor=LoadCursor(nullptr,IDC_ARROW);
    cls.lpfnWndProc=WindowProc;cls.lpszClassName=WindowClass;
    if(!RegisterClassExW(&cls)&&GetLastError()!=ERROR_CLASS_ALREADY_EXISTS)throw hresult_error(HRESULT_FROM_WIN32(GetLastError()));
    cls.lpfnWndProc=GlassWindowProc;cls.lpszClassName=GlassWindowClass;
    if(!RegisterClassExW(&cls)&&GetLastError()!=ERROR_CLASS_ALREADY_EXISTS)throw hresult_error(HRESULT_FROM_WIN32(GetLastError()));
    cls.lpfnWndProc=ShadowProc;cls.lpszClassName=ShadowClass;
    if(!RegisterClassExW(&cls)&&GetLastError()!=ERROR_CLASS_ALREADY_EXISTS)throw hresult_error(HRESULT_FROM_WIN32(GetLastError()));
}
}

static HRESULT CreateHost(GDCallback callback,void* user,uint32_t backend,bool authorizedShortTest,void** context,HWND* hwnd)
{
    if(!context||!hwnd)return E_POINTER;*context=nullptr;*hwnd=nullptr;
    try
    {
        auto host=std::make_shared<gd::Host>();
        host->liquidCaptureCandidate=backend==1||backend==3;
        host->materialBackend=backend==3?1:backend;host->authorizedIncidentShortTest=authorizedShortTest;
        if(!winrt::Windows::System::DispatcherQueue::GetForCurrentThread())
        {
            DispatcherQueueOptions options{sizeof(options),DQTYPE_THREAD_CURRENT,DQTAT_COM_NONE};
            check_hresult(CreateDispatcherQueueController(options,reinterpret_cast<ABI::Windows::System::IDispatcherQueueController**>(put_abi(host->queue))));
        }
        gd::RegisterWindows();
        auto window=CreateWindowExW(WS_EX_TOOLWINDOW|WS_EX_NOACTIVATE|WS_EX_TOPMOST|WS_EX_NOREDIRECTIONBITMAP,gd::WindowClass,
            L"GlassDesk 音乐小组件",WS_POPUP,0,0,232,48,nullptr,nullptr,GetModuleHandleW(nullptr),host.get());
        check_bool(window!=nullptr);host->Initialize(window);
        wchar_t fields[256]{};
        swprintf_s(fields,std::size(fields),L"stage=native-start backend=%u captureCandidate=%u capturedPresented=0 materialState=waiting activeShaderVersion=0.3 fallbackReason=first-frame-pending pluginVersion=%ls",
            backend,host->liquidCaptureCandidate?1u:0u,gd::liquidglass::diagnostics::kLiquidGlassPluginVersion);
        gd::liquidglass::diagnostics::Log(L"native-start",fields);
        host->callback=callback;host->user=user;*hwnd=window;*context=new gd::HostRef(host);return S_OK;
    }catch(...){return to_hresult();}
}
extern "C" __declspec(dllexport) HRESULT __stdcall GDCreate(GDCallback callback,void* user,void** context,HWND* hwnd)
{return CreateHost(callback,user,1,false,context,hwnd);}
extern "C" __declspec(dllexport) HRESULT __stdcall GDCreateV2(GDCallback callback,void* user,GDHostCreationOptions const* options,void** context,HWND* hwnd)
{
    if(context)*context=nullptr;if(hwnd)*hwnd=nullptr;
    if(options&&options->size==sizeof(GDHostCreationOptions)&&options->version==2){
        if(options->backend!=3||options->reserved||!(options->sessionLow|options->sessionHigh))return E_INVALIDARG;
        return CreateHost(callback,user,3,false,context,hwnd);
    }
    if(!options||options->size!=sizeof(GDHostCreationOptions)||options->version!=1||options->backend>3||(options->reserved&~1u)||(options->reserved&&options->backend!=2&&options->backend!=3))return E_INVALIDARG;
    if(options->backend==3 && options->reserved!=1)return E_ACCESSDENIED;
    if(options->backend==2||options->backend==3){
        if(!(options->sessionLow|options->sessionHigh))return E_ACCESSDENIED;
        wchar_t computer[MAX_COMPUTERNAME_LENGTH+1]{};DWORD count=MAX_COMPUTERNAME_LENGTH+1;
        if(!GetComputerNameW(computer,&count))return E_ACCESSDENIED;
        bool incident=_wcsicmp(computer,L"sheng")==0;
        if(incident!=(options->reserved==1))return E_ACCESSDENIED;
    }
    return CreateHost(callback,user,options->backend,options->reserved==1,context,hwnd);
}
extern "C" __declspec(dllexport) HRESULT __stdcall GDTripOwnedGlass(void* context)
{
    auto host=gd::FromContext(context);if(!host)return E_POINTER;
    auto hr=host->CheckThread();if(FAILED(hr))return hr;host->TripOwned();return S_OK;
}
extern "C" __declspec(dllexport) HRESULT __stdcall GDSetOwnedGlassImage(void* context,uint8_t const* pixels,uint32_t width,uint32_t height,uint32_t bytes,GDOwnedGlassImageMapping const* mapping)
{
    auto host=gd::FromContext(context);if(!host||!mapping||!pixels)return E_POINTER;
    auto hr=host->CheckThread();if(FAILED(hr))return hr;
    if(host->materialBackend!=2||host->ownedFaulted)return E_ACCESSDENIED;
    if(mapping->size!=sizeof(GDOwnedGlassImageMapping)||mapping->version!=1||mapping->reserved
        ||width==0||height==0||width>1600||height>1600||uint64_t(width)*height*4!=bytes
        ||!std::isfinite(mapping->imageX)||!std::isfinite(mapping->imageY)
        ||!std::isfinite(mapping->displayedWidth)||!std::isfinite(mapping->displayedHeight)
        ||mapping->displayedWidth<=0||mapping->displayedHeight<=0||mapping->displayedWidth>100000||mapping->displayedHeight>100000
        ||std::abs(mapping->imageX)>100000||std::abs(mapping->imageY)>100000
        ||mapping->monitorWidth<=0||mapping->monitorHeight<=0||mapping->monitorWidth>32768||mapping->monitorHeight>32768)return E_INVALIDARG;
    try{
        if(!host->ownedRenderer)host->ownedRenderer=std::make_unique<gd::OwnedImageGlass>(host->d3d.get());
        host->ownedRenderer->SetImage(pixels,width,height,bytes,gd::OwnedImageAlphaMode::Straight);
        host->ownedMapping=*mapping;host->ownedImageSet=true;host->ownedFrameKey.clear();return S_OK;
    }catch(...){host->TripOwned();return to_hresult();}
}
extern "C" __declspec(dllexport) HRESULT __stdcall GDPresentOwnedGlass(void* context,int eligible,GDOwnedGlassLens const* lenses,uint32_t count,uint64_t sequence)
{
    auto host=gd::FromContext(context);if(!host)return E_POINTER;
    auto hr=host->CheckThread();if(FAILED(hr))return hr;
    if(host->materialBackend!=2||host->ownedFaulted)return E_ACCESSDENIED;
    if(count>3||(!lenses&&count)||sequence<=host->ownedSequence)return E_INVALIDARG;
    for(uint32_t i=0;i<count;i++){
        auto const& l=lenses[i];
        if(!std::isfinite(l.centerX)||!std::isfinite(l.centerY)||!std::isfinite(l.width)||!std::isfinite(l.height)
            ||!std::isfinite(l.radius)||!std::isfinite(l.opacity)||!std::isfinite(l.emphasis)||!std::isfinite(l.lightInk)
            ||l.width<=0||l.height<=0||l.width>64||l.height>64||l.radius<0||l.radius>32
            ||l.opacity<0||l.opacity>1||l.emphasis<0||l.emphasis>1)return E_INVALIDARG;
    }
    host->ownedSequence=sequence;
    try{
        RECT bounds{};check_bool(GetWindowRect(host->window,&bounds));auto const& m=host->ownedMapping;
        host->ownedEligible=eligible!=0&&host->ownedImageSet&&bounds.left>=m.monitorX&&bounds.top>=m.monitorY
            &&int64_t(bounds.right)<=int64_t(m.monitorX)+m.monitorWidth&&int64_t(bounds.bottom)<=int64_t(m.monitorY)+m.monitorHeight;
        if(!host->ownedEligible||!host->liquidEnabled||host->frame.solid||!host->visible){host->ApplyMaterial();return S_FALSE;}
        check_hresult(host->d3d->GetDeviceRemovedReason());
        std::vector<float> key{float(bounds.left),float(bounds.top),float(host->cachedWidth),float(host->cachedHeight),
            float(host->dpi),host->frame.radius,host->EffectiveTension(),float(count)};
        for(uint32_t i=0;i<count;i++){auto const& l=lenses[i];key.insert(key.end(),{l.centerX,l.centerY,l.width,l.height,l.radius,l.opacity,l.emphasis,l.lightInk});}
        if(host->ownedSurface&&key==host->ownedFrameKey){host->ApplyMaterial();return S_OK;}
        gd::OwnedImageGlassFrame f{};
        f.widthPx=uint32_t(host->cachedWidth);f.heightPx=uint32_t(host->cachedHeight);
        f.dpiScaleX=f.dpiScaleY=host->Scale();f.windowOriginPxX=float(bounds.left);f.windowOriginPxY=float(bounds.top);
        f.imageOriginPxX=m.imageX;f.imageOriginPxY=m.imageY;f.imageDisplayWidthPx=m.displayedWidth;f.imageDisplayHeightPx=m.displayedHeight;
        f.bodyBoundsPx={0,0,float(f.widthPx),float(f.heightPx)};f.radiusDip=host->frame.radius;f.surfaceTension=host->EffectiveTension();
        f.backgroundRed=((m.backgroundArgb>>16)&255)/255.0f;f.backgroundGreen=((m.backgroundArgb>>8)&255)/255.0f;f.backgroundBlue=(m.backgroundArgb&255)/255.0f;
        f.peek=host->frame.height>52.01f;f.musicControls=count!=0;f.lensCount=count;
        for(uint32_t i=0;i<count;i++){
            auto const& l=lenses[i];auto scale=host->Scale();
            f.lenses[i]={{(l.centerX-l.width*.5f)*scale,(l.centerY-l.height*.5f)*scale,l.width*scale,l.height*scale},l.radius,l.opacity,l.lightInk>.5f?0.0f:1.0f,l.emphasis};
        }
        // Complete D3D work BEFORE surface BeginDraw; never nest/interleave a
        // material draw with Composition's borrowed drawing context.
        auto bitmap=host->ownedRenderer->Render(host->cacheContext.get(),f);
        check_hresult(host->d3d->GetDeviceRemovedReason());
        bool resized=!host->ownedSurface||host->ownedSurface.Size().Width!=f.widthPx||host->ownedSurface.Size().Height!=f.heightPx;
        if(resized){
            if(host->ownedBrush)host->ownedBrush.Surface(nullptr);
            host->ownedSurface=nullptr; // release before allocating the replacement
            host->ownedSurface=host->CreateSurface(int(f.widthPx),int(f.heightPx));
        }
        host->Draw(host->ownedSurface,float(host->dpi),[&](ID2D1DeviceContext* dc){
            dc->DrawBitmap(bitmap.get(),D2D1::RectF(0,0,f.widthPx/host->Scale(),f.heightPx/host->Scale()),1,D2D1_INTERPOLATION_MODE_LINEAR);
        });
        if(!host->ownedBrush){host->ownedBrush=host->compositor.CreateSurfaceBrush();host->ownedBrush.Stretch(CompositionStretch::None);}
        host->ownedBrush.Surface(host->ownedSurface);host->glass.Brush(host->ownedBrush);host->ownedFrameKey=std::move(key);host->ApplyMaterial();return S_OK;
    }catch(...){host->TripOwned();return to_hresult();}
}
extern "C" __declspec(dllexport) HRESULT __stdcall GDDestroy(void* context)
{
    auto host=gd::FromContext(context);if(!host)return S_OK;
    auto hr=host->CheckThread();if(FAILED(hr))return hr;const HRESULT cleanup=host->Shutdown();delete static_cast<gd::HostRef*>(context);return cleanup;
}
extern "C" __declspec(dllexport) HRESULT __stdcall GDGetContext(void* context,GDContext* result)
{
    auto host=gd::FromContext(context);if(!host||!result)return E_POINTER;
    auto hr=host->CheckThread();if(FAILED(hr))return hr;*result=host->Context();return S_OK;
}
extern "C" __declspec(dllexport) HRESULT __stdcall GDGetComposition(void* context,::IInspectable** compositor,::IInspectable** content)
{
    if(!context||!compositor||!content)return E_POINTER;*compositor=nullptr;*content=nullptr;
    auto host=gd::FromContext(context);auto hr=host->CheckThread();if(FAILED(hr))return hr;
    auto compositorCopy=host->compositor;auto contentCopy=host->content;
    *compositor=static_cast<::IInspectable*>(detach_abi(compositorCopy));*content=static_cast<::IInspectable*>(detach_abi(contentCopy));return S_OK;
}
extern "C" __declspec(dllexport) HRESULT __stdcall GDCreateCompositionPath(void* context,GDPoint const* points,unsigned count,float rounding,::IInspectable** result)
{
    auto host=gd::FromContext(context);if(!host||!points||!result)return E_POINTER;*result=nullptr;
    auto hr=host->CheckThread();if(FAILED(hr))return hr;
    if(host->ownedFaulted&&(host->materialBackend==2||false))return E_ABORT;
    if(count<3||count>64||!std::isfinite(rounding)||rounding<0||rounding>100)return E_INVALIDARG;
    for(unsigned i=0;i<count;++i)if(!std::isfinite(points[i].x)||!std::isfinite(points[i].y)||std::abs(points[i].x)>4096||std::abs(points[i].y)>4096)return E_INVALIDARG;
    try
    {
        if(!host->geometryFactory)check_hresult(D2D1CreateFactory(D2D1_FACTORY_TYPE_MULTI_THREADED,host->geometryFactory.put()));
        auto source=make<gd::GeometrySource>(host->geometryFactory.get(),std::vector<GDPoint>(points,points+count),rounding);
        CompositionPath path(source);*result=static_cast<::IInspectable*>(detach_abi(path));return S_OK;
    }catch(...){return to_hresult();}
}
static HRESULT ApplyFrameWithTension(void* context,GDFrame const* frame,float tension)
{
    auto host=gd::FromContext(context);if(!host||!frame)return E_POINTER;
    auto hr=host->CheckThread();if(FAILED(hr))return hr;
    if(host->ownedFaulted&&(host->materialBackend==2||false))return E_ABORT;
    if(!gd::ValidFrameRequest(*frame,tension))return E_INVALIDARG;
    try
    {
        gd::FramePixelGeometry geometry{};
        check_hresult(host->ComputeFramePixelGeometry(*frame,host->dpi,host->presentationOffsetX,host->presentationOffsetY,geometry));
        host->frame=*frame;host->surfaceTension=tension;
        host->glassSceneShapes.clear();host->glassSceneSequence=0;host->appliedGlassSceneSequence=0;
        check_hresult(host->PositionHostWindows(0,0,geometry.allocationWidth,geometry.allocationHeight,SWP_NOMOVE));
        if(host->ownedFaulted&&(host->materialBackend==2||false))return E_ABORT;
        host->Layout();host->UpdateAccessibleGeometry();return host->ownedFaulted?E_ABORT:S_OK;
    }catch(...){auto failure=to_hresult();host->TripOwned(failure);try{host->UpdateAccessibleGeometry();}catch(...){}return failure;}
}
// Additive entry; GDFrame layout and the original export remain unchanged.
extern "C" __declspec(dllexport) HRESULT __stdcall GDApplyFrame(void* context,GDFrame const* frame)
{return ApplyFrameWithTension(context,frame,0);}
extern "C" __declspec(dllexport) HRESULT __stdcall GDApplyFrameWithTension(void* context,GDFrame const* frame,float tension)
{return ApplyFrameWithTension(context,frame,tension);}
static bool ValidGlassScene(GDGlassShapeV1 const* shapes,uint32_t count,uint32_t stride,
    uint32_t structSize,uint32_t version,std::vector<GDGlassShapeV1>& snapshot) noexcept
{
    if(version!=1||structSize!=static_cast<uint32_t>(sizeof(GDGlassShapeV1))||
        stride<structSize||stride>4096u||count>32||(!shapes&&count))return false;
    try
    {
        snapshot.clear();snapshot.reserve(count);
        auto const* bytes=reinterpret_cast<uint8_t const*>(shapes);
        for(uint32_t index=0;index<count;++index)
        {
            GDGlassShapeV1 value{};
            std::memcpy(&value,bytes+static_cast<size_t>(index)*stride,sizeof(value));
            if(value.id==0||(value.layer!=2&&value.layer!=3)||value.enabled>1||
                !std::isfinite(value.xDip)||!std::isfinite(value.yDip)||!std::isfinite(value.widthDip)||
                !std::isfinite(value.heightDip)||!std::isfinite(value.radiusDip)||
                !std::isfinite(value.materialize)||!std::isfinite(value.press)||
                std::abs(value.xDip)>4096||std::abs(value.yDip)>4096||
                value.widthDip<=0||value.heightDip<=0||value.widthDip>4096||value.heightDip>4096||
                value.radiusDip<0||value.radiusDip>4096||value.materialize<0||value.materialize>1||
                value.press<0||value.press>1)return false;
            for(auto const& prior:snapshot)if(prior.id==value.id)return false;
            snapshot.push_back(value);
        }
        return true;
    }
    catch(...){snapshot.clear();return false;}
}

static HRESULT ApplyContinuousSceneFrame(void* context,GDFrame const* frame,float tension,float screenX,float screenY,
    uint64_t sequence,std::vector<GDGlassShapeV1>&& shapes) noexcept
{
    gd::HostRef host;
    try
    {
        host=gd::FromContext(context);if(!host||!frame)return E_POINTER;
        auto hr=host->CheckThread();if(FAILED(hr))return hr;
        if(host->ownedFaulted&&(host->materialBackend==2||false))return E_ABORT;
        if(!gd::ValidFrameRequest(*frame,tension))return E_INVALIDARG;
        if(!std::isfinite(screenX)||!std::isfinite(screenY)||std::abs(double(screenX))>10000000||std::abs(double(screenY))>10000000)return E_INVALIDARG;
        const bool continuous=host->liquidCaptureCandidate;
        int targetX=continuous?int(std::floor(screenX)):int(std::lround(screenX));
        int targetY=continuous?int(std::floor(screenY)):int(std::lround(screenY));
        const float offsetX=continuous?screenX-static_cast<float>(targetX):0.0f;
        const float offsetY=continuous?screenY-static_cast<float>(targetY):0.0f;
        gd::FramePixelGeometry geometry{};
        hr=host->ComputeFramePixelGeometry(*frame,host->dpi,offsetX,offsetY,geometry);
        if(FAILED(hr))return hr;
        const int width=geometry.allocationWidth,height=geometry.allocationHeight;
        if(int64_t(targetX)+width>std::numeric_limits<int>::max()||
            int64_t(targetY)+height>std::numeric_limits<int>::max())return E_BOUNDS;
        const GDFrame oldFrame=host->frame;
        const float oldTension=host->surfaceTension;
        const float oldOffsetX=host->presentationOffsetX,oldOffsetY=host->presentationOffsetY;
        std::vector<GDGlassShapeV1> oldShapes=std::move(host->glassSceneShapes);
        const uint64_t oldSequence=host->glassSceneSequence,oldAppliedSequence=host->appliedGlassSceneSequence;
        host->frame=*frame;host->surfaceTension=tension;
        host->presentationOffsetX=offsetX;host->presentationOffsetY=offsetY;
        host->glassSceneShapes=std::move(shapes);host->glassSceneSequence=sequence;
        host->appliedGlassSceneSequence=sequence;
        hr=host->PositionHostWindows(targetX,targetY,width,height,SWP_NOACTIVATE);
        if(FAILED(hr))
        {
            host->frame=oldFrame;host->surfaceTension=oldTension;
            host->presentationOffsetX=oldOffsetX;host->presentationOffsetY=oldOffsetY;
            host->glassSceneShapes=std::move(oldShapes);host->glassSceneSequence=oldSequence;
            host->appliedGlassSceneSequence=oldAppliedSequence;
            return hr;
        }
        if(host->ownedFaulted&&(host->materialBackend==2||false))return E_ABORT;
        host->Layout();host->UpdateAccessibleGeometry();
        if(host->ownedFaulted&&(host->materialBackend==2||false))return E_ABORT;
        return S_OK;
    }
    catch(...)
    {
        const HRESULT failure=to_hresult();
        if(host){host->TripOwned(failure);try{host->UpdateAccessibleGeometry();}catch(...){}}
        return failure;
    }
}
extern "C" __declspec(dllexport) HRESULT __stdcall GDApplyContinuousPositionedFrameWithTension(
    void* context,GDFrame const* frame,float tension,float screenX,float screenY) noexcept
{
    std::vector<GDGlassShapeV1> empty;
    return ApplyContinuousSceneFrame(context,frame,tension,screenX,screenY,0,std::move(empty));
}
extern "C" __declspec(dllexport) HRESULT __stdcall GDApplySceneFrameV1(void* context,
    GDFrame const* frame,float tension,float screenX,float screenY,uint64_t sequence,
    GDGlassShapeV1 const* shapes,uint32_t count,uint32_t stride,uint32_t structSize,uint32_t version) noexcept
{
    std::vector<GDGlassShapeV1> snapshot;
    if(!ValidGlassScene(shapes,count,stride,structSize,version,snapshot))return E_INVALIDARG;
    return ApplyContinuousSceneFrame(context,frame,tension,screenX,screenY,sequence,std::move(snapshot));
}
extern "C" __declspec(dllexport) HRESULT __stdcall GDApplyPositionedFrameWithTension(
    void* context,GDFrame const* frame,float tension,int screenX,int screenY) noexcept
{return GDApplyContinuousPositionedFrameWithTension(context,frame,tension,float(screenX),float(screenY));}
extern "C" __declspec(dllexport) HRESULT __stdcall GDSetPlacement(void* context,int x,int y)
{
    auto host=gd::FromContext(context);if(!host)return E_POINTER;
    auto hr=host->CheckThread();if(FAILED(hr))return hr;
    if(host->ownedFaulted&&(host->materialBackend==2||false))return E_ABORT;
    gd::FramePixelGeometry geometry{};
    hr=host->ComputeFramePixelGeometry(host->frame,host->dpi,0.0f,0.0f,geometry);
    if(FAILED(hr))return hr;
    const float previousOffsetX=host->presentationOffsetX,previousOffsetY=host->presentationOffsetY;
    host->presentationOffsetX=host->presentationOffsetY=0.0f;
    const HRESULT placement=host->PositionHostWindows(x,y,geometry.allocationWidth,geometry.allocationHeight,SWP_NOACTIVATE);
    if(FAILED(placement)){
        host->presentationOffsetX=previousOffsetX;host->presentationOffsetY=previousOffsetY;
        host->TripOwned(placement);return placement;
    }
    try{host->Layout();host->UpdateAccessibleGeometry();}
    catch(...){const HRESULT failure=to_hresult();host->TripOwned(failure);return failure;}
    return host->ownedFaulted?E_ABORT:S_OK;
}
extern "C" __declspec(dllexport) HRESULT __stdcall GDSetVisible(void* context,int visible)
{
    auto host=gd::FromContext(context);if(!host)return E_POINTER;auto hr=host->CheckThread();if(FAILED(hr))return hr;
    if((host->materialBackend==2||false)&&host->ownedFaulted&&visible)return E_ABORT;
    try
    {
        bool becameVisible=!host->visible&&visible!=0;
        host->visible=visible!=0;
        host->UpdateLiquid();
        if(visible&&host->ownedFaulted)return E_ABORT;
        if(!host->visible)
        {
            TRACKMOUSEEVENT cancel{sizeof(cancel),TME_CANCEL|TME_LEAVE,host->window,0};TrackMouseEvent(&cancel);
            host->tracking=false;
            bool wasCaptured=host->captured;host->captured=false;
            if(GetCapture()==host->window)ReleaseCapture();
            if(wasCaptured)host->Emit(15);
        }
        if(host->visible){ShowWindow(host->glassWindow,SW_SHOWNOACTIVATE);ShowWindow(host->window,SW_SHOWNOACTIVATE);}
        else{ShowWindow(host->window,SW_HIDE);ShowWindow(host->glassWindow,SW_HIDE);}
        host->UpdateShadow();host->UpdateAccessibleGeometry();
        if(visible&&host->ownedFaulted)return E_ABORT;
        if(becameVisible)host->EnsureTopmost();
        return S_OK;
    }catch(...){auto failure=to_hresult();host->TripOwned(failure);return failure;}
}
extern "C" __declspec(dllexport) HRESULT __stdcall GDSetLiquidGlassMotionActive(void* context,int active)
{
    auto host=gd::FromContext(context);if(!host)return E_POINTER;
    auto result=host->CheckThread();if(FAILED(result))return result;
    host->captureMotionActive=active!=0;
    return S_OK;
}
extern "C" __declspec(dllexport) HRESULT __stdcall GDSetLiquidGlassEnabled(void* context,int enabled)
{
    auto host=gd::FromContext(context);if(!host)return E_POINTER;
    auto hr=host->CheckThread();if(FAILED(hr))return hr;
    if(host->ownedFaulted&&(host->materialBackend==2||false))return E_ABORT;
    try{host->liquidEnabled=enabled!=0;host->Layout();host->UpdateAccessibleGeometry();return S_OK;}catch(...){auto failure=to_hresult();host->TripOwned(failure);try{host->UpdateAccessibleGeometry();}catch(...){}return failure;}
}
extern "C" __declspec(dllexport) HRESULT __stdcall GDSetLiquidGlassSettings(void* context,GDLiquidGlassSettings const* settings)
{
    auto host=gd::FromContext(context);if(!host||!settings)return E_POINTER;
    auto hr=host->CheckThread();if(FAILED(hr))return hr;
    if(!host->liquidCaptureCandidate)return E_ACCESSDENIED;
    if(settings->size!=sizeof(GDLiquidGlassSettings)||settings->version!=1)return E_INVALIDARG;
    try
    {
        host->liquidSettings=*settings;
        if(host->liquidCapture)check_hresult(host->liquidCapture->SetSettings(host->liquidSettings));
        host->shadowMaterialScale=-1.0f;host->shadowThemeWeight=-1.0f;
        host->UpdateShadow();
        host->UpdateLiquidCapture();
        return S_OK;
    }
    catch(...){return to_hresult();}
}
extern "C" __declspec(dllexport) HRESULT __stdcall GDUpdateLiquidGlassCapture(void* context)
{
    auto host=gd::FromContext(context);if(!host)return E_POINTER;
    auto hr=host->CheckThread();if(FAILED(hr))return hr;
    if(!host->liquidCaptureCandidate)return E_ACCESSDENIED;
    host->UpdateLiquidCapture();return S_OK;
}
extern "C" __declspec(dllexport) HRESULT __stdcall GDResetLiquidGlassCapture(void* context)
{
    auto host=gd::FromContext(context);if(!host)return E_POINTER;
    auto hr=host->CheckThread();if(FAILED(hr))return hr;
    if(!host->liquidCaptureCandidate)return E_ACCESSDENIED;
    host->StopLiquidCapture();host->liquidCaptureFailure=S_OK;host->liquidCaptureFuseTripped=false;
    host->UpdateLiquidCapture();return S_OK;
}
extern "C" __declspec(dllexport) HRESULT __stdcall GDStopLiquidGlassCapture(void* context)
{
    auto host=gd::FromContext(context);if(!host)return E_POINTER;
    auto hr=host->CheckThread();if(FAILED(hr))return hr;
    if(!host->liquidCaptureCandidate)return S_FALSE;
    host->StopLiquidCapture();return S_OK;
}
extern "C" __declspec(dllexport) HRESULT __stdcall GDGetLiquidGlassInk(void* context,int* available,int* isDark)
{
    auto host=gd::FromContext(context);if(!host||!available||!isDark)return E_POINTER;
    auto hr=host->CheckThread();if(FAILED(hr))return hr;
    *available=host->liquidCapture&&host->liquidCapture->HasPresentedFrame()?1:0;
    *isDark=*available&&host->liquidCapture->InkIsDark()?1:0;
    return S_OK;
}
extern "C" __declspec(dllexport) HRESULT __stdcall GDGetLiquidGlassInkColorSurface(void* context,::IInspectable** surface,uint64_t* generation)
{
    auto host=gd::FromContext(context);if(!host||!surface||!generation)return E_POINTER;
    auto hr=host->CheckThread();if(FAILED(hr))return hr;
    *surface=nullptr;*generation=host->liquidInkSurfaceGeneration;
    if(!host->liquidCapture||!host->liquidCapturePresented||!host->liquidInkSurface)return S_FALSE;
    auto* value=reinterpret_cast<::IInspectable*>(get_abi(host->liquidInkSurface));
    if(!value)return S_FALSE;
    value->AddRef();*surface=value;return S_OK;
}
extern "C" __declspec(dllexport) HRESULT __stdcall GDGetLiquidGlassStatus(void* context,wchar_t* text,unsigned capacity)
{
    auto host=gd::FromContext(context);if(!host||!text)return E_POINTER;
    auto hr=host->CheckThread();if(FAILED(hr))return hr;if(!capacity)return E_INVALIDARG;
    try
    {
        std::wstring status;
        if(host->materialBackend==2)status=host->ownedFaulted?L"Tripped: owned-image material stopped; restart authorization required":
            !host->liquidEnabled?L"Disabled: owned-image candidate; no capture":
            host->ownedImageSet&&host->ownedEligible?L"Experimental: wallpaper-only optical material; external windows are not refracted":
            L"Fallback: solid; wallpaper candidate unavailable or outside capsule/peek";
        else if(host->liquidCaptureCandidate)
        {
            if(!host->liquidEnabled)status=L"DisabledByUser: liquid glass capture";
            else if(host->frame.solid)status=L"SolidByAccessibility: solid material requested";
            else if(host->liquidCaptureFuseTripped)status=L"FaultedSafety: liquid glass capture fuse tripped; system frost";
            else if(!host->visible)status=L"Paused: liquid glass capture; host hidden";
            else if(host->liquidCaptureGeometryRetrying)
            {
                const ULONGLONG elapsed=host->liquidCaptureRetryStartedMs?GetTickCount64()-host->liquidCaptureRetryStartedMs:0;
                wchar_t detail[48]{};swprintf_s(detail,std::size(detail),L" %llums",static_cast<unsigned long long>(elapsed));
                status=L"RetryingGeometry:";status+=detail;
            }
            else if(host->liquidCaptureSurfaceRetrying)status=L"RetryingSurface: preserving the last attached frame";
            else if(FAILED(host->liquidCaptureFailure))
            {
                wchar_t detail[9]{};swprintf_s(detail,L"%08X",static_cast<unsigned>(host->liquidCaptureFailure));
                status=L"Fallback: liquid glass capture failed; HRESULT 0x";status+=detail;status+=L"; system frost";
            }
            else status=host->liquidCapturePresented?L"Active: liquid glass capture":L"WaitingFirstFrame: liquid glass capture";
        }
        else if(host->materialBackend==0)status=L"Fallback: solid backend; no capture";
        else status=!host->liquidEnabled?L"Disabled: system frost; no desktop capture":
            !host->visible?L"Paused: host hidden; capture-free optical material enabled":
            host->frame.solid||host->fallback?L"Fallback: solid/system policy; no desktop capture":
            host->opticalReady?L"Active: system frost + procedural curved light; no desktop capture":
            L"Fallback: system frost; optical shader unavailable; no desktop capture";
        std::wstring prefix=L"Liquid Glass plugin ";prefix+=gd::liquidglass::diagnostics::kLiquidGlassPluginVersion;
        prefix+=L" · shader ";prefix+=gd::liquidglass::diagnostics::kLiquidGlassShaderVersion;prefix+=L" · ";prefix+=status;
        status=std::move(prefix);
        if(status.size()+1>capacity)return HRESULT_FROM_WIN32(ERROR_INSUFFICIENT_BUFFER);
        wcscpy_s(text,capacity,status.c_str());return S_OK;
    }catch(...){return to_hresult();}
}
extern "C" __declspec(dllexport) HRESULT __stdcall GDSaveLiquidGlassDiagnosticFrame(void* context,wchar_t const* path)
{
    auto host=gd::FromContext(context);if(!host)return E_POINTER;
    auto hr=host->CheckThread();if(FAILED(hr))return hr;
    return host->liquid?host->liquid->SaveDiagnostic(path):HRESULT_FROM_WIN32(ERROR_NOT_READY);
}
extern "C" __declspec(dllexport) HRESULT __stdcall GDBeginDiagnosticSnapshot(void* context,unsigned durationMilliseconds)
{
    auto host=gd::FromContext(context);if(!host)return E_POINTER;
    auto hr=host->CheckThread();if(FAILED(hr))return hr;
    if(!host->visible||!host->liquidEnabled||host->frame.solid||host->fallback||!host->liquid)
        return HRESULT_FROM_WIN32(ERROR_NOT_READY);
    return host->liquid->BeginDiagnosticSnapshot(durationMilliseconds);
}
extern "C" __declspec(dllexport) HRESULT __stdcall GDEndDiagnosticSnapshot(void* context)
{
    auto host=gd::FromContext(context);if(!host)return E_POINTER;
    auto hr=host->CheckThread();if(FAILED(hr))return hr;
    return host->liquid?host->liquid->EndDiagnosticSnapshot():S_FALSE;
}
extern "C" __declspec(dllexport) HRESULT __stdcall GDGetDiagnosticSnapshotStatus(void* context,wchar_t* text,unsigned capacity)
{
    auto host=gd::FromContext(context);if(!host||!text)return E_POINTER;
    auto hr=host->CheckThread();if(FAILED(hr))return hr;if(!capacity)return E_INVALIDARG;
    try
    {
        auto status=host->liquid?host->liquid->DiagnosticSnapshotStatus():L"QA final-UI snapshot: inactive; renderer unavailable";
        if(status.size()+1>capacity)return HRESULT_FROM_WIN32(ERROR_INSUFFICIENT_BUFFER);
        wcscpy_s(text,capacity,status.c_str());return S_OK;
    }catch(...){return to_hresult();}
}
extern "C" __declspec(dllexport) HRESULT __stdcall GDSetAccessible(void* context,wchar_t const* name,GDAccessible const* items,unsigned count)
{
    auto host=gd::FromContext(context);if(!host||(!items&&count))return E_POINTER;auto hr=host->CheckThread();if(FAILED(hr))return hr;
    if(count>128)return E_INVALIDARG;
    try
    {
        std::vector<gd::AccessibleItem> values;values.reserve(count);
        for(unsigned index=0;index<count;++index)
        {
            auto const& item=items[index];
            if(!std::isfinite(item.x)||!std::isfinite(item.y)||!std::isfinite(item.width)||!std::isfinite(item.height)||item.width<0||item.height<0||!std::isfinite(item.value)||!std::isfinite(item.minimum)||!std::isfinite(item.maximum)||item.maximum<item.minimum)return E_INVALIDARG;
            if(std::any_of(values.begin(),values.end(),[&](auto const& value){return value.id==item.id;}))return E_INVALIDARG;
            values.push_back({item.id,item.name?item.name:L"",item.x,item.y,item.width,item.height,item.enabled!=0,item.isRange!=0,std::clamp(item.value,item.minimum,item.maximum),item.minimum,item.maximum});
        }
        {std::lock_guard lock(host->accessibility->mutex);host->accessibility->name=name?name:L"GlassDesk 音乐小组件";host->accessibility->items=std::move(values);}
        if(UiaClientsAreListening())UiaRaiseStructureChangedEvent(host->provider.Get(),StructureChangeType_ChildrenInvalidated,nullptr,0);
        return S_OK;
    }catch(...){return to_hresult();}
}
extern "C" __declspec(dllexport) HRESULT __stdcall GDRenderSurface(void* context,float width,float height,GDOp const* operations,unsigned count,::IInspectable** surface)
{
    if(!context||!surface)return E_POINTER;*surface=nullptr;auto host=gd::FromContext(context);auto hr=host->CheckThread();if(FAILED(hr))return hr;
    try{auto value=host->Render(width,height,operations,count);*surface=static_cast<::IInspectable*>(detach_abi(value));return S_OK;}catch(...){return to_hresult();}
}
extern "C" __declspec(dllexport) HRESULT __stdcall GDGetNativeAbiInfo(GDNativeAbiInfo* result)
{
    if(!result)return E_POINTER;if(result->size!=sizeof(GDNativeAbiInfo))return E_INVALIDARG;
    *result={sizeof(GDNativeAbiInfo),2,sizeof(GDOp),sizeof(GDOpV2),1u|2u|4u|8u|16u|32u,0};return S_OK;
}
extern "C" __declspec(dllexport) HRESULT __stdcall GDConfigureTypographyV2(void* context,uint32_t profile,uint32_t tokenRevision,wchar_t const* qaFontPath)
{
    auto host=gd::FromContext(context);if(!host)return E_POINTER;auto hr=host->CheckThread();if(FAILED(hr))return hr;
    if(profile>2||tokenRevision!=1)return E_INVALIDARG;
    if(profile!=2&&qaFontPath&&qaFontPath[0])return E_INVALIDARG;
    if(qaFontPath&&wcsnlen_s(qaFontPath,32768)>=32768)return E_INVALIDARG;
    try{host->Typography().Configure(gd::typography::Profile(profile),tokenRevision,qaFontPath?qaFontPath:L"");return S_OK;}catch(...){return to_hresult();}
}
extern "C" __declspec(dllexport) HRESULT __stdcall GDGetTypographyCacheStats(void* context,GDTypographyCacheStats* result)
{
    auto host=gd::FromContext(context);if(!host||!result)return E_POINTER;*result={};auto hr=host->CheckThread();if(FAILED(hr))return hr;
    try
    {
        if(host->typographyEngine){auto stats=host->typographyEngine->Stats();*result={uint64_t(stats.formats),uint64_t(stats.layouts),uint64_t(stats.formatHits),uint64_t(stats.layoutHits),uint64_t(stats.layoutsCreated)};}
        return S_OK;
    }catch(...){return to_hresult();}
}
extern "C" __declspec(dllexport) HRESULT __stdcall GDMeasureTextV2(void* context,GDOpV2 const* operation,uint32_t stride,GDTextMetrics* result)
{
    if(!result)return E_POINTER;*result={};auto host=gd::FromContext(context);if(!host||!operation)return E_POINTER;
    auto hr=host->CheckThread();if(FAILED(hr))return hr;if(stride!=sizeof(GDOpV2))return E_INVALIDARG;
    try
    {
        host->ValidateV2(*operation);auto const& op=operation->legacy;
        if((op.kind!=2&&op.kind!=6&&op.kind!=7)||!op.text||op.width<=0||op.height<=0)return E_INVALIDARG;
        auto layout=host->Typography().CreateLayout(host->TextRequest(*operation));auto const& metrics=layout->metrics;
        *result={metrics.advance,metrics.widthIncludingTrailingWhitespace,metrics.height,metrics.baseline,metrics.lineHeight,metrics.lineCount};return S_OK;
    }catch(...){return to_hresult();}
}
extern "C" __declspec(dllexport) HRESULT __stdcall GDMeasureTextInkBoundsV2(void* context,GDOpV2 const* operation,uint32_t stride,GDInkBounds* result)
{
    if(!result)return E_POINTER;*result={};auto host=gd::FromContext(context);if(!host||!operation)return E_POINTER;
    auto hr=host->CheckThread();if(FAILED(hr))return hr;if(host->ownedFaulted)return E_ABORT;if(stride!=sizeof(GDOpV2))return E_INVALIDARG;
    try
    {
        host->ValidateV2(*operation);auto const& op=operation->legacy;
        if((op.kind!=2&&op.kind!=7)||operation->textRole==0||op.width<=0||op.height<=0)return E_INVALIDARG;
        auto layout=host->Typography().CreateLayout(host->TextRequest(*operation));
        auto bounds=gd::typography::InkBoundsForLayout(layout->textLayout.get());
        if(!std::isfinite(bounds.left)||!std::isfinite(bounds.top)||!std::isfinite(bounds.right)||!std::isfinite(bounds.bottom)
            ||bounds.right<bounds.left||bounds.bottom<bounds.top)return E_FAIL;
        *result={bounds.left,bounds.top,bounds.right,bounds.bottom};return S_OK;
    }catch(...){*result={};return to_hresult();}
}
extern "C" __declspec(dllexport) HRESULT __stdcall GDRenderSurfaceV2(void* context,float width,float height,GDOpV2 const* operations,uint32_t count,uint32_t stride,::IInspectable** surface)
{
    if(!surface)return E_POINTER;*surface=nullptr;auto host=gd::FromContext(context);if(!host)return E_POINTER;
    auto hr=host->CheckThread();if(FAILED(hr))return hr;
    if(stride!=sizeof(GDOpV2))
    {host->renderOpIndex=std::numeric_limits<uint32_t>::max();host->LogRenderV2Failure(L"validate-stride",E_INVALIDARG,L"abi-error",width,height,operations,count);return E_INVALIDARG;}
    try{auto value=host->RenderV2(width,height,operations,count);*surface=static_cast<::IInspectable*>(detach_abi(value));return S_OK;}
    catch(hresult_error const& error)
    {host->LogRenderV2Failure(L"GDRenderSurfaceV2-return",error.code(),L"hresult_error",width,height,operations,count);return error.code();}
    catch(...)
    {hr=to_hresult();host->LogRenderV2Failure(L"GDRenderSurfaceV2-return",hr,L"unknown",width,height,operations,count);return hr;}
}

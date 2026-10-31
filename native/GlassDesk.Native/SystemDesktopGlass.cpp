#include "SystemDesktopGlass.h"
#include "CleanupResult.h"
#include "SystemGlassColorEffect.h"
#include "DesktopSourceViewport.h"
#include <cwchar>
#include <vector>
#include <algorithm>
#include <cmath>
#include <windows.ui.composition.interop.h>
using namespace winrt;
using namespace winrt::Windows::UI::Composition;
namespace gd::systemglass {
namespace {
HRESULT SameInkBrush(winrt::Windows::UI::Composition::CompositionEffectBrush const& first,
    winrt::Windows::UI::Composition::CompositionEffectBrush const& second,bool& same) noexcept {
    same=false;
    try {
        winrt::com_ptr<::IUnknown> firstIdentity,secondIdentity;
        auto firstUnknown=reinterpret_cast<::IUnknown*>(winrt::get_abi(first));
        auto secondUnknown=reinterpret_cast<::IUnknown*>(winrt::get_abi(second));
        winrt::check_hresult(firstUnknown->QueryInterface(IID_PPV_ARGS(firstIdentity.put())));
        winrt::check_hresult(secondUnknown->QueryInterface(IID_PPV_ARGS(secondIdentity.put())));
        same=firstIdentity.get()==secondIdentity.get();return S_OK;
    }catch(...){return winrt::to_hresult();}
}
HRESULT ResolveInkBrush(ink::Binding const& binding,CompositionEffectBrush& result) noexcept {
    result=nullptr;
    try {
        result=binding.brush.get();
        return result?S_OK:S_FALSE;
    }catch(...){return winrt::to_hresult();}
}
}

HRESULT SystemDesktopGlass::EnsureInkFactory() noexcept {
    // A published factory is usable only with its matching white source.
    // Never retry or silently repair a partially initialized pair.
    if(inkFactory||inkWhite)return inkFactory&&inkWhite?S_OK:E_UNEXPECTED;
    if(!resources||!resources->compositor)return E_ABORT;
    try {
        using namespace gd::systemglass::ink;
        using winrt::Windows::UI::Composition::CompositionEffectSourceParameter;

        auto desktop=CompositionEffectSourceParameter(L"Desktop");
        auto white=CompositionEffectSourceParameter(L"InkWhite");
        auto mask=CompositionEffectSourceParameter(L"GlyphMask");

        inkStage=InkStage::CreatingEffectFactory;
        auto graph=MakeInkGraph(desktop,white,mask);
        auto dynamicProperties=winrt::single_threaded_vector<hstring>();
        for(auto const& property:graph.animatableProperties)dynamicProperties.Append(property);
        auto factory=resources->compositor.CreateEffectFactory(graph.root,dynamicProperties);
        inkStage=InkStage::EffectFactoryCreated;
        inkStage=InkStage::CreatingWhiteSource;
        auto whiteBrush=resources->compositor.CreateColorBrush(winrt::Windows::UI::Color{255,255,255,255});
        // Publish only after both creations succeed; moves do not call WinRT.
        inkFactory=std::move(factory);
        inkWhite=std::move(whiteBrush);
        inkStage=InkStage::FactoryReady;
        return S_OK;
    }catch(...){return winrt::to_hresult();}
}

HRESULT SystemDesktopGlass::SetInkBrushTint(ink::Binding& binding,float tint) noexcept {
    CompositionEffectBrush brush{nullptr};
    auto resolve=ResolveInkBrush(binding,brush);if(resolve!=S_OK)return resolve;
    try {
        const float value=std::clamp(tint,0.0f,1.0f);
        inkStage=ink::InkStage::SettingTintProperty;
        brush.Properties().InsertVector4(L"InkTint.Coefficients",
            winrt::Windows::Foundation::Numerics::float4{0.0f,1.0f-value,value,0.0f});
        return S_OK;
    }catch(...){return winrt::to_hresult();}
}

HRESULT SystemDesktopGlass::SetInkBrushFrame(ink::Binding& binding,Frame const& frame) noexcept {
    CompositionEffectBrush brush{nullptr};
    auto resolve=ResolveInkBrush(binding,brush);if(resolve!=S_OK)return resolve;
    if(!binding.desktop)return E_ABORT;
    if(!std::isfinite(frame.dpiScale)||frame.dpiScale<0.5f||frame.dpiScale>4.0f)return E_INVALIDARG;
    try {
        inkStage=ink::InkStage::MappingFrame;
        constexpr float zoom=1.08f;
        const float scale=zoom/frame.dpiScale;
        const float centerX=frame.widthDips*0.5f;
        const float centerY=frame.heightDips*0.5f;
        const float sourceX=frame.bodySourceXPx+
            (centerX+(binding.x-centerX)/zoom)*frame.dpiScale;
        const float sourceY=frame.bodySourceYPx+
            (centerY+(binding.y-centerY)/zoom)*frame.dpiScale;

        // VisualSurface coordinates are retained in the source canvas' pixel
        // coordinate system. Scale/crop its one source brush into this tight
        // foreground region; this is a center-zoom approximation, not the
        // SceneMesh's exact nonlinear UV field.
        binding.desktop.Stretch(CompositionStretch::None);
        binding.desktop.HorizontalAlignmentRatio(0.0f);
        binding.desktop.VerticalAlignmentRatio(0.0f);
        binding.desktop.TransformMatrix({scale,0.0f,0.0f,scale,-sourceX*scale,-sourceY*scale});
        inkStage=ink::InkStage::SettingBlurProperty;
        brush.Properties().InsertScalar(L"InkBlur.BlurAmount",3.0f/frame.dpiScale);
        return S_OK;
    }catch(...){return winrt::to_hresult();}
}

HRESULT SystemDesktopGlass::DetachInkBinding(ink::Binding& binding) noexcept {
    HRESULT first=S_OK;
    auto record=[&](HRESULT hr) noexcept {if(FAILED(hr)&&SUCCEEDED(first))first=hr;};
    auto attempt=[&](auto&& operation) noexcept {
        try {operation();}catch(...){record(winrt::to_hresult());}
    };
    CompositionEffectBrush brush{nullptr};
    record(ResolveInkBrush(binding,brush));
    if(brush){
        attempt([&]{brush.SetSourceParameter(L"Desktop",nullptr);});
        attempt([&]{brush.SetSourceParameter(L"InkWhite",nullptr);});
        attempt([&]{brush.SetSourceParameter(L"GlyphMask",nullptr);});
    }
    attempt([&]{if(binding.desktop)binding.desktop.Surface(nullptr);});
    attempt([&]{if(binding.glyphMask)binding.glyphMask.Surface(nullptr);});
    binding.desktop=nullptr;binding.glyphMask=nullptr;binding.brush={};
    return first;
}

HRESULT SystemDesktopGlass::PruneInkBindings() noexcept {
    HRESULT first=S_OK;
    auto current=inkBindings.begin();
    while(current!=inkBindings.end()){
        CompositionEffectBrush brush{nullptr};
        auto resolve=ResolveInkBrush(*current,brush);
        if(FAILED(resolve))return FAILED(first)?first:resolve;
        if(brush){++current;continue;}
        auto hr=DetachInkBinding(*current);if(FAILED(hr)&&SUCCEEDED(first))first=hr;
        current=inkBindings.erase(current);
    }
    return first;
}

HRESULT SystemDesktopGlass::CreateInk(ICompositionSurface const& glyphMask,
    float x,float y,float width,float height,CompositionEffectBrush& result) noexcept {
    result=nullptr;
    if(!Ready())return S_FALSE;
    if(GetCurrentThreadId()!=thread)return RPC_E_WRONG_THREAD;
    if(!glyphMask)return E_POINTER;
    if(!ink::ValidRequestRect(x,y,width,height))return E_INVALIDARG;
    auto prune=PruneInkBindings();if(FAILED(prune))return prune;
    if(inkBindings.size()>=12)return E_BOUNDS;
    try {
        inkStage=ink::InkStage::BeforeEnsureFactory;
        winrt::check_hresult(EnsureInkFactory());
        inkStage=ink::InkStage::FactoryReady;
        inkStage=ink::InkStage::CreatingEffectBrush;
        auto effectBrush=inkFactory.CreateBrush();
        inkStage=ink::InkStage::EffectBrushCreated;
        inkStage=ink::InkStage::CreatingDesktopBrush;
        auto desktopBrush=resources->compositor.CreateSurfaceBrush(surface);
        desktopBrush.Stretch(CompositionStretch::None);
        desktopBrush.HorizontalAlignmentRatio(0.0f);
        desktopBrush.VerticalAlignmentRatio(0.0f);
        inkStage=ink::InkStage::DesktopBrushCreated;
        inkStage=ink::InkStage::CreatingMaskBrush;
        auto maskBrush=resources->compositor.CreateSurfaceBrush(glyphMask);
        maskBrush.Stretch(CompositionStretch::Fill);
        inkStage=ink::InkStage::MaskBrushCreated;
        inkStage=ink::InkStage::SettingDesktopSource;
        effectBrush.SetSourceParameter(L"Desktop",desktopBrush);
        inkStage=ink::InkStage::DesktopSourceSet;
        inkStage=ink::InkStage::SettingWhiteSource;
        effectBrush.SetSourceParameter(L"InkWhite",inkWhite);
        inkStage=ink::InkStage::WhiteSourceSet;
        inkStage=ink::InkStage::SettingMaskSource;
        effectBrush.SetSourceParameter(L"GlyphMask",maskBrush);
        inkStage=ink::InkStage::MaskSourceSet;

        ink::Binding binding{};
        binding.brush=winrt::make_weak(effectBrush);
        binding.desktop=desktopBrush;binding.glyphMask=maskBrush;
        binding.x=x;binding.y=y;binding.width=width;binding.height=height;
        winrt::check_hresult(SetInkBrushTint(binding,inkTint));
        winrt::check_hresult(SetInkBrushFrame(binding,lastFrame));
        inkStage=ink::InkStage::RegisteringBinding;
        inkBindings.push_back(std::move(binding));
        result=effectBrush;
        inkStage=ink::InkStage::Success;
        return S_OK;
    }catch(...){return winrt::to_hresult();}
}

HRESULT SystemDesktopGlass::UpdateInk(CompositionEffectBrush const& brush,
    float x,float y,float width,float height) noexcept {
    if(!Ready())return S_FALSE;
    if(GetCurrentThreadId()!=thread)return RPC_E_WRONG_THREAD;
    if(!brush)return E_POINTER;
    if(!ink::ValidRequestRect(x,y,width,height))return E_INVALIDARG;
    auto prune=PruneInkBindings();if(FAILED(prune))return prune;
    for(auto& binding:inkBindings){
        CompositionEffectBrush live{nullptr};
        auto resolve=ResolveInkBrush(binding,live);if(FAILED(resolve))return resolve;
        if(!live)continue;
        bool same{};auto hr=SameInkBrush(live,brush,same);if(FAILED(hr))return hr;
        if(!same)continue;
        binding.x=x;binding.y=y;binding.width=width;binding.height=height;
        return SetInkBrushFrame(binding,lastFrame);
    }
    return S_FALSE;
}

HRESULT SystemDesktopGlass::ReleaseInk(CompositionEffectBrush const& brush) noexcept {
    if(!Ready())return S_FALSE;
    if(GetCurrentThreadId()!=thread)return RPC_E_WRONG_THREAD;
    if(!brush)return E_POINTER;
    auto prune=PruneInkBindings();if(FAILED(prune))return prune;
    for(auto current=inkBindings.begin();current!=inkBindings.end();++current){
        CompositionEffectBrush live{nullptr};
        auto resolve=ResolveInkBrush(*current,live);if(FAILED(resolve))return resolve;
        if(!live)continue;
        bool same{};auto hr=SameInkBrush(live,brush,same);if(FAILED(hr))return hr;
        if(!same)continue;
        hr=DetachInkBinding(*current);inkBindings.erase(current);return hr;
    }
    return S_FALSE;
}

HRESULT SystemDesktopGlass::SetInkTint(float tint) noexcept {
    if(!Ready())return S_FALSE;
    if(GetCurrentThreadId()!=thread)return RPC_E_WRONG_THREAD;
    if(!std::isfinite(tint))return E_INVALIDARG;
    inkTint=std::clamp(tint,0.0f,1.0f);
    auto prune=PruneInkBindings();if(FAILED(prune))return prune;
    for(auto& binding:inkBindings){auto hr=SetInkBrushTint(binding,inkTint);if(FAILED(hr))return hr;}
    return S_OK;
}

HRESULT SystemDesktopGlass::UpdateInkFrame(Frame const& frame) noexcept {
    auto prune=PruneInkBindings();if(FAILED(prune))return prune;
    for(auto& binding:inkBindings){auto hr=SetInkBrushFrame(binding,frame);if(FAILED(hr))return hr;}
    return S_OK;
}

HRESULT SystemDesktopGlass::ValidateColorProfile() const noexcept {
    // Metadata only; do not export a single source pixel for calibration.
    UINT32 pathCount{},modeCount{};
    LONG rc=GetDisplayConfigBufferSizes(QDC_ONLY_ACTIVE_PATHS,&pathCount,&modeCount);
    if(rc!=ERROR_SUCCESS)return HRESULT_FROM_WIN32(rc);
    if(pathCount<1||pathCount>32||modeCount>128)return E_BOUNDS;
    try{
        std::vector<DISPLAYCONFIG_PATH_INFO> paths(pathCount);std::vector<DISPLAYCONFIG_MODE_INFO> modes(modeCount);
        rc=QueryDisplayConfig(QDC_ONLY_ACTIVE_PATHS,&pathCount,paths.data(),&modeCount,modes.data(),nullptr);
        if(rc!=ERROR_SUCCESS)return HRESULT_FROM_WIN32(rc);
        MONITORINFOEXW monitor{};monitor.cbSize=sizeof(monitor);
        if(!GetMonitorInfoW(MonitorFromWindow(host,MONITOR_DEFAULTTONEAREST),&monitor))return HRESULT_FROM_WIN32(GetLastError());
        for(auto const& path:paths){
            DISPLAYCONFIG_SOURCE_DEVICE_NAME name{};name.header={DISPLAYCONFIG_DEVICE_INFO_GET_SOURCE_NAME,sizeof(name),path.sourceInfo.adapterId,path.sourceInfo.id};
            if(DisplayConfigGetDeviceInfo(&name.header)!=ERROR_SUCCESS||_wcsicmp(name.viewGdiDeviceName,monitor.szDevice))continue;
            DISPLAYCONFIG_GET_ADVANCED_COLOR_INFO_2 color{};color.header={DISPLAYCONFIG_DEVICE_INFO_GET_ADVANCED_COLOR_INFO_2,sizeof(color),path.targetInfo.adapterId,path.targetInfo.id};
            rc=DisplayConfigGetDeviceInfo(&color.header);if(rc!=ERROR_SUCCESS)return HRESULT_FROM_WIN32(rc);
            // The calibration has been measured in WCG mode only. Never treat
            // this empirical gain as a universal HDR or SDR conversion.
            return static_cast<unsigned>(color.activeColorMode)==1u?S_OK:HRESULT_FROM_WIN32(ERROR_NOT_SUPPORTED);
        }
        return HRESULT_FROM_WIN32(ERROR_NOT_FOUND);
    }catch(...){return to_hresult();}
}
HRESULT SystemDesktopGlass::CalculateFrame(float dpi,float radius,float strain,Frame& frame,RECT& area,SIZE& canvas,RECT& desktopBounds) noexcept {
    if(!std::isfinite(dpi)||dpi<.5f||dpi>4||!std::isfinite(radius)||!std::isfinite(strain))return E_INVALIDARG;
    RECT body{};if(!GetClientRect(host,&body))return HRESULT_FROM_WIN32(GetLastError());
    POINT origin{};if(!ClientToScreen(host,&origin))return HRESULT_FROM_WIN32(GetLastError());
    const int w=body.right,h=body.bottom;
    if(w<1||h<1||w>1024||h>1024)return E_BOUNDS;
    const int left=GetSystemMetrics(SM_XVIRTUALSCREEN),top=GetSystemMetrics(SM_YVIRTUALSCREEN);
    const int desktopWidth=GetSystemMetrics(SM_CXVIRTUALSCREEN),desktopHeight=GetSystemMetrics(SM_CYVIRTUALSCREEN);
    if(desktopWidth<=0||desktopHeight<=0)return E_BOUNDS;
    const int right=left+desktopWidth,bottom=top+desktopHeight;
    desktopBounds={left,top,right,bottom};
    if(origin.x<left||origin.y<top||origin.x+w>right||origin.y+h>bottom)return E_BOUNDS;
    // Retain one source canvas through the entire capsule/peek morph. Only
    // the body UV origin moves inside it; no resize of the DWM child mid-morph.
    if(haveRegion && EqualRect(&desktopBounds,&lastDesktopBounds) &&
        RetainedDesktopSourceFitsBounds(left,top,desktopWidth,desktopHeight,
            lastArea.left,lastArea.top,lastCanvas.cx,lastCanvas.cy) &&
        origin.x>=lastArea.left && origin.y>=lastArea.top &&
        origin.x+w<=lastArea.right && origin.y+h<=lastArea.bottom){area=lastArea;canvas=lastCanvas;}
    else {
        const int wantedWidth=std::min(1024,std::max(w,int(std::ceil(400*dpi))));
        const auto stableHeight=PreferredStableSourceHeight(h,dpi,bottom-top,
            static_cast<int64_t>(origin.y)-top);
        if(stableHeight.height==0)return E_BOUNDS;
        const int cw=std::min(wantedWidth,right-left),ch=std::min(stableHeight.height,bottom-top);
        const int x=std::clamp(int(origin.x)+(w-cw)/2,left,right-cw);
        const int above=std::min(stableHeight.above,ch-h);
        const int y=std::clamp(int(origin.y)-above,top,bottom-ch);
        area={x,y,x+cw,y+ch};canvas={cw,ch};
    }
    frame={w/dpi,h/dpi,std::min(radius,std::min(w,h)*.5f/dpi),dpi,strain,
        std::clamp((h/dpi-52.0f)/124.0f,0.0f,1.0f),float(canvas.cx),float(canvas.cy),float(origin.x-area.left),float(origin.y-area.top)};
    for(uint32_t i=0;i<regionCount;++i){auto const& lens=regions[i];
        if(lens.opacity>.001f&&lens.centerXDips-lens.widthDips*.5f>=0&&lens.centerYDips-lens.heightDips*.5f>=0&&lens.centerXDips+lens.widthDips*.5f<=frame.widthDips&&lens.centerYDips+lens.heightDips*.5f<=frame.heightDips)frame.lenses[frame.lensCount++]=lens;
    }
    return S_OK;
}
HRESULT SystemDesktopGlass::Initialize(HWND ownHost,HWND ownShadow,SystemCompositorBootstrap& owner,
    ContainerVisual const& parent,float dpi,float radius,float strain) noexcept {
    if(attempted||closed)return E_ABORT;attempted=true;thread=GetCurrentThreadId();
    host=ownHost;shadow=ownShadow;bootstrap=&owner;root=parent;
    try {
        stage=1;check_hresult(ValidateColorProfile());colorMonitor=MonitorFromWindow(host,MONITOR_DEFAULTTONEAREST);check_hresult(owner.GetResources(&resources));check_hresult(owner.GetDesktopVisualFunctions(&create,&update));
        Frame frame{};RECT area{},desktopBounds{};SIZE canvas{};check_hresult(CalculateFrame(dpi,radius,strain,frame,area,canvas,desktopBounds));
        stage=2;system_compositor_lens::DesktopSharedSourceStatus detail{};
        check_hresult(system_compositor_lens::DesktopSharedSource::Create(host,resources->dcompDesktopDevice.get(),create,update,
            area,canvas,shared.put(),&privateId,&detail,shadow));
        stage=3;check_hresult(resources->dcompDesktopDevice->CreateVisual(sourceParent.put()));
        check_hresult(sourceParent->AddVisual(shared.get(),TRUE,nullptr));
        source=sourceParent.as<Visual>();source.Size({float(canvas.cx),float(canvas.cy)});
        // This is the only source sub-tree. Never use root or scene as SourceVisual.
        stage=4;surface=resources->compositor.CreateVisualSurface();surface.SourceVisual(source);
        surface.SourceOffset({0,0});surface.SourceSize({float(canvas.cx),float(canvas.cy)});
        stage=5;
        auto description=make_self<color::GammaTransferDescription>();description->source=CompositionEffectSourceParameter(L"Input");
        correctionFactory=resources->compositor.CreateEffectFactory(description.as<winrt::Windows::Graphics::Effects::IGraphicsEffect>());
        correctionBrush=correctionFactory.CreateBrush();colorInput=resources->compositor.CreateSurfaceBrush(surface);colorInput.Stretch(CompositionStretch::Fill);
        correctionBrush.SetSourceParameter(L"Input",colorInput);correctionSprite=resources->compositor.CreateSpriteVisual();
        correctionSprite.Size({float(canvas.cx),float(canvas.cy)});
        correctionSprite.Brush(correctionBrush);

        correctedSurface=resources->compositor.CreateVisualSurface();correctedSurface.SourceVisual(correctionSprite);
        correctedSurface.SourceOffset({0,0});correctedSurface.SourceSize({float(canvas.cx),float(canvas.cy)});
        check_hresult(scene.Initialize(resources->compositor,correctedSurface,frame));
        // Keep both SourceVisuals at canvas-local (0,0). The child placement
        // containers alone translate their direct root renderings into window
        // coordinates; the Scene UV already includes bodySourceX/Y. This
        // prevents a source visual's own transform being applied a second time
        // when the compositor renders it into a VisualSurface.
        stage=6;
        sourcePlacement=resources->compositor.CreateContainerVisual();
        correctionPlacement=resources->compositor.CreateContainerVisual();
        sourcePlacement.Size({float(canvas.cx),float(canvas.cy)});
        correctionPlacement.Size({float(canvas.cx),float(canvas.cy)});
        sourcePlacement.Offset({-frame.bodySourceXPx,-frame.bodySourceYPx,0});
        correctionPlacement.Offset({-frame.bodySourceXPx,-frame.bodySourceYPx,0});
        sourcePlacement.Children().InsertAtTop(source);sourcePlaced=true;
        correctionPlacement.Children().InsertAtTop(correctionSprite);correctionPlaced=true;
        root.Children().InsertAtBottom(scene.Visual());sceneAttached=true;
        root.Children().InsertAtBottom(sourcePlacement);sourceAttached=true;
        root.Children().InsertAtBottom(correctionPlacement);correctionAttached=true;
        lastArea=area;lastCanvas=canvas;lastDesktopBounds=desktopBounds;haveRegion=true;lastFrame=frame;haveFrame=true;initialized=true;stage=7;
        return Commit();
    }catch(...){auto hr=to_hresult();Close();return hr;}
}
HRESULT SystemDesktopGlass::Update(float dpi,float radius,float strain,bool sourceRefresh) noexcept {
    if(!Ready())return E_ABORT;if(GetCurrentThreadId()!=thread)return RPC_E_WRONG_THREAD;
    try {
        HMONITOR nextMonitor=MonitorFromWindow(host,MONITOR_DEFAULTTONEAREST);if(nextMonitor!=colorMonitor){check_hresult(ValidateColorProfile());colorMonitor=nextMonitor;}
        Frame frame{};RECT area{},desktopBounds{};SIZE canvas{};stage=8;check_hresult(CalculateFrame(dpi,radius,strain,frame,area,canvas,desktopBounds));
        const bool changedRegion=!haveRegion || !EqualRect(&desktopBounds,&lastDesktopBounds) ||
            !EqualRect(&area,&lastArea) || canvas.cx!=lastCanvas.cx || canvas.cy!=lastCanvas.cy;
        if(haveFrame&&!changedRegion&&SameOpticalFrame(frame,lastFrame)&&!sourceRefresh)return S_OK;
        if(changedRegion||sourceRefresh){
            stage=9;check_hresult(system_compositor_lens::UpdateDesktopSource(host,shadow,update,privateId,area,canvas));
        }
        if(changedRegion){
            source.Size({float(canvas.cx),float(canvas.cy)});
            sourcePlacement.Size({float(canvas.cx),float(canvas.cy)});
            correctionPlacement.Size({float(canvas.cx),float(canvas.cy)});
            surface.SourceSize({float(canvas.cx),float(canvas.cy)});
            correctionSprite.Size({float(canvas.cx),float(canvas.cy)});correctedSurface.SourceSize({float(canvas.cx),float(canvas.cy)});lastArea=area;lastCanvas=canvas;lastDesktopBounds=desktopBounds;haveRegion=true;
        }
        sourcePlacement.Offset({-frame.bodySourceXPx,-frame.bodySourceYPx,0});
        correctionPlacement.Offset({-frame.bodySourceXPx,-frame.bodySourceYPx,0});
        check_hresult(UpdateInkFrame(frame));
        stage=10;check_hresult(scene.Update(frame));lastFrame=frame;haveFrame=true;stage=11;return Commit();
    }catch(...){auto hr=to_hresult();Close();return hr;}
}
HRESULT SystemDesktopGlass::SetLenses(std::span<LensRegion const> lenses) noexcept {
    if(!Ready())return S_FALSE;if(GetCurrentThreadId()!=thread)return RPC_E_WRONG_THREAD;
    if(lenses.size()>3)return E_INVALIDARG;
    std::array<LensRegion,3> next{};std::copy(lenses.begin(),lenses.end(),next.begin());regions=next;regionCount=static_cast<uint32_t>(lenses.size());
    return Update(lastFrame.dpiScale,lastFrame.radiusDips,lastFrame.strain);
}
HRESULT SystemDesktopGlass::Commit() noexcept {
    if(!Ready()||!resources)return E_ABORT;
    if(GetCurrentThreadId()!=thread)return RPC_E_WRONG_THREAD;
    auto hr=resources->interopPartner->MarkDirty();if(FAILED(hr))return hr;
    return resources->dcompDesktopDevice->Commit();
}
void SystemDesktopGlass::SetVisible(bool visible){if(Ready()){scene.Visual().IsVisible(visible);sourcePlacement.IsVisible(visible);correctionPlacement.IsVisible(visible);}}
HRESULT SystemDesktopGlass::Close() noexcept {
    if(closed)return closeResult;
    if(thread!=0&&thread!=GetCurrentThreadId())return RPC_E_WRONG_THREAD;
    closed=true;initialized=false;
    gd::cleanup::Result result(closeResult);
    auto attempt=[&](auto&& operation) noexcept {
        result.Attempt(operation,[]() noexcept {return to_hresult();});
    };
    for(auto& binding:inkBindings)result.Record(DetachInkBinding(binding));
    inkBindings.clear();inkFactory=nullptr;inkWhite=nullptr;
    attempt([&]{if(root&&sceneAttached&&scene.Visual())root.Children().Remove(scene.Visual());});
    result.Record(scene.Close());
    attempt([&]{if(correctedSurface)correctedSurface.SourceVisual(nullptr);});
    attempt([&]{if(root&&correctionAttached&&correctionPlacement)root.Children().Remove(correctionPlacement);});
    attempt([&]{if(correctionPlacement&&correctionPlaced&&correctionSprite)correctionPlacement.Children().Remove(correctionSprite);});
    attempt([&]{if(correctionSprite)correctionSprite.Brush(nullptr);});
    attempt([&]{if(correctionBrush)correctionBrush.SetSourceParameter(L"Input",nullptr);});
    attempt([&]{if(colorInput)colorInput.Surface(nullptr);});
    correctedSurface=nullptr;correctionSprite=nullptr;correctionBrush=nullptr;correctionFactory=nullptr;colorInput=nullptr;
    attempt([&]{if(surface)surface.SourceVisual(nullptr);});
    attempt([&]{if(root&&sourceAttached&&sourcePlacement)root.Children().Remove(sourcePlacement);});
    attempt([&]{if(sourcePlacement&&sourcePlaced&&source)sourcePlacement.Children().Remove(source);});
    if(sourceParent&&shared)result.Record(sourceParent->RemoveVisual(shared.get()));
    if(resources&&resources->dcompDesktopDevice)result.Record(resources->dcompDesktopDevice->Commit());
    surface=nullptr;source=nullptr;sourcePlacement=nullptr;correctionPlacement=nullptr;shared=nullptr;sourceParent=nullptr;privateId=nullptr;root=nullptr;resources=nullptr;bootstrap=nullptr;
    closeResult=result.Value();return closeResult;
}
}

// The default ABI/DPI/lifetime mode stays hidden. The explicit --topmost-dll
// mode exercises visibility entirely outside the virtual desktop. Neither mode
// injects input or changes Windows settings; presented UI is verified separately.
#include "GlassDeskNative.cpp"
#include <iostream>
#include <thread>
#include <cstring>
#include <cwchar>
#include <dxgi1_4.h>
#include <atomic>
#include <filesystem>

namespace
{
void* testContext{};int callbackCount{},recoveryCount{},enterCount{},moveCount{};bool quitInCallback{};
void __stdcall Callback(void*,GDPointer const* value)
{
    ++callbackCount;
    if(value->kind==1)++enterCount;
    if(value->kind==3)++moveCount;
    if(value->kind==12&&value->controlId==-1)++recoveryCount;
    if(value->kind==14&&quitInCallback)
    {check_hresult(GDDestroy(testContext));testContext=nullptr;}
}
void Require(bool value,char const* message){if(!value)throw std::runtime_error(message);}
void Pump()
{MSG message{};while(PeekMessageW(&message,nullptr,0,0,PM_REMOVE)){TranslateMessage(&message);DispatchMessageW(&message);}}
struct PointerCounts {int enters{},moves{},leaves{};};
void __stdcall CountPointers(void* user,GDPointer const* value)
{
    auto& counts=*static_cast<PointerCounts*>(user);
    if(value->kind==1)++counts.enters;
    if(value->kind==2)++counts.leaves;
    if(value->kind==3)++counts.moves;
}
template<typename T> T Export(HMODULE module,char const* name)
{
    auto address=GetProcAddress(module,name);check_bool(address!=nullptr);
    T function{};static_assert(sizeof(function)==sizeof(address));
    std::memcpy(&function,&address,sizeof(function));return function;
}
struct ModuleLifetime
{
    HMODULE module{};
    ~ModuleLifetime(){if(module)FreeLibrary(module);}
};
struct ContextLifetime
{
    void* context{};decltype(&GDDestroy) destroy{};
    ~ContextLifetime(){if(context)destroy(context);}
};
struct TypographyApi
{
    decltype(&GDGetNativeAbiInfo) abi;
    decltype(&GDConfigureTypographyV2) configure;
    decltype(&GDMeasureTextV2) measure;
    decltype(&GDRenderSurfaceV2) render;
    decltype(&GDGetTypographyCacheStats) stats;
    decltype(&GDRenderSurface) renderV1;
};
struct TypographyResult
{GDTypographyCacheStats stats{};float timeDelta{},secondsDelta{};unsigned auditedGlyphRuns{};};
class FontAuditRenderer final : public IDWriteTextRenderer
{
    std::atomic<ULONG> references{1};
public:
    unsigned runs{},simulated{};
    HRESULT STDMETHODCALLTYPE QueryInterface(REFIID iid,void** value)override
    {if(!value)return E_POINTER;*value=nullptr;if(iid==__uuidof(IUnknown)||iid==__uuidof(IDWriteTextRenderer)||iid==__uuidof(IDWritePixelSnapping)){*value=static_cast<IDWriteTextRenderer*>(this);AddRef();return S_OK;}return E_NOINTERFACE;}
    ULONG STDMETHODCALLTYPE AddRef()override{return ++references;}
    ULONG STDMETHODCALLTYPE Release()override{auto value=--references;if(!value)delete this;return value;}
    HRESULT STDMETHODCALLTYPE IsPixelSnappingDisabled(void*,BOOL* value)override{if(!value)return E_POINTER;*value=TRUE;return S_OK;}
    HRESULT STDMETHODCALLTYPE GetCurrentTransform(void*,DWRITE_MATRIX* value)override{if(!value)return E_POINTER;*value={1,0,0,1,0,0};return S_OK;}
    HRESULT STDMETHODCALLTYPE GetPixelsPerDip(void*,FLOAT* value)override{if(!value)return E_POINTER;*value=1;return S_OK;}
    HRESULT STDMETHODCALLTYPE DrawGlyphRun(void*,FLOAT,FLOAT,DWRITE_MEASURING_MODE,DWRITE_GLYPH_RUN const* glyph,DWRITE_GLYPH_RUN_DESCRIPTION const*,IUnknown*)override
    {if(!glyph||!glyph->fontFace)return E_POINTER;++runs;if(glyph->fontFace->GetSimulations()!=DWRITE_FONT_SIMULATIONS_NONE)++simulated;return S_OK;}
    HRESULT STDMETHODCALLTYPE DrawUnderline(void*,FLOAT,FLOAT,DWRITE_UNDERLINE const*,IUnknown*)override{return S_OK;}
    HRESULT STDMETHODCALLTYPE DrawStrikethrough(void*,FLOAT,FLOAT,DWRITE_STRIKETHROUGH const*,IUnknown*)override{return S_OK;}
    HRESULT STDMETHODCALLTYPE DrawInlineObject(void* context,FLOAT x,FLOAT y,IDWriteInlineObject* object,BOOL sideways,BOOL rtl,IUnknown* effect)override
    {return object?object->Draw(context,this,x,y,sideways,rtl,effect):E_POINTER;}
};
TypographyResult CheckTypography(void* context,TypographyApi const& api,gd::Host* internal=nullptr)
{
    Require(sizeof(GDOp)==64&&sizeof(GDOpV2)==96&&offsetof(GDOpV2,fontWeight)==64&&offsetof(GDOpV2,trackingEm)==68
        &&offsetof(GDOpV2,textRole)==72&&offsetof(GDOpV2,lineHeightDip)==76&&offsetof(GDOpV2,baselineDip)==80
        &&offsetof(GDOpV2,textFlags)==84&&offsetof(GDOpV2,localeName)==88&&sizeof(GDNativeAbiInfo)==24
        &&sizeof(GDTextMetrics)==24&&sizeof(GDTypographyCacheStats)==40,"typography ABI sizes and offsets");
    GDNativeAbiInfo abi{sizeof(GDNativeAbiInfo)};check_hresult(api.abi(&abi));
    Require(abi.version==2&&abi.v1Size==64&&abi.v2Size==96&&(abi.capabilities&31)==31&&abi.reserved==0,"V2 ABI negotiation/capabilities including clock faces");
    Require(api.abi(nullptr)==E_POINTER,"ABI info null pointer");GDNativeAbiInfo badAbi{};
    Require(api.abi(&badAbi)==E_INVALIDARG,"ABI info caller size is required");
    Require(api.configure(context,3,1,nullptr)==E_INVALIDARG&&api.configure(context,0,2,nullptr)==E_INVALIDARG,"bad profile/revision rejection");
    Require(api.configure(context,0,1,L"ignored-font.ttf")==E_INVALIDARG,"SystemA must not silently consume a QA font path");
    check_hresult(api.configure(context,0,1,nullptr));
    GDTypographyCacheStats empty{};check_hresult(api.stats(context,&empty));Require(empty.formats==0&&empty.layouts==0,"configuration invalidates typography caches");
    GDOpV2 op{};op.legacy={2,8,4,800,90,0,25,0xff20252c,0,0,L"08:08"};op.fontWeight=400;op.textRole=1;op.lineHeightDip=32;op.textFlags=1;op.localeName=L"en-US";
    GDTextMetrics metrics{};check_hresult(api.measure(context,&op,sizeof(op),&metrics));
    Require(metrics.advance>0&&metrics.widthIncludingTrailingWhitespace>=metrics.advance&&metrics.lineCount==1&&metrics.lineHeight==32,"V2 semantic metrics");
    for(uint32_t face=0;face<=0x300;face+=0x100)
    {
        auto sample=op;sample.textFlags=1|face;float smallest=1000,largest=0;
        for(auto digits:{L"00:00",L"11:11",L"23:59",L"08:08"})
        {
            sample.legacy.text=digits;GDTextMetrics clock{};check_hresult(api.measure(context,&sample,sizeof(sample),&clock));
            Require(clock.advance>0&&clock.advance<96&&clock.height<=32.1f,"clock face fits the real narrow clock slot");
            smallest=std::min(smallest,clock.advance);largest=std::max(largest,clock.advance);
        }
        if(largest-smallest>=.25f)std::cerr<<"clock face "<<face<<" width delta "<<(largest-smallest)<<"\n";
        Require(largest-smallest<.25f,"clock faces retain tabular number width");
    }
    auto wrongFaceRole=op;wrongFaceRole.textRole=13;wrongFaceRole.textFlags=0x101;
    Require(api.measure(context,&wrongFaceRole,sizeof(wrongFaceRole),&metrics)==E_INVALIDARG,"clock face cannot replace body typography");
    GDTypographyCacheStats before{},after{};check_hresult(api.stats(context,&before));
    for(int i=0;i<16;++i)check_hresult(api.measure(context,&op,sizeof(op),&metrics));
    check_hresult(api.stats(context,&after));Require(after.layoutsCreated==before.layoutsCreated&&after.layoutHits>=before.layoutHits+16,"repeated measurement reuses cached layout");
    ::IInspectable* surface{};check_hresult(api.render(context,840,100,&op,1,sizeof(op),&surface));Require(surface!=nullptr,"V2 returned a Composition surface");surface->Release();surface=nullptr;
    GDTypographyCacheStats drawn{};check_hresult(api.stats(context,&drawn));
    Require(drawn.layoutsCreated==after.layoutsCreated&&drawn.layoutHits>after.layoutHits,"V2 measurement/drawing share one layout creator and key");
    auto recolored=op;recolored.legacy.color=0xffeeeeee;recolored.legacy.x=15;check_hresult(api.measure(context,&recolored,sizeof(op),&metrics));
    GDTypographyCacheStats colorStats{};check_hresult(api.stats(context,&colorStats));Require(colorStats.layoutsCreated==drawn.layoutsCreated,"color/position do not rebuild typography layout");
    auto resized=op;resized.legacy.width+=1;check_hresult(api.measure(context,&resized,sizeof(op),&metrics));check_hresult(api.stats(context,&after));Require(after.layoutsCreated==drawn.layoutsCreated+1,"width constraint participates in layout key");
    Require(api.measure(context,&op,64,&metrics)==E_INVALIDARG&&api.render(context,100,100,&op,1,64,&surface)==E_INVALIDARG&&!surface,"wrong V2 stride is rejected");
    auto invalid=op;invalid.fontWeight=-1;Require(api.measure(context,&invalid,sizeof(op),&metrics)==E_INVALIDARG,"negative font weight rejected");
    invalid=op;invalid.trackingEm=std::numeric_limits<float>::quiet_NaN();Require(api.measure(context,&invalid,sizeof(op),&metrics)==E_INVALIDARG,"NaN tracking rejected");
    invalid=op;invalid.lineHeightDip=std::numeric_limits<float>::infinity();Require(api.measure(context,&invalid,sizeof(op),&metrics)==E_INVALIDARG,"infinite line height rejected");
    invalid=op;invalid.baselineDip=-1;Require(api.measure(context,&invalid,sizeof(op),&metrics)==E_INVALIDARG,"negative baseline rejected");
    invalid=op;invalid.textRole=23;Require(api.measure(context,&invalid,sizeof(op),&metrics)==E_INVALIDARG,"unknown text role rejected");
    invalid=op;invalid.textFlags=4;Require(api.measure(context,&invalid,sizeof(op),&metrics)==E_INVALIDARG,"unknown text flags rejected");
    invalid=op;invalid.textRole=0;Require(api.measure(context,&invalid,sizeof(op),&metrics)==E_INVALIDARG,"legacy must not silently discard V2 typography fields");
    HRESULT wrongThread{};std::thread other([&]{GDTextMetrics value{};wrongThread=api.measure(context,&op,sizeof(op),&value);});other.join();Require(wrongThread==RPC_E_WRONG_THREAD,"V2 owner-thread guard");
    TypographyResult result;float minimum=std::numeric_limits<float>::max(),maximum=0;
    for(int hour=0;hour<24;++hour)for(int minute=0;minute<60;++minute)
    {wchar_t time[6]{};swprintf_s(time,L"%02d:%02d",hour,minute);op.legacy.text=time;check_hresult(api.measure(context,&op,sizeof(op),&metrics));minimum=std::min(minimum,metrics.advance);maximum=std::max(maximum,metrics.advance);}
    result.timeDelta=maximum-minimum;Require(result.timeDelta<=.05f,"V2 all 1440 HH:mm combinations must be stable");
    minimum=std::numeric_limits<float>::max();maximum=0;
    for(auto text:{L"00:00:00",L"08:08:08",L"11:11:11",L"12:34:56",L"22:22:22",L"23:59:59"})
    {op.legacy.text=text;check_hresult(api.measure(context,&op,sizeof(op),&metrics));minimum=std::min(minimum,metrics.advance);maximum=std::max(maximum,metrics.advance);}
    result.secondsDelta=maximum-minimum;Require(result.secondsDelta<=.05f,"V2 semantic HH:mm:ss must be stable");
    op.fontWeight=600;op.textRole=9;op.lineHeightDip=28;op.textFlags=0;op.legacy.fontSize=20;
    for(auto text:{L"今天 08:08 · GlassDesk",L"天球(そら)のMúsica",L"العربية / עברית",L"\U0001f469\U0001f3fd\u200d\U0001f4bb / Emoji"})
    {
        op.legacy.text=text;check_hresult(api.measure(context,&op,sizeof(op),&metrics));
        if(internal)
        {
            auto layout=internal->Typography().CreateLayout(internal->TextRequest(op));com_ptr<FontAuditRenderer> audit;audit.attach(new FontAuditRenderer());
            check_hresult(layout->textLayout->Draw(nullptr,audit.get(),0,0));Require(audit->runs>0&&audit->simulated==0,"V2 actual glyph runs must use no simulated fonts");result.auditedGlyphRuns+=audit->runs;
        }
    }
    // V1 consumes the exact original64-byte array and Legacy V2 paints the same
    // branch. No V1 array is interpreted as a96-byte strided array.
    GDOp legacy[3]={{0,0,0,100,40,0,14,0x22112233},{2,4,0,180,40,0,14,0xff20252c,1,0,L"Legacy / 音乐"},{6,185,4,24,24,0,20,0xff20252c,0,1,L"\uE768"}};
    check_hresult(api.renderV1(context,220,48,legacy,3,&surface));surface->Release();surface=nullptr;
    GDOpV2 legacyV2[3]{};for(unsigned i=0;i<3;++i)legacyV2[i].legacy=legacy[i];
    check_hresult(api.render(context,220,48,legacyV2,3,sizeof(GDOpV2),&surface));surface->Release();surface=nullptr;
    check_hresult(api.measure(context,&legacyV2[1],sizeof(GDOpV2),&metrics));Require(metrics.advance>0,"Legacy V2 text can be measured");
    if(internal)
    {
        auto layout=internal->Typography().CreateLayout(internal->TextRequest(legacyV2[1]));
        Require(layout->requestedFamily==L"Segoe UI Variable Text"&&layout->latinWeight==600&&layout->resolvedLocale.empty(),"Legacy helper preserves fixed Text/bool/default locale");
    }
    op.fontWeight=400;op.textRole=13;op.lineHeightDip=20;op.legacy.text=L"bounded cache";
    for(int i=0;i<180;++i){op.legacy.fontSize=12+float(i)/10;check_hresult(api.measure(context,&op,sizeof(op),&metrics));}
    check_hresult(api.stats(context,&result.stats));Require(result.stats.formats<=64&&result.stats.layouts<=128&&result.stats.layoutsCreated>128,"V2 format/layout cache bounds");
    auto saved=result.stats;Require(FAILED(api.configure(context,2,1,nullptr)),"NotoQa requires a real private font path");check_hresult(api.stats(context,&after));Require(after.layoutsCreated==saved.layoutsCreated&&after.layouts==saved.layouts,"failed configuration preserves prior typography state");
    return result;
}
void WriteTypographyResult(TypographyResult const& result)
{
    std::cout<<"{\"abiVersion\":2,\"v1Size\":64,\"v2Size\":96,\"abiInfoSize\":24,\"metricsSize\":24,\"cacheStatsSize\":40,\"hhmmCombinations\":1440,\"hhmmDeltaDip\":"<<result.timeDelta
        <<",\"secondsRepresentativeDeltaDip\":"<<result.secondsDelta<<",\"auditedNonSimulatedGlyphRuns\":"<<result.auditedGlyphRuns<<",\"formats\":"<<result.stats.formats<<",\"layouts\":"<<result.stats.layouts<<",\"layoutsCreated\":"<<result.stats.layoutsCreated<<",\"measureDrawSharedCache\":true,\"v1LegacyCompatible\":true}";
}
int MaterialSamples(wchar_t const* directory)
{
    std::filesystem::create_directories(directory);ContextLifetime context{nullptr,GDDestroy};HWND window{};
    check_hresult(GDCreate(nullptr,nullptr,&context.context,&window));auto host=gd::FromContext(context.context);
    Require(host->liquid!=nullptr,"material GPU device available");
    for(unsigned theme=0;theme<4;++theme)
    {
        auto path=std::filesystem::path(directory)/(L"material-"+std::to_wstring(theme)+L".png");
        check_hresult(host->liquid->SaveReferencePreview(path.c_str(),540,360,42,1.5f,theme));
    }
    auto capsule=std::filesystem::path(directory)/L"capsule.png";
    check_hresult(host->liquid->SaveReferencePreview(capsule.c_str(),368,64,32,2,0));
    Require(!host->liquidEnabled&&host->liquid->Stats().workerPid==0&&!IsWindowVisible(window),"synthetic previews must not capture or show desktop windows");
    std::cout<<"{\"passed\":true,\"nativeGpuSamples\":5,\"captureStarted\":false,\"left\":\"frosted input simulation + shared light\",\"right\":\"frosted input simulation + shared light\"}\n";return 0;
}
int TensionSamples(wchar_t const* directory)
{
    std::filesystem::create_directories(directory);
    ContextLifetime context{nullptr,GDDestroy};HWND window{};check_hresult(GDCreate(nullptr,nullptr,&context.context,&window));
    auto host=gd::FromContext(context.context);
    for(auto sample:{std::pair{L"tension-pull.png",1.0f},std::pair{L"tension-recoil.png",-.42f},std::pair{L"tension-rest.png",0.0f}})
    {
        auto path=std::filesystem::path(directory)/sample.first;
        check_hresult(host->liquid->SaveReferencePreview(path.c_str(),540,360,gd::SurfaceRadius(540,360,42,sample.second),1.5f,0,sample.second));
    }
    Require(host->liquid->Stats().workerPid==0&&!IsWindowVisible(window),"tension previews must not sample desktop or show windows");
    std::cout<<"{\"passed\":true,\"syntheticGpuSamples\":3,\"captureStarted\":false,\"left\":\"plain frost simulation\",\"right\":\"shared procedural optical shader\"}\n";return 0;
}
int MaterialProfile()
{
    auto foreground=GetForegroundWindow();
    ContextLifetime context{nullptr,GDDestroy};HWND window{};check_hresult(GDCreate(nullptr,nullptr,&context.context,&window));
    auto host=gd::FromContext(context.context);
    DWORD bodyBefore{},shadowBefore{};
    check_bool(GetWindowDisplayAffinity(window,&bodyBefore));check_bool(GetWindowDisplayAffinity(host->shadow,&shadowBefore));
    // Exercise the same logical visible path without showing a real HWND.
    check_hresult(GDSetLiquidGlassEnabled(context.context,1));host->visible=true;host->UpdateLiquid();
    host->liquid->SetEnabled(true);host->liquid->Suspend(false,false);
    host->liquid->Tick(RECT{40,60,408,124},32,20);
    Require(!host->liquid->Enabled()&&!host->liquid->Ready()&&host->liquid->Stats().workerPid==0,"legacy API must not enable capture");
    host->visible=false;host->UpdateLiquid();
    auto retainedShoulder=get_abi(host->shoulderGeometry);unsigned cases{};
    for(unsigned dpiValue:{96u,144u,192u})
    {
        RECT suggestion{40,60,500,600};SendMessageW(window,WM_DPICHANGED,MAKELONG(dpiValue,dpiValue),reinterpret_cast<LPARAM>(&suggestion));
        for(auto size:{std::pair{184.0f,32.0f},std::pair{320.0f,180.0f},std::pair{360.0f,512.0f},std::pair{3.0f,3.0f}})
        {
            GDFrame frame{size.first,size.second,std::min(24.0f,size.second/2),20,.24f,0};
            check_hresult(GDApplyFrame(context.context,&frame));
            for(auto const& geometry:{host->shoulderGeometry,host->meniscusGeometry})
            {
                auto offset=geometry.Offset(),extent=geometry.Size(),corner=geometry.CornerRadius();
                Require(extent.x>=0&&extent.y>=0&&corner.x>=0&&corner.y>=0,"material geometry must stay valid through small morph frames");
                Require(std::abs(extent.x+offset.x*2-host->cachedWidth)<.01f&&std::abs(extent.y+offset.y*2-host->cachedHeight)<.01f,"bevel must follow the actual pixel silhouette at each DPI");
            }
            auto optical=host->liquid->OpticalSurface(unsigned(host->cachedWidth),unsigned(host->cachedHeight),host->cachedRadius,host->Scale());
            Require(optical!=nullptr,"procedural material shader must render without capture");
            Require(get_abi(optical)==get_abi(host->liquid->OpticalSurface(unsigned(host->cachedWidth),unsigned(host->cachedHeight),host->cachedRadius,host->Scale())),"static material must reuse its optical surface");
            auto light=host->borderBrush.EndPoint();
            Require(std::abs(light.x-light.y*.55f)<.01f,"material light direction must stay fixed through aspect-ratio changes");
            Require(get_abi(host->shoulderGeometry)==retainedShoulder,"morph must retain material resources");
            frame.solid=1;check_hresult(GDApplyFrame(context.context,&frame));
            Require(!host->bevel.IsVisible()&&!host->glass.IsVisible()&&!host->clearEdge.IsVisible()&&!host->liquidOverlay.IsVisible(),"solid fallback must suppress optical material layers");
            frame.solid=0;check_hresult(GDApplyFrame(context.context,&frame));
            host->visible=true;host->UpdateLiquid();
            Require(host->opticalLight.IsVisible()==!host->fallback&&host->glass.IsVisible()==!host->fallback&&host->clearEdge.IsVisible()==!host->fallback,"capture-free material must restore system frost and optical light after fallback");
            host->visible=false;host->UpdateLiquid();
            ++cases;
        }
    }
    Require(host->liquidEnabled&&!host->liquidReady&&!host->liquidPresented&&!host->liquidOverlay.IsVisible(),"enabled product material must never present captured pixels");
    auto stats=host->liquid->Stats();
    Require(stats.workerPid==0&&stats.frames==0&&stats.captureCallbacks==0&&stats.captureWidth==0,"visible material must not start monitor capture");
    DWORD bodyAfter{},shadowAfter{};
    check_bool(GetWindowDisplayAffinity(window,&bodyAfter));check_bool(GetWindowDisplayAffinity(host->shadow,&shadowAfter));
    Require(bodyBefore==bodyAfter&&shadowBefore==shadowAfter,"no-capture material must not change screenshot affinity");
    check_hresult(GDSetLiquidGlassEnabled(context.context,0));host->visible=true;host->UpdateLiquid();
    Require(!host->opticalLight.IsVisible()&&!host->clearEdge.IsVisible()&&host->glass.IsVisible()==!host->fallback,"turning optics off preserves ordinary frost");
    host->visible=false;host->UpdateLiquid();
    Require(!IsWindowVisible(window)&&!IsWindowVisible(host->shadow)&&GetForegroundWindow()==foreground,"material smoke test stays hidden and preserves foreground");
    std::cout<<"{\"passed\":true,\"testKind\":\"hidden material geometry and fallback\",\"cases\":"<<cases<<",\"dpi\":[96,144,192],\"captureStarted\":false,\"presentedVisualVerified\":false}\n";return 0;
}
int TensionProfile()
{
    ContextLifetime context{nullptr,GDDestroy};HWND window{};check_hresult(GDCreate(nullptr,nullptr,&context.context,&window));
    auto host=gd::FromContext(context.context);check_hresult(GDSetLiquidGlassEnabled(context.context,1));
    unsigned cases{};
    for(unsigned dpiValue:{96u,144u,192u})
    {
        RECT suggestion{40,60,500,600};SendMessageW(window,WM_DPICHANGED,MAKELONG(dpiValue,dpiValue),reinterpret_cast<LPARAM>(&suggestion));
        GDFrame frame{360,512,24,20,.24f,0};
        for(float strain:{-1.0f,-.4f,0.0f,.4f,1.0f})
        {
            check_hresult(GDApplyFrameWithTension(context.context,&frame,strain));
            float neckY=512*(strain>=0?.36f:.64f);
            Require(host->Inside(180,neckY),"tension must preserve the content center");
            Require(strain==0?host->Inside(2,neckY):std::abs(strain)<.8f||!host->Inside(2,neckY),"hit testing must follow the liquid neck");
            HRGN region=CreateRectRgn(0,0,0,0);Require(GetWindowRgn(window,region)!=ERROR,"liquid input region exists");
            Require(strain==0?bool(PtInRegion(region,LONG(2*host->Scale()),LONG(neckY*host->Scale()))):std::abs(strain)<.8f||!PtInRegion(region,LONG(2*host->Scale()),LONG(neckY*host->Scale())),"native input region must match the clipped neck");DeleteObject(region);
            auto optical=host->liquid->OpticalSurface(unsigned(host->cachedWidth),unsigned(host->cachedHeight),host->cachedRadius,host->Scale(),strain);
            Require(optical!=nullptr,"tension optical shader must render without capture");
            Require(std::abs(host->cachedTension-strain)<.0001f,"clip must receive each current tension sample");++cases;
        }
        frame.solid=1;check_hresult(GDApplyFrameWithTension(context.context,&frame,1));
        Require(host->EffectiveTension()==0&&host->cachedTension==0,"solid fallback removes deformation");
        frame.solid=0;check_hresult(GDApplyFrame(context.context,&frame));
        Require(host->surfaceTension==0,"legacy frame ABI restores the original static contour");
    }
    GDFrame frame{360,512,24,20,.24f,0};Require(GDApplyFrameWithTension(context.context,&frame,2)==E_INVALIDARG,"invalid strain must be rejected");
    Require(!host->liquid->Enabled()&&host->liquid->Stats().workerPid==0,"tension must not enable desktop capture");
    Require(!IsWindowVisible(window)&&!IsWindowVisible(host->shadow),"tension smoke remains hidden");
    std::cout<<"{\"passed\":true,\"tensionCases\":"<<cases<<",\"dpi\":[96,144,192],\"captureStarted\":false,\"clipAndInputVerified\":true}\n";return 0;
}
int LoadedDllTypography(wchar_t const* path)
{
    ModuleLifetime module{LoadLibraryExW(path,nullptr,LOAD_LIBRARY_SEARCH_DLL_LOAD_DIR|LOAD_LIBRARY_SEARCH_DEFAULT_DIRS)};check_bool(module.module!=nullptr);
    auto create=Export<decltype(&GDCreate)>(module.module,"GDCreate");auto destroy=Export<decltype(&GDDestroy)>(module.module,"GDDestroy");
    TypographyApi api{Export<decltype(&GDGetNativeAbiInfo)>(module.module,"GDGetNativeAbiInfo"),Export<decltype(&GDConfigureTypographyV2)>(module.module,"GDConfigureTypographyV2"),Export<decltype(&GDMeasureTextV2)>(module.module,"GDMeasureTextV2"),Export<decltype(&GDRenderSurfaceV2)>(module.module,"GDRenderSurfaceV2"),Export<decltype(&GDGetTypographyCacheStats)>(module.module,"GDGetTypographyCacheStats"),Export<decltype(&GDRenderSurface)>(module.module,"GDRenderSurface")};
    auto foreground=GetForegroundWindow();ContextLifetime context{nullptr,destroy};HWND window{};check_hresult(create(nullptr,nullptr,&context.context,&window));
    auto result=CheckTypography(context.context,api);Require(!IsWindowVisible(window)&&GetForegroundWindow()==foreground,"loaded-DLL typography test stays hidden and preserves foreground");
    std::cout<<"{\"passed\":true,\"testKind\":\"loaded DLL hidden typography ABI V2\",\"neverVisible\":true,\"systemSettingsChanged\":false,\"typography\":";WriteTypographyResult(result);std::cout<<"}\n";return 0;
}
// Compatibility aliases now verify the capture-free material boundary. They
// no longer capture the desktop or simulate abrupt worker termination.
int HiddenLiquid(wchar_t const* /*path*/){return MaterialProfile();}
int ActiveLiquidCycles(unsigned /*cycles*/){return MaterialProfile();}
int CaptureOwnerExit(){return MaterialProfile();}
int LoadedDllTopmost(wchar_t const* path)
{
    ModuleLifetime module{LoadLibraryExW(path,nullptr,LOAD_LIBRARY_SEARCH_DLL_LOAD_DIR|LOAD_LIBRARY_SEARCH_DEFAULT_DIRS)};
    check_bool(module.module!=nullptr);
    auto create=Export<decltype(&GDCreate)>(module.module,"GDCreate");
    auto destroy=Export<decltype(&GDDestroy)>(module.module,"GDDestroy");
    auto place=Export<decltype(&GDSetPlacement)>(module.module,"GDSetPlacement");
    auto visible=Export<decltype(&GDSetVisible)>(module.module,"GDSetVisible");
    auto foreground=GetForegroundWindow();ContextLifetime context{nullptr,destroy};HWND window{};
    check_hresult(create(nullptr,nullptr,&context.context,&window));
    // Exercise the real visible-window path entirely outside the virtual desktop.
    int x=GetSystemMetrics(SM_XVIRTUALSCREEN)+GetSystemMetrics(SM_CXVIRTUALSCREEN)+2048;
    int y=GetSystemMetrics(SM_YVIRTUALSCREEN)+2048;
    check_hresult(place(context.context,x,y));
    check_hresult(visible(context.context,1));Pump();
    Require((GetWindowLongPtrW(window,GWL_EXSTYLE)&WS_EX_TOPMOST)!=0,"show restores topmost without activation");
    RECT before{};GetWindowRect(window,&before);
    check_bool(SetWindowPos(window,HWND_NOTOPMOST,0,0,0,0,SWP_NOMOVE|SWP_NOSIZE|SWP_NOACTIVATE));Pump();
    Require((GetWindowLongPtrW(window,GWL_EXSTYLE)&WS_EX_TOPMOST)!=0,"visible host repairs explicit topmost demotion");
    RECT after{};GetWindowRect(window,&after);
    Require(EqualRect(&before,&after)&&after.left==x&&after.top==y,"z-order repair preserves offscreen geometry");
    check_hresult(visible(context.context,0));
    check_bool(SetWindowPos(window,HWND_NOTOPMOST,0,0,0,0,SWP_NOMOVE|SWP_NOSIZE|SWP_NOACTIVATE));Pump();
    Require(!IsWindowVisible(window)&&(GetWindowLongPtrW(window,GWL_EXSTYLE)&WS_EX_TOPMOST)==0,"hidden host is neither shown nor forcibly raised");
    check_hresult(visible(context.context,1));Pump();
    Require((GetWindowLongPtrW(window,GWL_EXSTYLE)&WS_EX_TOPMOST)!=0,"show after hide repairs the topmost band again");
    check_hresult(visible(context.context,0));
    check_hresult(destroy(context.context));context.context=nullptr;
    Require(!IsWindow(window)&&GetForegroundWindow()==foreground,"topmost fixture releases its window and preserves foreground");
    std::cout<<"{\"passed\":true,\"testKind\":\"loaded DLL offscreen topmost repair\",\"cases\":6,\"foregroundPreserved\":true,\"systemSettingsChanged\":false,\"desktopInputSent\":false}\n";
    return 0;
}
int LoadedDllTracking(wchar_t const* path)
{
    // This mode calls the supplied binary's exported ABI, rather than the
    // implementation compiled into this fixture. It never reads its Host.
    ModuleLifetime module{LoadLibraryExW(path,nullptr,LOAD_LIBRARY_SEARCH_DLL_LOAD_DIR|LOAD_LIBRARY_SEARCH_DEFAULT_DIRS)};
    check_bool(module.module!=nullptr);
    auto create=Export<decltype(&GDCreate)>(module.module,"GDCreate");
    auto destroy=Export<decltype(&GDDestroy)>(module.module,"GDDestroy");
    auto hide=Export<decltype(&GDSetVisible)>(module.module,"GDSetVisible");
    auto getContext=Export<decltype(&GDGetContext)>(module.module,"GDGetContext");
    auto foreground=GetForegroundWindow();PointerCounts counts{};
    ContextLifetime context{nullptr,destroy};HWND window{};
    check_hresult(create(CountPointers,&counts,&context.context,&window));
    Require(!IsWindowVisible(window),"DLL fixture must start hidden");
    GDContext geometry{};check_hresult(getContext(context.context,&geometry));
    auto center=MAKELPARAM(int(std::lround(geometry.width*geometry.dpi/192.0f)),int(std::lround(geometry.height*geometry.dpi/192.0f)));
    check_hresult(hide(context.context,0));
    // A hidden HWND can still receive a mouse message queued before hiding.
    // Check both synchronous dispatch and delivery through the message queue.
    SendMessageW(window,WM_MOUSEMOVE,0,center);
    int sentEnters=counts.enters,sentMoves=counts.moves;
    check_hresult(hide(context.context,0));
    int beforeEnters=counts.enters,beforeMoves=counts.moves;
    check_bool(PostMessageW(window,WM_MOUSEMOVE,0,center));Pump();
    int postedEnters=counts.enters-beforeEnters,postedMoves=counts.moves-beforeMoves;
    bool foregroundPreserved=GetForegroundWindow()==foreground;
    bool neverVisible=!IsWindowVisible(window);
    bool passed=sentEnters==0&&sentMoves==0&&postedEnters==0&&postedMoves==0&&foregroundPreserved&&neverVisible;
    check_hresult(destroy(context.context));context.context=nullptr;
    Require(!IsWindow(window),"DLL fixture must destroy the hidden HWND");
    std::cout<<"{\"passed\":"<<(passed?"true":"false")<<",\"testKind\":\"hidden mouse messages against loaded DLL\",\"systemSettingsChanged\":false,\"hiddenTrackingCases\":2,\"hiddenSendEnter\":"<<sentEnters<<",\"hiddenSendMove\":"<<sentMoves<<",\"hiddenPostEnter\":"<<postedEnters<<",\"hiddenPostMove\":"<<postedMoves<<",\"foregroundPreserved\":"<<(foregroundPreserved?"true":"false")<<",\"neverVisible\":"<<(neverVisible?"true":"false")<<"}\n";
    return passed?0:1;
}
std::vector<unsigned char> Bitmap(unsigned color)
{
    constexpr int width=1024,height=16;
    std::vector<unsigned char> bytes(sizeof(BITMAPFILEHEADER)+sizeof(BITMAPINFOHEADER)+width*height*4);
    BITMAPFILEHEADER file{};file.bfType=0x4D42;file.bfSize=static_cast<DWORD>(bytes.size());file.bfOffBits=sizeof(file)+sizeof(BITMAPINFOHEADER);
    BITMAPINFOHEADER info{};info.biSize=sizeof(info);info.biWidth=width;info.biHeight=height;info.biPlanes=1;info.biBitCount=32;info.biCompression=BI_RGB;
    std::memcpy(bytes.data(),&file,sizeof(file));std::memcpy(bytes.data()+sizeof(file),&info,sizeof(info));
    for(size_t index=file.bfOffBits;index<bytes.size();index+=4)std::memcpy(bytes.data()+index,&color,4);
    return bytes;
}
}
int wmain(int argc,wchar_t** argv)
{
    try
    {
        SetProcessDpiAwarenessContext(DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2);
        init_apartment(apartment_type::single_threaded);
        Require(!gd::CaptureWaitExpired(99,100),"pre-start timestamp must not underflow into a false capture timeout");
        Require(!gd::CaptureWaitExpired(5099,100)&&gd::CaptureWaitExpired(5100,100),"capture timeout must respect the five-second boundary");
        if(argc==3&&std::wcscmp(argv[1],L"--material-samples")==0)return MaterialSamples(argv[2]);
        if(argc==2&&std::wcscmp(argv[1],L"--material-profile")==0)return MaterialProfile();
        if(argc==2&&std::wcscmp(argv[1],L"--tension-profile")==0)return TensionProfile();
        if(argc==3&&std::wcscmp(argv[1],L"--tension-samples")==0)return TensionSamples(argv[2]);
        if(argc==3&&std::wcscmp(argv[1],L"--tracking-dll")==0)return LoadedDllTracking(argv[2]);
        if(argc==3&&std::wcscmp(argv[1],L"--topmost-dll")==0)return LoadedDllTopmost(argv[2]);
        if(argc==3&&std::wcscmp(argv[1],L"--typography-dll")==0)return LoadedDllTypography(argv[2]);
        if(argc==3&&std::wcscmp(argv[1],L"--liquid-hidden")==0)return HiddenLiquid(argv[2]);
        if(argc==3&&std::wcscmp(argv[1],L"--liquid-cycles")==0)return ActiveLiquidCycles(static_cast<unsigned>(std::wcstoul(argv[2],nullptr,10)));
        if(argc==2&&std::wcscmp(argv[1],L"--capture-owner-exit")==0)return CaptureOwnerExit();
        Require(argc==1,"usage: GlassDesk.NativeRegression.exe [--tracking-dll absolute-DLL-path | --typography-dll absolute-DLL-path | --liquid-hidden absolute-PNG-path | --liquid-cycles count]");
        HWND window{};check_hresult(GDCreate(Callback,nullptr,&testContext,&window));
        auto host=gd::FromContext(testContext);
        Require(!IsWindowVisible(window)&&!IsWindowVisible(host->shadow),"fixture must remain hidden");
        auto shadow=host->shadow;
        auto foreground=GetForegroundWindow();
        GDFrame frame{272.5f,178.25f,23.125f,20,.62f,0};check_hresult(GDApplyFrame(testContext,&frame));
        for(unsigned dpiValue:{96u,120u,144u,192u,96u,144u})
        {
            RECT suggestion{40,60,500,400};SendMessageW(window,WM_DPICHANGED,MAKELONG(dpiValue,dpiValue),reinterpret_cast<LPARAM>(&suggestion));
            GDContext context{};check_hresult(GDGetContext(testContext,&context));RECT rectangle{};GetClientRect(window,&rectangle);
            Require(context.width==frame.width&&context.height==frame.height&&context.dpi==dpiValue,"DPI must preserve current animated DIP frame");
            Require(rectangle.right==int(std::lround(frame.width*dpiValue/96))&&rectangle.bottom==int(std::lround(frame.height*dpiValue/96)),"canonical DIP to pixel conversion");
        }
        SendMessageW(window,WM_SETTINGCHANGE,0,0);
        Require(host->dpi==host->CurrentMonitorDpi(),"setting notification must repair stale DPI");
        // Exercise visible-model input while the actual HWND remains hidden.
        // This reproduces an armed tracking state without showing a window.
        auto hiddenCenter=MAKELPARAM(int(std::lround(frame.width*host->Scale()/2)),int(std::lround(frame.height*host->Scale()/2)));
        host->visible=true;int beforeArmEnters=enterCount;
        SendMessageW(window,WM_MOUSEMOVE,0,hiddenCenter);
        Require(host->tracking&&enterCount==beforeArmEnters+1,"visible model must arm mouse tracking");
        Require(!IsWindowVisible(window)&&!IsWindowVisible(shadow),"visible model must not show either HWND");
        check_hresult(GDSetVisible(testContext,0));
        bool trackingClearedOnHide=!host->tracking;
        int beforeHiddenEnters=enterCount,beforeHiddenMoves=moveCount;
        check_bool(PostMessageW(window,WM_MOUSEMOVE,0,hiddenCenter));Pump();
        int hiddenEnters=enterCount-beforeHiddenEnters,hiddenMoves=moveCount-beforeHiddenMoves;
        bool hiddenMessageSuppressed=hiddenEnters==0&&hiddenMoves==0&&!host->tracking;
        // Restore only the logical model, preserving the never-visible rule.
        // The first inside move must emit a fresh Enter for the Core hover timer.
        host->visible=true;int beforeRestoreEnters=enterCount;
        SendMessageW(window,WM_MOUSEMOVE,0,hiddenCenter);
        int restoredEnter=enterCount-beforeRestoreEnters;
        bool freshEnterAfterRestore=restoredEnter==1&&host->tracking;
        check_hresult(GDSetVisible(testContext,0));
        bool hiddenTrackingPassed=trackingClearedOnHide&&hiddenMessageSuppressed&&freshEnterAfterRestore;
        Require(!IsWindowVisible(window)&&!IsWindowVisible(shadow),"mouse regression must remain hidden");
        HRESULT wrongThread=S_OK;
        std::thread worker([&]{GDContext context{};wrongThread=GDGetContext(testContext,&context);});worker.join();
        Require(wrongThread==RPC_E_WRONG_THREAD,"owner thread guard");
        ::IInspectable* compositor{};::IInspectable* content{};
        check_hresult(GDGetComposition(testContext,&compositor,&content));compositor->Release();content->Release();
        GDOp operations[8]{};
        operations[0]={0,2,2,100,20,0,14,0x44222222};
        operations[1]={1,4,28,110,24,8,14,0x88606060};
        operations[2]={2,8,2,150,48,0,14,0xff202632,1,1,L"音乐 / Unicode / DPI"};
        operations[3]={3,160,4,64,64,12,14,0xffffffff};
        operations[4]={4,90,90,-30,-15,1.5f,14,0xff202632};
        operations[5]={5,12,90,18,18,0,14,0xff559977};
        operations[6]={6,190,88,24,24,0,24,0xff202632,0,1,L"\uE768"};
        operations[7]={7,34,80,148,40,0,12,0xff202632,0,0,L"Two lines of status text / 换行"};
        for(unsigned color:{0xff0044ffu,0xff88cc22u,0xffaa44bbu,0xff4499aau})
        {
            auto bytes=Bitmap(color);operations[3].image=bytes.data();operations[3].imageSize=static_cast<unsigned>(bytes.size());
            ::IInspectable* result{};check_hresult(GDRenderSurface(testContext,240,130,operations,8,&result));result->Release();
            Require(!host->images.empty()&&host->images.size()<=3,"decoded bitmap cache is bounded");
            auto size=host->images.front().bitmap->GetPixelSize();Require(std::max(size.width,size.height)<=768,"artwork decode limit");
        }
        Require(host->images.size()==3,"recent-three cache eviction");
        host->CreateGraphics();host->CreateGrain();
        operations[3].image=nullptr;operations[3].imageSize=0;
        ::IInspectable* result{};check_hresult(GDRenderSurface(testContext,240,130,operations,8,&result));result->Release();
        PostMessageW(window,gd::RecoveredMessage,0,0);Pump();Require(recoveryCount==1,"queued graphics redraw signal");
        TypographyApi typographyApi{GDGetNativeAbiInfo,GDConfigureTypographyV2,GDMeasureTextV2,GDRenderSurfaceV2,GDGetTypographyCacheStats,GDRenderSurface};
        auto typographyResult=CheckTypography(testContext,typographyApi,host.get());
        // Repeated geometry changes must release the previous HBITMAP/DC. These
        // are hidden CPU/ABI checks, not a claim about presented 60 FPS.
        auto gdiBefore=GetGuiResources(GetCurrentProcess(),GR_GDIOBJECTS);
        auto userBefore=GetGuiResources(GetCurrentProcess(),GR_USEROBJECTS);
        wchar_t liquidStatus[512]{};
        for(int index=0;index<100;++index)
        {
            check_hresult(GDSetLiquidGlassEnabled(testContext,1));check_hresult(GDGetLiquidGlassStatus(testContext,liquidStatus,512));
            Require(host->liquidEnabled&&std::wcsstr(liquidStatus,L"hidden"),"hidden enabled liquid must preserve user intent and report pause");
            Require(!host->liquid->Surface()&&!host->liquid->Ready(),"hidden liquid must allocate no capture/material surface");
            check_hresult(GDSetLiquidGlassEnabled(testContext,0));
        }
        for(int index=0;index<1000;++index)
        {
            auto height=index%3==0?48.0f:(index%3==1?132.0f:464.0f);
            auto width=index%3==0?232.0f:360.0f;
            GDFrame value{width,height,24,20,.62f,0};check_hresult(GDApplyFrame(testContext,&value));
            if(index%20==0)Pump();
        }
        auto gdiAfter=GetGuiResources(GetCurrentProcess(),GR_GDIOBJECTS);
        auto userAfter=GetGuiResources(GetCurrentProcess(),GR_USEROBJECTS);
        HRGN region=CreateRectRgn(0,0,0,0);Require(GetWindowRgn(window,region)!=ERROR,"rounded HWND region exists");
        RECT finalClient{};GetClientRect(window,&finalClient);
        Require(!PtInRegion(region,0,0)&&PtInRegion(region,finalClient.right/2,0),"AA margin preserves top pixels and excludes corner input");DeleteObject(region);
        Require(gdiAfter<=gdiBefore+1&&userAfter<=userBefore+1,"1000 geometry changes must not leak GDI/USER handles");
        auto provider=host->provider;
        quitInCallback=true;SendMessageW(window,WM_CLOSE,0,0);
        Require(testContext==nullptr&&!IsWindow(window)&&!IsWindow(shadow),"reentrant Quit callback must dispose both HWNDs");
        VARIANT property{};Require(provider->GetPropertyValue(UIA_NamePropertyId,&property)==UIA_E_ELEMENTNOTAVAILABLE,"retained UIA provider must be safely disconnected");
        Require(GetForegroundWindow()==foreground,"hidden fixture must preserve foreground");
        std::cout<<"{\"passed\":"<<(hiddenTrackingPassed?"true":"false")<<",\"testKind\":\"hidden ABI/message regression\",\"systemSettingsChanged\":false,\"dpiCases\":6,\"geometryChanges\":1000,\"hiddenTrackingCases\":3,\"trackingClearedOnHide\":"<<(trackingClearedOnHide?"true":"false")<<",\"hiddenMessageSuppressed\":"<<(hiddenMessageSuppressed?"true":"false")<<",\"freshEnterAfterRestore\":"<<(freshEnterAfterRestore?"true":"false")<<",\"hiddenEnter\":"<<hiddenEnters<<",\"hiddenMove\":"<<hiddenMoves<<",\"restoredEnter\":"<<restoredEnter<<",\"gdiBefore\":"<<gdiBefore<<",\"gdiAfter\":"<<gdiAfter<<",\"userBefore\":"<<userBefore<<",\"userAfter\":"<<userAfter<<",\"callbacks\":"<<callbackCount<<",\"typography\":";
        WriteTypographyResult(typographyResult);std::cout<<"}\n";
        return hiddenTrackingPassed?0:1;
    }
    catch(hresult_error const& error)
    {std::cerr<<"native-regression HRESULT 0x"<<std::hex<<static_cast<unsigned>(error.code())<<"\n";return 1;}
    catch(std::exception const& error){std::cerr<<error.what()<<"\n";return 1;}
}

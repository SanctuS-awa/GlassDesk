#error Screen_capture_is_prohibited_by_the_GlassDesk_user_requirement
// Standalone, never-visible WGC diagnostic. No host, Composition, shader,
// pointer input, or settings mutation. --exclude changes capture affinity only
// on two disposable, physically hidden diagnostic HWNDs owned by this process.
#define WIN32_LEAN_AND_MEAN
#define NOMINMAX
#include <windows.h>
#include <d3d11.h>
#include <d3d11_4.h>
#include <dxgi.h>
#include <windows.graphics.capture.interop.h>
#include <windows.graphics.directx.direct3d11.interop.h>
#include <winrt/Windows.Graphics.Capture.h>
#include <winrt/Windows.Foundation.h>
#include <winrt/Windows.Graphics.DirectX.Direct3D11.h>
#include <atomic>
#include <memory>
#include <iostream>
#include <cwchar>
#include <cstring>

using namespace winrt;
using namespace winrt::Windows::Graphics::Capture;
using namespace winrt::Windows::Graphics::DirectX;
using namespace winrt::Windows::Graphics::DirectX::Direct3D11;
struct Arrival
{
    std::atomic<unsigned> callbacks{},frames{},width{},height{};
    std::atomic<HRESULT> error{S_OK};
};
int wmain(int argc,wchar_t** argv)
{
    char const* stage="initialize";
    try
    {
        bool warp=false,mta=false,noCursor=false,exclude=false;
        for(int i=1;i<argc;++i){warp|=std::wcscmp(argv[i],L"--warp")==0;mta|=std::wcscmp(argv[i],L"--mta")==0;noCursor|=std::wcscmp(argv[i],L"--no-cursor")==0;exclude|=std::wcscmp(argv[i],L"--exclude")==0;}
        SetProcessDpiAwarenessContext(DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2);
        init_apartment(mta?apartment_type::multi_threaded:apartment_type::single_threaded);
        DWORD processSession{},activeSession=WTSGetActiveConsoleSessionId();ProcessIdToSessionId(GetCurrentProcessId(),&processSession);
        char station[256]{};DWORD bytes{};GetUserObjectInformationA(GetProcessWindowStation(),UOI_NAME,station,sizeof(station),&bytes);
        auto desktop=OpenInputDesktop(0,FALSE,DESKTOP_READOBJECTS);char inputDesktop[256]{};
        if(desktop){GetUserObjectInformationA(desktop,UOI_NAME,inputDesktop,sizeof(inputDesktop),&bytes);CloseDesktop(desktop);}
        stage="IsSupported";bool supported=GraphicsCaptureSession::IsSupported();
        std::cout<<"{\"processSession\":"<<processSession<<",\"activeSession\":"<<activeSession<<",\"windowStation\":\""<<station<<"\",\"inputDesktop\":\""<<inputDesktop<<"\",\"captureSupported\":"<<(supported?"true":"false")<<"}\n";
        if(!supported)return 2;
        stage="CreateD3D11Device";com_ptr<ID3D11Device> d3d;com_ptr<ID3D11DeviceContext> context;D3D_FEATURE_LEVEL level{};
        check_hresult(D3D11CreateDevice(nullptr,warp?D3D_DRIVER_TYPE_WARP:D3D_DRIVER_TYPE_HARDWARE,nullptr,D3D11_CREATE_DEVICE_BGRA_SUPPORT,nullptr,0,D3D11_SDK_VERSION,d3d.put(),&level,context.put()));
        auto multithread=context.as<ID3D11Multithread>();multithread->SetMultithreadProtected(TRUE);
        stage="BridgeDevice";auto dxgi=d3d.as<IDXGIDevice>();com_ptr<::IInspectable> inspectable;
        check_hresult(CreateDirect3D11DeviceFromDXGIDevice(dxgi.get(),inspectable.put()));auto device=inspectable.as<IDirect3DDevice>();
        auto monitor=MonitorFromPoint({0,0},MONITOR_DEFAULTTOPRIMARY);MONITORINFOEXW info{};info.cbSize=sizeof(info);check_bool(GetMonitorInfoW(monitor,&info));
        stage="CreateForMonitor";auto factory=get_activation_factory<GraphicsCaptureItem,IGraphicsCaptureItemInterop>();GraphicsCaptureItem item{nullptr};
        check_hresult(factory->CreateForMonitor(monitor,guid_of<GraphicsCaptureItem>(),put_abi(item)));auto size=item.Size();
        stage="CreateFreeThreaded";auto pool=Direct3D11CaptureFramePool::CreateFreeThreaded(device,DirectXPixelFormat::B8G8R8A8UIntNormalized,2,size);
        auto state=std::make_shared<Arrival>();
        auto token=pool.FrameArrived([state](auto const& sender,auto const&)
        {
            ++state->callbacks;
            try
            {
                auto frame=sender.TryGetNextFrame();if(!frame)return;
                auto dimensions=frame.ContentSize();state->width=static_cast<unsigned>(dimensions.Width);state->height=static_cast<unsigned>(dimensions.Height);
                auto access=frame.Surface().template as<::Windows::Graphics::DirectX::Direct3D11::IDirect3DDxgiInterfaceAccess>();
                com_ptr<ID3D11Texture2D> texture;check_hresult(access->GetInterface(__uuidof(ID3D11Texture2D),texture.put_void()));
                ++state->frames;frame.Close();
            }
            catch(...){state->error=to_hresult();}
        });
        HWND excludedBody{},excludedShadow{};
        if(exclude)
        {
            excludedBody=CreateWindowExW(WS_EX_TOOLWINDOW|WS_EX_NOACTIVATE|WS_EX_TOPMOST|WS_EX_NOREDIRECTIONBITMAP,L"STATIC",L"Never-visible WGC body",WS_POPUP,80,80,200,52,nullptr,nullptr,GetModuleHandleW(nullptr),nullptr);
            excludedShadow=CreateWindowExW(WS_EX_TOOLWINDOW|WS_EX_NOACTIVATE|WS_EX_TOPMOST|WS_EX_TRANSPARENT|WS_EX_LAYERED,L"STATIC",L"Never-visible WGC shadow",WS_POPUP,64,64,232,84,nullptr,nullptr,GetModuleHandleW(nullptr),nullptr);
            check_bool(excludedBody&&excludedShadow);check_bool(SetWindowDisplayAffinity(excludedBody,WDA_EXCLUDEFROMCAPTURE));check_bool(SetWindowDisplayAffinity(excludedShadow,WDA_EXCLUDEFROMCAPTURE));
        }
        auto session=pool.CreateCaptureSession(item);if(noCursor)session.IsCursorCaptureEnabled(false);stage="StartCapture";session.StartCapture();
        auto until=GetTickCount64()+3500;MSG message{};
        while(GetTickCount64()<until)
        {
            while(PeekMessageW(&message,nullptr,0,0,PM_REMOVE)){TranslateMessage(&message);DispatchMessageW(&message);}
            MsgWaitForMultipleObjectsEx(0,nullptr,34,QS_ALLINPUT,MWMO_INPUTAVAILABLE);
        }
        pool.FrameArrived(token);session.Close();pool.Close();
        if(excludedBody)DestroyWindow(excludedBody);if(excludedShadow)DestroyWindow(excludedShadow);
        std::cout<<"{\"test\":\"standalone official-equivalent WGC receive\",\"warp\":"<<(warp?"true":"false")<<",\"mta\":"<<(mta?"true":"false")
            <<",\"noCursor\":"<<(noCursor?"true":"false")<<",\"exclude\":"<<(exclude?"true":"false")<<",\"monitorRect\":["<<info.rcMonitor.left<<","<<info.rcMonitor.top<<","<<info.rcMonitor.right<<","<<info.rcMonitor.bottom<<"],\"itemWidth\":"<<size.Width<<",\"itemHeight\":"<<size.Height
            <<",\"callbacks\":"<<state->callbacks<<",\"frames\":"<<state->frames<<",\"lastWidth\":"<<state->width<<",\"lastHeight\":"<<state->height
            <<",\"callbackHresult\":"<<static_cast<unsigned>(state->error.load())<<",\"deviceHresult\":"<<static_cast<unsigned>(d3d->GetDeviceRemovedReason())<<"}\n";
        return state->frames&&SUCCEEDED(state->error.load())?0:2;
    }
    catch(hresult_error const& error){std::cerr<<"stage="<<stage<<" HRESULT=0x"<<std::hex<<static_cast<unsigned>(error.code())<<"\n";return 1;}
}

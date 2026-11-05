#error Screen_capture_is_prohibited_by_the_GlassDesk_user_requirement
#define WIN32_LEAN_AND_MEAN
#define NOMINMAX
#include "CaptureProtocol.h"
#include <d3d11_1.h>
#include <d3d11_4.h>
#include <windows.graphics.capture.interop.h>
#include <windows.graphics.directx.direct3d11.interop.h>
#include <winrt/Windows.Graphics.Capture.h>
#include <winrt/Windows.Graphics.DirectX.Direct3D11.h>
#include <winrt/Windows.Foundation.Metadata.h>
#include <shellapi.h>
#include <mutex>
#include <memory>
#include <cwchar>

using namespace winrt;
using namespace winrt::Windows::Graphics::Capture;
using namespace winrt::Windows::Graphics::DirectX;
using namespace winrt::Windows::Graphics::DirectX::Direct3D11;

namespace
{
struct Slot
{
    std::mutex mutex;
    Direct3D11CaptureFrame frame{nullptr};
    unsigned generation{};
    bool stopped{};
    HRESULT error{S_OK};
};
int Capture(gd::CaptureControl* control,HANDLE stop)
{
    init_apartment(apartment_type::multi_threaded);
    if(!GraphicsCaptureSession::IsSupported())throw hresult_error(E_NOTIMPL);
    com_ptr<IDXGIFactory1> factory;check_hresult(CreateDXGIFactory1(__uuidof(IDXGIFactory1),factory.put_void()));
    com_ptr<IDXGIAdapter1> adapter;
    for(UINT index=0;;++index)
    {
        auto hr=factory->EnumAdapters1(index,adapter.put());check_hresult(hr);
        DXGI_ADAPTER_DESC1 desc{};check_hresult(adapter->GetDesc1(&desc));
        if(desc.AdapterLuid.HighPart==control->adapter.HighPart&&desc.AdapterLuid.LowPart==control->adapter.LowPart)break;
        adapter=nullptr;
    }
    com_ptr<ID3D11Device> device;com_ptr<ID3D11DeviceContext> context;D3D_FEATURE_LEVEL level{};
    check_hresult(D3D11CreateDevice(adapter.get(),D3D_DRIVER_TYPE_UNKNOWN,nullptr,D3D11_CREATE_DEVICE_BGRA_SUPPORT,
        nullptr,0,D3D11_SDK_VERSION,device.put(),&level,context.put()));
    context.as<ID3D11Multithread>()->SetMultithreadProtected(TRUE);
    com_ptr<ID3D11Texture2D> crop;
    check_hresult(device.as<ID3D11Device1>()->OpenSharedResource1(reinterpret_cast<HANDLE>(control->textureHandle),__uuidof(ID3D11Texture2D),crop.put_void()));
    auto keyed=crop.as<IDXGIKeyedMutex>();
    com_ptr<::IInspectable> inspectable;
    check_hresult(CreateDirect3D11DeviceFromDXGIDevice(device.as<IDXGIDevice>().get(),inspectable.put()));
    auto captureDevice=inspectable.as<IDirect3DDevice>();
    auto itemFactory=get_activation_factory<GraphicsCaptureItem,IGraphicsCaptureItemInterop>();
    GraphicsCaptureItem item{nullptr};check_hresult(itemFactory->CreateForMonitor(reinterpret_cast<HMONITOR>(control->monitor),guid_of<GraphicsCaptureItem>(),put_abi(item)));
    auto size=item.Size();
    if(size.Width!=static_cast<int>(control->width)||size.Height!=static_cast<int>(control->height))throw hresult_error(DXGI_ERROR_ACCESS_LOST);
    auto pool=Direct3D11CaptureFramePool::CreateFreeThreaded(captureDevice,DirectXPixelFormat::B8G8R8A8UIntNormalized,2,size);
    auto slot=std::make_shared<Slot>();
    auto token=pool.FrameArrived([slot,control](auto const& sender,auto const&)
    {
        try
        {
            auto frame=sender.TryGetNextFrame();std::lock_guard lock(slot->mutex);
            InterlockedIncrement(&control->callbacks);
            if(!frame)return;
            if(slot->stopped){frame.Close();return;}
            if(slot->frame)slot->frame.Close();slot->frame=frame;++slot->generation;
            InterlockedIncrement(&control->received);
        }
        catch(...){std::lock_guard lock(slot->mutex);slot->error=to_hresult();}
    });
    auto session=pool.CreateCaptureSession(item);
    if(winrt::Windows::Foundation::Metadata::ApiInformation::IsPropertyPresent(L"Windows.Graphics.Capture.GraphicsCaptureSession",L"IsCursorCaptureEnabled"))session.IsCursorCaptureEnabled(false);
    session.StartCapture();InterlockedExchange(&control->error,S_OK);
    LONG lastRequest=-1;unsigned lastGeneration{};
    while(WaitForSingleObject(stop,34)==WAIT_TIMEOUT)
    {
        LONG request=InterlockedCompareExchange(&control->request,0,0);
        if(!request||(request&1))continue;
        RECT box=control->source;MemoryBarrier();
        if(request!=InterlockedCompareExchange(&control->request,0,0))continue;
        if(box.left<0||box.top<0||box.right<=box.left||box.bottom<=box.top||box.right>size.Width||box.bottom>size.Height)throw hresult_error(E_INVALIDARG);
        std::lock_guard lock(slot->mutex);check_hresult(slot->error);
        if(!slot->frame||(request==lastRequest&&slot->generation==lastGeneration))continue;
        auto actual=slot->frame.ContentSize();
        if(actual.Width!=size.Width||actual.Height!=size.Height)throw hresult_error(DXGI_ERROR_ACCESS_LOST);
        auto acquired=keyed->AcquireSync(0,0);
        if(acquired==WAIT_TIMEOUT)continue;
        if(acquired!=S_OK)throw hresult_error(FAILED(acquired)?acquired:E_FAIL);
        try
        {
            auto access=slot->frame.Surface().as<::Windows::Graphics::DirectX::Direct3D11::IDirect3DDxgiInterfaceAccess>();
            com_ptr<ID3D11Texture2D> captured;check_hresult(access->GetInterface(__uuidof(ID3D11Texture2D),captured.put_void()));
            D3D11_BOX region{static_cast<UINT>(box.left),static_cast<UINT>(box.top),0,static_cast<UINT>(box.right),static_cast<UINT>(box.bottom),1};
            context->CopySubresourceRegion(crop.get(),0,0,0,0,captured.get(),0,&region);context->Flush();
        }
        catch(...){keyed->ReleaseSync(1);throw;}
        InterlockedExchange(&control->frameRequest,request);InterlockedIncrement(&control->delivered);
        check_hresult(keyed->ReleaseSync(1));
        lastRequest=request;lastGeneration=slot->generation;
    }
    // The host owns a kill-on-close job as a bounded fallback if an OS/driver
    // blocks inside WGC Close. No worker can remain after its owner exits.
    {std::lock_guard lock(slot->mutex);slot->stopped=true;if(slot->frame)slot->frame.Close();slot->frame=nullptr;}
    pool.FrameArrived(token);session.Close();pool.Close();captureDevice.Close();
    return 0;
}
}
int WINAPI wWinMain(HINSTANCE,HINSTANCE,PWSTR,int)
{
    SetProcessDpiAwarenessContext(DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2);
    int count{};auto args=CommandLineToArgvW(GetCommandLineW(),&count);
    if(!args||count!=3){if(args)LocalFree(args);return 2;}
    auto mapping=reinterpret_cast<HANDLE>(std::wcstoull(args[1],nullptr,10));
    auto stop=reinterpret_cast<HANDLE>(std::wcstoull(args[2],nullptr,10));LocalFree(args);
    auto control=static_cast<gd::CaptureControl*>(MapViewOfFile(mapping,FILE_MAP_ALL_ACCESS,0,0,sizeof(gd::CaptureControl)));
    if(!control||control->magic!=0x47444332||!control->width||!control->height||control->width>16384||control->height>16384)return 3;
    int result{};
    try{result=Capture(control,stop);}catch(...){InterlockedExchange(&control->error,to_hresult());result=1;}
    // Frame callbacks retain the mapped block until process termination, even
    // if WGC shutdown failed. The mapping is anonymous and never written out.
    return result;
}

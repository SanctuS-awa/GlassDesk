// Original, settings-only optics. The source is a local scene and an explicitly supplied still image: no
// desktop capture, screen DC, external window texture, readback or network.
#define WIN32_LEAN_AND_MEAN
#define NOMINMAX
#include <windows.h>
#include <d3d11.h>
#include <dxgi1_3.h>
#include <winrt/base.h>
#include <algorithm>
#include <array>
#include <cmath>
#include <cstring>
#include <memory>
#include <mutex>
#include <cstdint>
#include <vector>
using namespace winrt;
namespace {
struct __declspec(uuid("63aad0b8-7c24-40ff-85a8-640d944cc325")) PreviewPanel : IUnknown {
    virtual HRESULT STDMETHODCALLTYPE SetSwapChain(IDXGISwapChain*)=0;
};
#include "LocalGlassVS.h"
#include "LocalGlassPS.h"
#include "LocalGlassBlurPS.h"

struct Shared {
    com_ptr<ID3D11Device> device;com_ptr<ID3D11DeviceContext> context;
    com_ptr<ID3D11VertexShader> vertex;com_ptr<ID3D11PixelShader> pixel,blurPixel;com_ptr<ID3D11SamplerState> sampler;
    Shared(){
        D3D_FEATURE_LEVEL level;
        HRESULT hr=D3D11CreateDevice(nullptr,D3D_DRIVER_TYPE_HARDWARE,nullptr,D3D11_CREATE_DEVICE_BGRA_SUPPORT,
            nullptr,0,D3D11_SDK_VERSION,device.put(),&level,context.put());
        if(FAILED(hr))check_hresult(D3D11CreateDevice(nullptr,D3D_DRIVER_TYPE_WARP,nullptr,D3D11_CREATE_DEVICE_BGRA_SUPPORT,
            nullptr,0,D3D11_SDK_VERSION,device.put(),&level,context.put()));
        check_hresult(device->CreateVertexShader(LocalGlassVertexShader,sizeof(LocalGlassVertexShader),nullptr,vertex.put()));
        check_hresult(device->CreatePixelShader(LocalGlassPixelShader,sizeof(LocalGlassPixelShader),nullptr,pixel.put()));
        check_hresult(device->CreatePixelShader(LocalGlassBlurPixelShader,sizeof(LocalGlassBlurPixelShader),nullptr,blurPixel.put()));
        D3D11_SAMPLER_DESC sample{};sample.Filter=D3D11_FILTER_MIN_MAG_MIP_LINEAR;
        sample.AddressU=sample.AddressV=sample.AddressW=D3D11_TEXTURE_ADDRESS_CLAMP;sample.MaxLOD=D3D11_FLOAT32_MAX;
        check_hresult(device->CreateSamplerState(&sample,sampler.put()));
    }
};
std::mutex sharedMutex;std::weak_ptr<Shared> sharedDevice;
std::shared_ptr<Shared> Device(){std::lock_guard lock(sharedMutex);auto d=sharedDevice.lock();if(!d){d=std::make_shared<Shared>();sharedDevice=d;}return d;}
struct Preview {
    DWORD thread=GetCurrentThreadId();std::shared_ptr<Shared> gpu=Device();
    com_ptr<PreviewPanel> panel;com_ptr<IDXGISwapChain1> swap;
    com_ptr<ID3D11RenderTargetView> target;com_ptr<ID3D11Buffer> constants;com_ptr<ID3D11ShaderResourceView> image;
    std::array<com_ptr<ID3D11ShaderResourceView>,2> blurImages;
    std::array<com_ptr<ID3D11RenderTargetView>,2> blurTargets;
    unsigned imageWidth=0,imageHeight=0,blurWidth=0,blurHeight=0;
    float cachedSigma=-1;
    unsigned width=0,height=0;
    explicit Preview(IUnknown* source){
        check_hresult(source->QueryInterface(__uuidof(PreviewPanel),panel.put_void()));
        D3D11_BUFFER_DESC desc{};desc.ByteWidth=256;desc.Usage=D3D11_USAGE_DYNAMIC;
        desc.BindFlags=D3D11_BIND_CONSTANT_BUFFER;desc.CPUAccessFlags=D3D11_CPU_ACCESS_WRITE;
        check_hresult(gpu->device->CreateBuffer(&desc,nullptr,constants.put()));
    }
    ~Preview(){gpu->context->OMSetRenderTargets(0,nullptr,nullptr);if(panel)panel->SetSwapChain(nullptr);}
    void SetImage(uint8_t const* pixels,unsigned w,unsigned h){
        if(thread!=GetCurrentThreadId())throw hresult_wrong_thread();
        cachedSigma=-1;
        if(!pixels){image=nullptr;imageWidth=imageHeight=0;blurImages={};blurTargets={};blurWidth=blurHeight=0;return;}
        // Composite straight-alpha images over this lab's white canvas before
        // filtering, so transparent PNG borders cannot bleed dark mip halos.
        size_t bytes=static_cast<size_t>(w)*h*4;std::vector<uint8_t> opaque;
        for(size_t i=3;i<bytes;i+=4)if(pixels[i]!=255){opaque.assign(pixels,pixels+bytes);break;}
        if(!opaque.empty())for(size_t i=0;i<bytes;i+=4){
            unsigned a=opaque[i+3];
            for(size_t channel=0;channel<3;channel++)opaque[i+channel]=static_cast<uint8_t>((opaque[i+channel]*a+255u*(255u-a)+127u)/255u);
            opaque[i+3]=255;
        }
        D3D11_TEXTURE2D_DESC desc{};desc.Width=w;desc.Height=h;desc.MipLevels=0;desc.ArraySize=1;
        desc.Format=DXGI_FORMAT_B8G8R8A8_UNORM;desc.SampleDesc.Count=1;desc.Usage=D3D11_USAGE_DEFAULT;
        desc.BindFlags=D3D11_BIND_SHADER_RESOURCE|D3D11_BIND_RENDER_TARGET;desc.MiscFlags=D3D11_RESOURCE_MISC_GENERATE_MIPS;
        com_ptr<ID3D11Texture2D> texture;check_hresult(gpu->device->CreateTexture2D(&desc,nullptr,texture.put()));
        gpu->context->UpdateSubresource(texture.get(),0,nullptr,opaque.empty()?pixels:opaque.data(),w*4,0);
        com_ptr<ID3D11ShaderResourceView> replacement;check_hresult(gpu->device->CreateShaderResourceView(texture.get(),nullptr,replacement.put()));
        // Once per loaded image, never per animation frame. The shader selects
        // a trilinear LOD for softening, rather than repeatedly shifted images.
        gpu->context->GenerateMips(replacement.get());
        image=std::move(replacement);
        imageWidth=w;imageHeight=h;
    }
    void Upload(float const* f){
        D3D11_MAPPED_SUBRESOURCE mapped{};check_hresult(gpu->context->Map(constants.get(),0,D3D11_MAP_WRITE_DISCARD,0,&mapped));
        memcpy(mapped.pData,f,256);gpu->context->Unmap(constants.get(),0);
    }
    ID3D11ShaderResourceView* PrepareBlur(float const* f){
        if(!image||f[3]>.5f||f[10]<=.001f)return image.get();
        const float iw=static_cast<float>(imageWidth),ih=static_cast<float>(imageHeight);
        const float cover=std::max(f[0]/iw,f[1]/ih);
        const float sigma=std::clamp(f[10],0.f,8.f)/cover;
        if(std::abs(sigma-cachedSigma)<.0001f&&blurImages[1])return blurImages[1].get();
        // At most ~3 texels per sigma keeps the finite Gaussian support smooth.
        // Downsampling happens before filtering, not as a final enlarged mip.
        const float divisor=std::max(1.f,sigma/3.f);
        const unsigned bw=std::max(1u,static_cast<unsigned>(std::ceil(iw/divisor)));
        const unsigned bh=std::max(1u,static_cast<unsigned>(std::ceil(ih/divisor)));
        const float fw=static_cast<float>(bw),fh=static_cast<float>(bh);
        ID3D11ShaderResourceView* empty[2]{};gpu->context->PSSetShaderResources(0,2,empty);
        gpu->context->OMSetRenderTargets(0,nullptr,nullptr);
        if(bw!=blurWidth||bh!=blurHeight){
            blurImages={};blurTargets={};
            D3D11_TEXTURE2D_DESC desc{};desc.Width=bw;desc.Height=bh;desc.MipLevels=1;desc.ArraySize=1;
            desc.Format=DXGI_FORMAT_B8G8R8A8_UNORM;desc.SampleDesc.Count=1;
            desc.BindFlags=D3D11_BIND_RENDER_TARGET|D3D11_BIND_SHADER_RESOURCE;
            for(unsigned i=0;i<2;i++){
                com_ptr<ID3D11Texture2D> texture;check_hresult(gpu->device->CreateTexture2D(&desc,nullptr,texture.put()));
                check_hresult(gpu->device->CreateRenderTargetView(texture.get(),nullptr,blurTargets[i].put()));
                check_hresult(gpu->device->CreateShaderResourceView(texture.get(),nullptr,blurImages[i].put()));
            }
            blurWidth=bw;blurHeight=bh;
        }
        D3D11_VIEWPORT viewport{0,0,static_cast<float>(bw),static_cast<float>(bh),0,1};gpu->context->RSSetViewports(1,&viewport);
        gpu->context->PSSetShader(gpu->blurPixel.get(),nullptr,0);
        std::array<float,64> blurFrame{};
        for(unsigned pass=0;pass<2;pass++){
            gpu->context->PSSetShaderResources(0,2,empty);
            auto rt=blurTargets[pass].get();gpu->context->OMSetRenderTargets(1,&rt,nullptr);
            blurFrame[48]=pass==0?1.f/fw:0;blurFrame[49]=pass==1?1.f/fh:0;
            blurFrame[50]=sigma*(pass==0?fw/iw:fh/ih);
            blurFrame[51]=pass==0?std::log2(divisor):0;
            Upload(blurFrame.data());
            auto source=pass==0?image.get():blurImages[0].get();gpu->context->PSSetShaderResources(0,1,&source);
            gpu->context->Draw(3,0);
        }
        gpu->context->PSSetShaderResources(0,2,empty);gpu->context->OMSetRenderTargets(0,nullptr,nullptr);
        cachedSigma=sigma;return blurImages[1].get();
    }
    void Render(float const* f){
        if(thread!=GetCurrentThreadId())throw hresult_wrong_thread();
        unsigned w=static_cast<unsigned>(std::ceil(f[0]*f[2])),h=static_cast<unsigned>(std::ceil(f[1]*f[2]));
        if(w!=width||h!=height){
            gpu->context->OMSetRenderTargets(0,nullptr,nullptr);target=nullptr;
            if(swap)check_hresult(swap->ResizeBuffers(2,w,h,DXGI_FORMAT_B8G8R8A8_UNORM,0));
            else{
                auto dxgi=gpu->device.as<IDXGIDevice>();com_ptr<IDXGIAdapter> adapter;check_hresult(dxgi->GetAdapter(adapter.put()));
                com_ptr<IDXGIFactory2> factory;check_hresult(adapter->GetParent(__uuidof(IDXGIFactory2),factory.put_void()));
                DXGI_SWAP_CHAIN_DESC1 desc{};desc.Width=w;desc.Height=h;desc.Format=DXGI_FORMAT_B8G8R8A8_UNORM;
                desc.SampleDesc.Count=1;desc.BufferUsage=DXGI_USAGE_RENDER_TARGET_OUTPUT;desc.BufferCount=2;
                desc.Scaling=DXGI_SCALING_STRETCH;desc.SwapEffect=DXGI_SWAP_EFFECT_FLIP_SEQUENTIAL;desc.AlphaMode=DXGI_ALPHA_MODE_IGNORE;
                check_hresult(factory->CreateSwapChainForComposition(gpu->device.get(),&desc,nullptr,swap.put()));
                check_hresult(panel->SetSwapChain(swap.get()));
            }
            com_ptr<ID3D11Texture2D> buffer;check_hresult(swap->GetBuffer(0,__uuidof(ID3D11Texture2D),buffer.put_void()));
            check_hresult(gpu->device->CreateRenderTargetView(buffer.get(),nullptr,target.put()));width=w;height=h;
        }
        // WinUI interprets swap-chain coordinates in DIP. Raster pixels must
        // be mapped back once, otherwise XAML and optics diverge at 150% DPI.
        DXGI_MATRIX_3X2_F matrix{1/f[2],0,0,1/f[2],0,0};
        check_hresult(swap.as<IDXGISwapChain2>()->SetMatrixTransform(&matrix));
        gpu->context->IASetInputLayout(nullptr);gpu->context->IASetPrimitiveTopology(D3D11_PRIMITIVE_TOPOLOGY_TRIANGLELIST);
        gpu->context->VSSetShader(gpu->vertex.get(),nullptr,0);
        auto sampler=gpu->sampler.get();gpu->context->PSSetSamplers(0,1,&sampler);
        auto cb=constants.get();gpu->context->PSSetConstantBuffers(0,1,&cb);
        auto softened=PrepareBlur(f);Upload(f);
        auto rt=target.get();gpu->context->OMSetRenderTargets(1,&rt,nullptr);
        D3D11_VIEWPORT viewport{0,0,static_cast<float>(w),static_cast<float>(h),0,1};gpu->context->RSSetViewports(1,&viewport);
        gpu->context->IASetInputLayout(nullptr);gpu->context->IASetPrimitiveTopology(D3D11_PRIMITIVE_TOPOLOGY_TRIANGLELIST);
        gpu->context->VSSetShader(gpu->vertex.get(),nullptr,0);gpu->context->PSSetShader(gpu->pixel.get(),nullptr,0);
        ID3D11ShaderResourceView* sources[2]{image.get(),softened};gpu->context->PSSetShaderResources(0,2,sources);
        gpu->context->Draw(3,0);
        check_hresult(swap->Present(0,0));
    }
};
}
extern "C" __declspec(dllexport) HRESULT __stdcall GDCreateLocalGlass(IUnknown* panel,void** result){
    if(!panel||!result)return E_POINTER;*result=nullptr;try{*result=new Preview(panel);return S_OK;}catch(...){return to_hresult();}
}
extern "C" __declspec(dllexport) HRESULT __stdcall GDRenderLocalGlass(void* context,float const* frame,unsigned count){
    if(!context||!frame)return E_POINTER;if(count!=64)return E_INVALIDARG;
    for(unsigned i=0;i<count;i++)if(!std::isfinite(frame[i]))return E_INVALIDARG;
    if(frame[0]<8||frame[0]>1200||frame[1]<8||frame[1]>640||frame[2]<.5f||frame[2]>3)return E_INVALIDARG;
    try{static_cast<Preview*>(context)->Render(frame);return S_OK;}catch(...){return to_hresult();}
}
extern "C" __declspec(dllexport) void __stdcall GDDestroyLocalGlass(void* context){delete static_cast<Preview*>(context);}

extern "C" __declspec(dllexport) HRESULT __stdcall GDSetLocalGlassImage(void* context,uint8_t const* pixels,unsigned width,unsigned height,unsigned bytes){
    if(!context)return E_POINTER;
    if(pixels&&(width==0||height==0||width>1600||height>1600||bytes!=width*height*4))return E_INVALIDARG;
    try{static_cast<Preview*>(context)->SetImage(pixels,width,height);return S_OK;}catch(...){return to_hresult();}
}

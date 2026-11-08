#define WIN32_LEAN_AND_MEAN
#define NOMINMAX
#include "LiquidGlass.h"
#include "SurfaceTension.h"
#include <windows.ui.composition.interop.h>
#include <winrt/Windows.Foundation.h>
#include <d3dcompiler.h>
#include <d3d11_1.h>
#include <dxgi.h>
#include <wincodec.h>
#include <algorithm>
#include <array>
#include <cmath>
#include <cstring>
#include <vector>

using namespace winrt;
using namespace winrt::Windows::Graphics::DirectX;
using namespace winrt::Windows::UI::Composition;

namespace gd
{
namespace
{
// The host uses only Optics, a procedural highlight independent of desktop pixels.
// Legacy blur/refraction helpers remain in this compatibility unit; the exposed
// production surface path is procedural optics and does not read pixels back.
constexpr char shader[]=R"HLSL(
cbuffer Parameters : register(b0) {
    float2 textureSize; float2 panelSize;
    float2 panelOrigin; float radius; float blurRadius;
    float pixelScale; float refraction; float2 padding;
    float4 lenses[3]; float4 lensStyles[3];
};
Texture2D source : register(t0);
Texture2D unfiltered : register(t1);
SamplerState linearClamp : register(s0);
struct Varying { float4 position : SV_POSITION; float2 uv : TEXCOORD; };
Varying VS(uint id : SV_VertexID) {
    Varying o; o.uv=float2((id<<1)&2,id&2);
    o.position=float4(o.uv*float2(2,-2)+float2(-1,1),0,1); return o;
}
float4 Blur(float2 uv,float2 direction) {
    float3 result=source.SampleLevel(linearClamp,uv,0).rgb;
    if(blurRadius>=0.01) {
        float sigma=max(blurRadius*0.48,0.01);
        float stride=blurRadius/6.0;
        float3 color=0; float weightSum=0;
        [unroll] for(int i=-6;i<=6;i++) {
            float normalized=i*stride/sigma;
            float weight=exp(-0.5*normalized*normalized);
            color+=source.SampleLevel(linearClamp,uv+direction*(i*stride)/textureSize,0).rgb*weight;
            weightSum+=weight;
        }
        result=color/max(weightSum,0.0001);
    }
    return float4(result,1);
}
float4 Horizontal(Varying i) : SV_TARGET { return Blur(i.uv,float2(1,0)); }
float4 Prefilter(Varying i) : SV_TARGET {
    if(blurRadius<4) return source.SampleLevel(linearClamp,i.uv,0);
    float2 d=(blurRadius>=12?1.0:0.5)/textureSize;
    return (source.SampleLevel(linearClamp,i.uv+float2(-d.x,-d.y),0)
        +source.SampleLevel(linearClamp,i.uv+float2(d.x,-d.y),0)
        +source.SampleLevel(linearClamp,i.uv+float2(-d.x,d.y),0)
        +source.SampleLevel(linearClamp,i.uv+d,0))*0.25;
}

// Original GlassDesk optics. No third-party implementation copied.
struct Profile { float2 normal; float inside; float lens; float width; float coverage; float3 dome; };
float Bump(float u) { float b=max(0.0,1.0-u*u);return b*b*b; }
float WarpFactor(float y) {
    float v=saturate(y/panelSize.y),strain=padding.x;
    float belly=4.0*v*(1.0-v),direction=strain>=0.0?1.0:-1.0;
    float amount=abs(strain)*belly*belly*(0.78+0.22*direction*(2.0*v-1.0));
    float limit=min(7.0*pixelScale,min(panelSize.y*0.035,panelSize.x*0.018));
    return 1.0-min(amount,1.15)*limit/(panelSize.x*0.5);
}
float SurfaceDistance(float2 p) {
    float factor=WarpFactor(p.y);p.x=panelSize.x*0.5+(p.x-panelSize.x*0.5)/factor;
    float2 halfPanel=panelSize*0.5;
    float r=clamp(radius,0.0,min(halfPanel.x,halfPanel.y));
    float2 q=abs(p-halfPanel)-(halfPanel-r);
    return (length(max(q,0.0))+min(max(q.x,q.y),0.0)-r)*factor;
}
Profile Shape(float2 p) {
    Profile o;
    float2 halfPanel=panelSize*0.5;
    float r=clamp(radius,0.0,min(halfPanel.x,halfPanel.y));
    float tension=padding.x;
    float2 original=p;
    float factor=WarpFactor(p.y);
    p.x=halfPanel.x+(p.x-halfPanel.x)/factor;
    float2 local=p-halfPanel;
    float2 q=abs(local)-(halfPanel-r);
    float2 corner=max(q,0.0);
    float len=length(corner);
    float sd=len+min(max(q.x,q.y),0.0)-r;
    o.normal=len>0.0001?corner/max(len,0.0001)*sign(local)
        :q.x>q.y?float2(sign(local.x),0):float2(0,sign(local.y));
    if(abs(tension)>0.0001) {
        sd=SurfaceDistance(original);
        float step=max(pixelScale*0.6,0.5);
        float2 gradient=float2(SurfaceDistance(original+float2(step,0))-SurfaceDistance(original-float2(step,0)),
            SurfaceDistance(original+float2(0,step))-SurfaceDistance(original-float2(0,step)));
        o.normal=gradient/max(length(gradient),0.0001);
    }
    o.inside=max(-sd,0.0);
    float scale=max(pixelScale,0.5);
    o.width=max(0.5,min(clamp(r*0.35,5.0*scale,12.0*scale)*(1.0+0.12*abs(tension)),min(halfPanel.x,halfPanel.y)*0.94));
    float t=saturate(o.inside/o.width);
    o.lens=1.0-t*t*t*(t*(t*6.0-15.0)+10.0);
    float arc=sqrt(max(t*(2.0-t),0.001));
    float slope=(1.0-t)/max(arc,0.12);
    o.dome=normalize(float3(o.normal*slope*0.65,1.0));
    o.coverage=saturate(0.5-sd);
    return o;
}
float4 Optics(Varying i) : SV_TARGET {
    Profile s=Shape(i.uv*panelSize);
    float scale=max(pixelScale,0.5);
    float facing=dot(s.normal,normalize(float2(-0.55,-1.0)));
    float front=pow(saturate(facing),4.0);
    float back=pow(saturate(-facing),5.0);
    float rim=exp(-pow(s.inside/max(scale*(0.5+1.1*front+0.6*back),0.1),2.0));
    float3 light=normalize(float3(-0.55,-1.0,0.85));
    float3 halfLight=normalize(light+float3(0,0,1));
    float spec=pow(saturate(dot(s.dome,halfLight)),42.0)*s.lens;
    float shoulder=4.0*s.lens*(1.0-s.lens);
    float shine=saturate(rim*(0.025+0.50*front+0.24*back)+spec*0.16);
    float absorption=saturate(shoulder*(0.008+0.022*back+0.005*(1.0-front))
        +exp(-s.inside/max(1.1*scale,0.1))*(0.008+0.045*back));
    // Premultiplied reflection plus absorption, independent of screen pixels.
    float alpha=1.0-(1.0-absorption)*(1.0-shine);
    return float4(shine.xxx,alpha)*s.coverage;
}
float4 Over(float4 front,float4 back){return front+back*(1-front.a);}
float LocalDistance(float2 p,float4 lens,float r){
    r=clamp(r,0,min(lens.z,lens.w)*.5);
    float2 q=abs(p-lens.xy)-lens.zw*.5+r;
    return length(max(q,0))+min(max(q.x,q.y),0)-r;
}
float4 SystemOptics(Varying i) : SV_TARGET {
    // SV_POSITION stays in physical pixels even though the retained surface is
    // larger than the current panel. Exactly the SceneMesh's SDF and cap profile.
    float2 p=i.position.xy;
    float sd=SurfaceDistance(p),depth=max(0.0,-sd);
    float epsilon=0.4;
    float2 gradient=float2(SurfaceDistance(p+float2(epsilon,0))-SurfaceDistance(p-float2(epsilon,0)),
        SurfaceDistance(p+float2(0,epsilon))-SurfaceDistance(p-float2(0,epsilon)));
    float2 outward=gradient/max(length(gradient),0.0001);
    float band=padding.y,u=1.0-saturate(depth/band);
    float slope=0.62*u/sqrt(max(1.0-u*u,0.003));
    float3 normal=normalize(float3(outward*slope,1));
    float highlight=pow(saturate(dot(normal,normalize(float3(-.48,-.64,1)))),10);
    float fresnel=pow(1-normal.z,3);
    float key=pow(saturate(dot(outward,normalize(float2(-.55,-.83)))),3);
    float counter=pow(saturate(dot(outward,normalize(float2(.65,.76)))),5);
    float rim=exp(-pow((depth-1.1*pixelScale)/(1.3*pixelScale),2));
    // Match the reference preview's single direction of illumination. A broad
    // shoulder belongs to the curved cap, not to a second outlined rectangle.
    float shoulder=4*u*(1-u);
    float shine=saturate(highlight*.09+rim*(key*.24+counter*.065)+fresnel*.055
        +shoulder*(key*.032+counter*.018));
    float coverage=saturate(.5-sd);
    // Transmission loss on the unlit side is paired with the white reflection.
    // It follows this same cap/normal, vanishes at the center, and is not a
    // full-perimeter stroke. In this entry only, the legacy blur slot carries
    // the conservative opacity ceiling; no blur or background input is used.
    float unlit=saturate(dot(outward,normalize(float2(.55,.83))));
    float lip=exp(-pow((depth-1.5*pixelScale)/(1.4*pixelScale),2))*unlit;
    float maxPrimaryAbsorption=clamp(blurRadius,0,.075);
    float absorption=min(maxPrimaryAbsorption,.05*lip+.01*shoulder*unlit);
    float4 result=float4(shine.xxx,1-(1-shine)*(1-absorption));
    // The same three lens descriptors drive UVs and light. No extra texture,
    // source visual or mesh: one premultiplied app-generated light pass.
    [loop]for(int j=0;j<(int)refraction;j++){
        float4 lens=lenses[j],style=lensStyles[j];
        float d=LocalDistance(p,lens,style.x);
        if(d>5*pixelScale)continue;
        float a=style.y,activity=style.z;
        // Let one projected shadow crossfade through the cap's existing 0.9 px feather.
        if(d>-.45){
            // Project down-right from the upper-left key light; preserve the unshifted cap gate.
            float2 shadowPoint=p-float2(.6,1.2)*pixelScale;
            float shadowDistance=max(0.0,LocalDistance(shadowPoint,lens,style.x));
            float shadow=exp(-shadowDistance/(2*pixelScale))*exp(-shadowDistance/(3*pixelScale))
                *.065*a*smoothstep(-.45,.45,d);
            result=Over(float4(0,0,0,shadow),result);
        }
        if(d>.45)continue;
        float2 q=abs(p-lens.xy)-lens.zw*.5+style.x;
        float2 n=length(max(q,0))>.001?normalize(max(q,0))*sign(p-lens.xy)
            :q.x>q.y?float2(sign(p.x-lens.x),0):float2(0,sign(p.y-lens.y));
        float localDepth=max(0,-d),localBand=max(3*pixelScale,min(lens.z,lens.w)*.42);
        float v=1-saturate(localDepth/localBand);
        float3 capNormal=normalize(float3(n*(.46*v/sqrt(max(1-v*v,.003))),1));
        float k=pow(saturate(dot(n,normalize(float2(-.55,-.83)))),3);
        float c=pow(saturate(dot(n,normalize(float2(.65,.76)))),5);
        // One continuous, IOR-matched cap reflection. Its Fresnel and compact
        // specular response share the SDF-derived normal; no inset contour,
        // fixed-depth rim, or crown wash is layered over the control.
        const float f0Ratio=(1.45-1.0)/(1.45+1.0);
        float f0=f0Ratio*f0Ratio;
        float fresnel=f0+(1-f0)*pow(1-saturate(capNormal.z),5);
        float3 light=normalize(float3(-.48,-.64,1));
        float3 halfVector=normalize(light+float3(0,0,1));
        float specular=pow(saturate(dot(capNormal,halfVector)),24);
        float directional=.24*k+.045*c;
        // GlassDesk calibration, not Apple's parameter set: preserve the
        // dielectric IOR baseline; direction only weights the Fresnel rise and highlight.
        float interaction=smoothstep(.3,1,activity);
        float angularReflection=(fresnel-f0)*directional+specular*.075;
        float white=saturate(f0+angularReflection*(1+.28*activity+.12*interaction));
        float4 local=float4(white.xxx,white);
        // Same physical .45 px feather as the mesh's secondary UV coverage.
        float feather=1-smoothstep(-.45,.45,d);
        result=Over(local*(a*feather),result);
    }
    return result*coverage;
}
float4 Glass(Varying i) : SV_TARGET {
    float2 p=i.uv*panelSize;
    Profile s=Shape(p);
    float scale=max(pixelScale,0.5);
    float strength=saturate(refraction);
    float bend=s.lens*s.lens*clamp(radius*0.58,9.0*scale,22.0*scale)*strength;
    float2 local=p-panelSize*0.5;
    float2 refracted=panelSize*0.5+local/(1.0+0.09*s.lens*strength)-s.normal*bend;
    float2 uv=(panelOrigin+refracted)/textureSize;
    float3 frost=Blur(uv,float2(0,1)).rgb;
    float chroma=1.15*scale*pow(s.lens,3.0)*strength;
    float2 split=s.normal*chroma/textureSize;
    float3 clear=float3(unfiltered.SampleLevel(linearClamp,uv+split,0).r,
        unfiltered.SampleLevel(linearClamp,uv,0).g,
        unfiltered.SampleLevel(linearClamp,uv-split,0).b);
    float3 color=lerp(frost,clear,(0.03+0.91*s.lens)*strength);
    // Tint and optical light are separate shared layers, applied once.
    return float4(saturate(color),1);
}
)HLSL";
struct Parameters {float textureSize[2],panelSize[2],panelOrigin[2],radius,blur,pixelScale,refraction,padding[2];float lenses[3][4]{},lensStyles[3][4]{};};
static_assert(sizeof(Parameters)==144);
struct Texture
{
    com_ptr<ID3D11Texture2D> value;
    com_ptr<ID3D11ShaderResourceView> source;
    com_ptr<ID3D11RenderTargetView> target;
    unsigned width{},height{};
    void Reset(){value=nullptr;source=nullptr;target=nullptr;width=height=0;}
};
}

struct LiquidGlass::Impl
{
    com_ptr<ID3D11Device> device;
    com_ptr<ID3D11DeviceContext> context;
    CompositionGraphicsDevice graphics{nullptr};
    com_ptr<ID3D11VertexShader> vertex;
    com_ptr<ID3D11PixelShader> prefilterShader,horizontal,glass,optics;
    com_ptr<ID3D11SamplerState> sampler;
    com_ptr<ID3D11Buffer> parameters;
    Texture crop,prefilter,scratch,output,lighting;
    float lightingRadius{-1},lightingScale{-1},lightingTension{-2};
    unsigned lightingWidth{},lightingHeight{};
    CompositionDrawingSurface lightingSurface{nullptr};
    bool closed{},legacyLightMode{};HRESULT closeResult{S_OK};

    Impl(ID3D11Device* deviceValue,ID3D11DeviceContext* contextValue,CompositionGraphicsDevice const& graphicsValue)
        :graphics(graphicsValue)
    {device.copy_from(deviceValue);context.copy_from(contextValue);}
    ~Impl()
    {
        Close();
    }
    HRESULT Close() noexcept {
        if(closed)return closeResult;closed=true;
        try{if(lightingSurface)lightingSurface.Close();}catch(...){closeResult=to_hresult();}
        lightingSurface=nullptr;
        if(context){context->ClearState();context->Flush();}
        return closeResult;
    }



    void CreateShaders()
    {
        if(vertex&&prefilterShader&&horizontal&&glass&&optics&&parameters&&sampler)return;
        vertex=nullptr;prefilterShader=nullptr;horizontal=nullptr;glass=nullptr;optics=nullptr;parameters=nullptr;sampler=nullptr;
        auto compile=[](char const* entry,char const* profile)
        {
            com_ptr<ID3DBlob> code,error;
            check_hresult(D3DCompile(shader,sizeof(shader)-1,"GlassDesk.LiquidGlass",nullptr,nullptr,entry,profile,D3DCOMPILE_OPTIMIZATION_LEVEL3,0,code.put(),error.put()));
            return code;
        };
        auto vs=compile("VS","vs_4_0"),ps=compile("Prefilter","ps_4_0"),hs=compile("Horizontal","ps_4_0"),gs=compile("Glass","ps_4_0"),os=compile("Optics","ps_4_0");
        check_hresult(device->CreateVertexShader(vs->GetBufferPointer(),vs->GetBufferSize(),nullptr,vertex.put()));
        check_hresult(device->CreatePixelShader(ps->GetBufferPointer(),ps->GetBufferSize(),nullptr,prefilterShader.put()));
        check_hresult(device->CreatePixelShader(hs->GetBufferPointer(),hs->GetBufferSize(),nullptr,horizontal.put()));
        check_hresult(device->CreatePixelShader(gs->GetBufferPointer(),gs->GetBufferSize(),nullptr,glass.put()));
        check_hresult(device->CreatePixelShader(os->GetBufferPointer(),os->GetBufferSize(),nullptr,optics.put()));
        D3D11_SAMPLER_DESC sampling{};sampling.Filter=D3D11_FILTER_MIN_MAG_MIP_LINEAR;
        sampling.AddressU=sampling.AddressV=sampling.AddressW=D3D11_TEXTURE_ADDRESS_CLAMP;sampling.MaxLOD=D3D11_FLOAT32_MAX;
        check_hresult(device->CreateSamplerState(&sampling,sampler.put()));
        D3D11_BUFFER_DESC constants{};constants.ByteWidth=sizeof(Parameters);constants.Usage=D3D11_USAGE_DEFAULT;constants.BindFlags=D3D11_BIND_CONSTANT_BUFFER;
        check_hresult(device->CreateBuffer(&constants,nullptr,parameters.put()));
    }
    void Ensure(Texture& texture,unsigned width,unsigned height)
    {
        if(texture.value&&texture.width==width&&texture.height==height)return;
        texture.Reset();D3D11_TEXTURE2D_DESC desc{};desc.Width=width;desc.Height=height;desc.MipLevels=desc.ArraySize=1;
        desc.Format=DXGI_FORMAT_B8G8R8A8_UNORM;desc.SampleDesc.Count=1;desc.Usage=D3D11_USAGE_DEFAULT;
        desc.BindFlags=D3D11_BIND_SHADER_RESOURCE|D3D11_BIND_RENDER_TARGET;
        check_hresult(device->CreateTexture2D(&desc,nullptr,texture.value.put()));
        check_hresult(device->CreateShaderResourceView(texture.value.get(),nullptr,texture.source.put()));
        check_hresult(device->CreateRenderTargetView(texture.value.get(),nullptr,texture.target.put()));texture.width=width;texture.height=height;
    }
    void Pass(Texture const& source,Texture const& target,ID3D11PixelShader* pixel,Parameters const& constants,ID3D11ShaderResourceView* raw=nullptr)
    {
        context->UpdateSubresource(parameters.get(),0,nullptr,&constants,0,0);
        ID3D11ShaderResourceView* input[]{source.source.get(),raw};auto render=target.target.get();auto buffer=parameters.get();auto sample=sampler.get();
        context->OMSetRenderTargets(1,&render,nullptr);context->PSSetShaderResources(0,2,input);
        context->PSSetConstantBuffers(0,1,&buffer);context->PSSetSamplers(0,1,&sample);
        D3D11_VIEWPORT viewport{0,0,float(target.width),float(target.height),0,1};context->RSSetViewports(1,&viewport);
        context->IASetInputLayout(nullptr);context->IASetPrimitiveTopology(D3D11_PRIMITIVE_TOPOLOGY_TRIANGLELIST);
        context->VSSetShader(vertex.get(),nullptr,0);context->PSSetShader(pixel,nullptr,0);
        context->OMSetBlendState(nullptr,nullptr,0xffffffff);context->OMSetDepthStencilState(nullptr,0);context->RSSetState(nullptr);
        context->Draw(3,0);input[0]=input[1]=nullptr;render=nullptr;
        context->PSSetShaderResources(0,2,input);context->OMSetRenderTargets(1,&render,nullptr);
    }
    void PresentTexture(Texture const& rendered,CompositionDrawingSurface& destinationSurface)
    {
        if(!destinationSurface||destinationSurface.Size().Width!=float(rendered.width)||destinationSurface.Size().Height!=float(rendered.height))
        {
            if(destinationSurface)destinationSurface.Close();
            destinationSurface=graphics.CreateDrawingSurface({float(rendered.width),float(rendered.height)},DirectXPixelFormat::B8G8R8A8UIntNormalized,DirectXAlphaMode::Premultiplied);
        }
        auto interop=destinationSurface.as<ABI::Windows::UI::Composition::ICompositionDrawingSurfaceInterop>();
        com_ptr<IDXGISurface> destination;POINT offset{};
        check_hresult(interop->BeginDraw(nullptr,__uuidof(IDXGISurface),destination.put_void(),&offset));
        try
        {
            auto texture=destination.as<ID3D11Texture2D>();
            D3D11_TEXTURE2D_DESC target{};texture->GetDesc(&target);
            if(offset.x<0||offset.y<0||uint64_t(offset.x)+rendered.width>target.Width
                ||uint64_t(offset.y)+rendered.height>target.Height)throw hresult_error(E_BOUNDS);
            context->CopySubresourceRegion(texture.get(),0,static_cast<UINT>(offset.x),static_cast<UINT>(offset.y),0,rendered.value.get(),0,nullptr);
        }
        catch(...){interop->EndDraw();throw;}
        check_hresult(interop->EndDraw());
    }
    CompositionDrawingSurface Light(unsigned width,unsigned height,float radiusValue,float scale,float strain=0)
    {
        legacyLightMode=true;
        if(!width||!height||width>4096||height>4096||uint64_t(width)*height>1'000'000
            ||!std::isfinite(radiusValue)||!std::isfinite(scale)||!std::isfinite(strain)
            ||scale<=0||radiusValue<0)return nullptr;
        if(lightingSurface&&width==lightingWidth&&height==lightingHeight&&radiusValue==lightingRadius&&scale==lightingScale&&strain==lightingTension)return lightingSurface;
        CreateShaders();Ensure(lighting,width,height);
        Parameters constants{{float(width),float(height)},{float(width),float(height)},{0,0},radiusValue,0,scale,0,{}};
        constants.padding[0]=strain;
        Texture empty;Pass(empty,lighting,optics.get(),constants);PresentTexture(lighting,lightingSurface);
        lightingWidth=width;lightingHeight=height;lightingRadius=radiusValue;lightingScale=scale;lightingTension=strain;
        return lightingSurface;
    }

};

LiquidGlass::LiquidGlass(HWND,HWND,ID3D11Device* device,ID3D11DeviceContext* context,CompositionGraphicsDevice const& graphics)
    :impl(std::make_unique<Impl>(device,context,graphics)){}
LiquidGlass::~LiquidGlass()=default;
HRESULT LiquidGlass::Close()noexcept{return impl->Close();}

void LiquidGlass::SetEnabled(bool)noexcept{}
void LiquidGlass::Suspend(bool,bool)noexcept{}
void LiquidGlass::Tick(RECT,float,float)noexcept{}
HRESULT LiquidGlass::SaveReferencePreview(wchar_t const* path,unsigned width,unsigned height,float radius,float scale,unsigned theme,float tension)noexcept
{
    // CPU pixel readback is prohibited, including for generated preview fixtures.
    (void)path;(void)width;(void)height;(void)radius;(void)scale;(void)theme;(void)tension;
    return E_NOTIMPL;
}
CompositionDrawingSurface LiquidGlass::OpticalSurface(unsigned width,unsigned height,float radius,float scale,float tension)noexcept
{try{return impl->Light(width,height,radius,scale,tension);}catch(...){return nullptr;}}
bool LiquidGlass::Enabled()const noexcept{return false;}
bool LiquidGlass::Ready()const noexcept{return false;}
bool LiquidGlass::MatchesFrame(RECT,float,float)const noexcept{return false;}
std::wstring LiquidGlass::Status()const{return L"Disabled: screen capture removed; procedural highlight only";}
LiquidGlassStats LiquidGlass::Stats()const noexcept{return {};}
CompositionDrawingSurface LiquidGlass::Surface()const{return nullptr;}
HRESULT LiquidGlass::SaveDiagnostic(wchar_t const*)noexcept{return E_NOTIMPL;}
HRESULT LiquidGlass::BeginDiagnosticSnapshot(unsigned)noexcept{return E_NOTIMPL;}
HRESULT LiquidGlass::EndDiagnosticSnapshot()noexcept{return E_NOTIMPL;}
std::wstring LiquidGlass::DiagnosticSnapshotStatus()const{return L"Disabled: screenshot snapshot support was removed with desktop capture";}
}

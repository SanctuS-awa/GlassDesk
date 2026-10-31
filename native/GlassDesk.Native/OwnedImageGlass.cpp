// App-owned wallpaper material renderer. This file consumes only a caller-
// supplied local BGRA image and the host's existing D3D11/D2D devices.
#ifndef WIN32_LEAN_AND_MEAN
#define WIN32_LEAN_AND_MEAN
#endif
#ifndef NOMINMAX
#define NOMINMAX
#endif
#include "OwnedImageGlass.h"

#include <dxgi1_2.h>
#include <windows.h>

#include <algorithm>
#include <array>
#include <cmath>
#include <cstring>
#include <limits>
#include <vector>

#include "OwnedImageGlassVS.h"
#include "OwnedImageGlassPS.h"
#include "OwnedImageGlassBlurPS.h"

namespace gd
{
namespace
{
constexpr std::uint32_t kMaxImageEdge = 1600;
constexpr std::uint64_t kMaxCachedImagePixels = 1'500'000;
constexpr std::uint64_t kMaxInputPixels = 64'000'000;
constexpr std::uint64_t kMaxOutputPixels = 1'000'000;
constexpr std::uint64_t kMaxGpuBytes = 32ull * 1024ull * 1024ull;
constexpr std::uint64_t kPresenterReserveBytes = 4ull * 1024ull * 1024ull;
constexpr UINT kMaxClassInstances = 256;

struct alignas(16) Float4
{
    float x{};
    float y{};
    float z{};
    float w{};
};

struct alignas(16) FrameConstants
{
    Float4 dimensions;
    Float4 body;
    Float4 shape;
    Float4 material;
    Float4 wallpaper;
    Float4 window;
    Float4 flags;
    Float4 backgroundColor;
    Float4 lensRect[3];
    Float4 lensStyle[3];
    Float4 blurParams;
};
static_assert(sizeof(FrameConstants) == 240);
static_assert(sizeof(FrameConstants) % 16 == 0);

bool Finite(float value) noexcept
{
    return std::isfinite(value);
}

void Require(bool condition)
{
    if (!condition)
        winrt::check_hresult(E_INVALIDARG);
}

float Clamp01(float value) noexcept
{
    return std::clamp(value, 0.0f, 1.0f);
}

std::uint64_t MipChainBytes(std::uint32_t width, std::uint32_t height) noexcept
{
    std::uint64_t bytes = 0;
    for (;;)
    {
        bytes += static_cast<std::uint64_t>(width) * height * 4;
        if (width == 1 && height == 1)
            return bytes;
        width = std::max(1u, width / 2);
        height = std::max(1u, height / 2);
    }
}

void ReleaseClassInstances(ID3D11ClassInstance** instances, UINT count) noexcept
{
    for (UINT i = 0; i < count; ++i)
    {
        if (instances[i])
        {
            instances[i]->Release();
            instances[i] = nullptr;
        }
    }
}

template<typename T>
void ReleaseOne(T*& value) noexcept
{
    if (value)
    {
        value->Release();
        value = nullptr;
    }
}

winrt::com_ptr<IUnknown> ComIdentity(IUnknown* value)
{
    winrt::com_ptr<IUnknown> identity;
    winrt::check_hresult(value->QueryInterface(__uuidof(IUnknown), identity.put_void()));
    return identity;
}

// Save the immediate-context state touched by the fullscreen material draw.
// The renderer shares the host device and must leave its graphics work intact.
class ContextState final
{
public:
    explicit ContextState(ID3D11DeviceContext* context) : context_(context)
    {
        context_->OMGetRenderTargets(D3D11_SIMULTANEOUS_RENDER_TARGET_COUNT, renderTargets_, &depthTarget_);
        context_->OMGetBlendState(&blendState_, blendFactor_, &sampleMask_);
        context_->OMGetDepthStencilState(&depthState_, &stencilRef_);
        context_->RSGetState(&rasterizerState_);

        viewportCount_ = D3D11_VIEWPORT_AND_SCISSORRECT_OBJECT_COUNT_PER_PIPELINE;
        context_->RSGetViewports(&viewportCount_, viewports_);
        scissorCount_ = D3D11_VIEWPORT_AND_SCISSORRECT_OBJECT_COUNT_PER_PIPELINE;
        context_->RSGetScissorRects(&scissorCount_, scissors_);

        context_->IAGetInputLayout(&inputLayout_);
        context_->IAGetPrimitiveTopology(&topology_);

        vertexClassCount_ = kMaxClassInstances;
        context_->VSGetShader(&vertexShader_, vertexClasses_, &vertexClassCount_);
        geometryClassCount_ = kMaxClassInstances;
        context_->GSGetShader(&geometryShader_, geometryClasses_, &geometryClassCount_);
        hullClassCount_ = kMaxClassInstances;
        context_->HSGetShader(&hullShader_, hullClasses_, &hullClassCount_);
        domainClassCount_ = kMaxClassInstances;
        context_->DSGetShader(&domainShader_, domainClasses_, &domainClassCount_);
        pixelClassCount_ = kMaxClassInstances;
        context_->PSGetShader(&pixelShader_, pixelClasses_, &pixelClassCount_);

        context_->PSGetConstantBuffers(0, 1, &pixelConstantBuffer_);
        context_->PSGetShaderResources(0, 2, pixelResources_);
        context_->PSGetSamplers(0, 1, &pixelSampler_);
        context_->GetPredication(&predicate_, &predicateValue_);
    }

    ~ContextState()
    {
        Restore();
        Cleanup();
    }

    ContextState(ContextState const&) = delete;
    ContextState& operator=(ContextState const&) = delete;

private:
    void Restore() noexcept
    {
        if (!context_)
            return;

        context_->OMSetRenderTargets(D3D11_SIMULTANEOUS_RENDER_TARGET_COUNT, renderTargets_, depthTarget_);
        context_->OMSetBlendState(blendState_, blendFactor_, sampleMask_);
        context_->OMSetDepthStencilState(depthState_, stencilRef_);
        context_->RSSetState(rasterizerState_);
        context_->RSSetViewports(viewportCount_, viewports_);
        context_->RSSetScissorRects(scissorCount_, scissors_);

        context_->IASetInputLayout(inputLayout_);
        context_->IASetPrimitiveTopology(topology_);

        context_->VSSetShader(vertexShader_, vertexClasses_, vertexClassCount_);
        context_->GSSetShader(geometryShader_, geometryClasses_, geometryClassCount_);
        context_->HSSetShader(hullShader_, hullClasses_, hullClassCount_);
        context_->DSSetShader(domainShader_, domainClasses_, domainClassCount_);
        context_->PSSetShader(pixelShader_, pixelClasses_, pixelClassCount_);

        context_->PSSetConstantBuffers(0, 1, &pixelConstantBuffer_);
        context_->PSSetShaderResources(0, 2, pixelResources_);
        context_->PSSetSamplers(0, 1, &pixelSampler_);
        context_->SetPredication(predicate_, predicateValue_);
        context_ = nullptr;
    }

    void Cleanup() noexcept
    {
        for (auto*& target : renderTargets_)
            ReleaseOne(target);
        ReleaseOne(depthTarget_);
        ReleaseOne(blendState_);
        ReleaseOne(depthState_);
        ReleaseOne(rasterizerState_);
        ReleaseOne(inputLayout_);
        ReleaseOne(vertexShader_);
        ReleaseOne(geometryShader_);
        ReleaseOne(hullShader_);
        ReleaseOne(domainShader_);
        ReleaseOne(pixelShader_);
        ReleaseOne(pixelConstantBuffer_);
        for (auto*& resource : pixelResources_)
            ReleaseOne(resource);
        ReleaseOne(pixelSampler_);
        ReleaseOne(predicate_);
        ReleaseClassInstances(vertexClasses_, vertexClassCount_);
        ReleaseClassInstances(geometryClasses_, geometryClassCount_);
        ReleaseClassInstances(hullClasses_, hullClassCount_);
        ReleaseClassInstances(domainClasses_, domainClassCount_);
        ReleaseClassInstances(pixelClasses_, pixelClassCount_);
    }

    ID3D11DeviceContext* context_{};
    ID3D11RenderTargetView* renderTargets_[D3D11_SIMULTANEOUS_RENDER_TARGET_COUNT]{};
    ID3D11DepthStencilView* depthTarget_{};
    ID3D11BlendState* blendState_{};
    FLOAT blendFactor_[4]{};
    UINT sampleMask_{};
    ID3D11DepthStencilState* depthState_{};
    UINT stencilRef_{};
    ID3D11RasterizerState* rasterizerState_{};
    D3D11_VIEWPORT viewports_[D3D11_VIEWPORT_AND_SCISSORRECT_OBJECT_COUNT_PER_PIPELINE]{};
    UINT viewportCount_{};
    D3D11_RECT scissors_[D3D11_VIEWPORT_AND_SCISSORRECT_OBJECT_COUNT_PER_PIPELINE]{};
    UINT scissorCount_{};
    ID3D11InputLayout* inputLayout_{};
    D3D11_PRIMITIVE_TOPOLOGY topology_{D3D11_PRIMITIVE_TOPOLOGY_UNDEFINED};

    ID3D11VertexShader* vertexShader_{};
    ID3D11ClassInstance* vertexClasses_[kMaxClassInstances]{};
    UINT vertexClassCount_{};
    ID3D11GeometryShader* geometryShader_{};
    ID3D11ClassInstance* geometryClasses_[kMaxClassInstances]{};
    UINT geometryClassCount_{};
    ID3D11HullShader* hullShader_{};
    ID3D11ClassInstance* hullClasses_[kMaxClassInstances]{};
    UINT hullClassCount_{};
    ID3D11DomainShader* domainShader_{};
    ID3D11ClassInstance* domainClasses_[kMaxClassInstances]{};
    UINT domainClassCount_{};
    ID3D11PixelShader* pixelShader_{};
    ID3D11ClassInstance* pixelClasses_[kMaxClassInstances]{};
    UINT pixelClassCount_{};

    ID3D11Buffer* pixelConstantBuffer_{};
    ID3D11ShaderResourceView* pixelResources_[2]{};
    ID3D11SamplerState* pixelSampler_{};
    ID3D11Predicate* predicate_{};
    BOOL predicateValue_{};
};

struct Bgra
{
    float b{};
    float g{};
    float r{};
    float a{};
};

Bgra ReadPixel(
    std::uint8_t const* pixels,
    std::uint32_t width,
    std::uint32_t x,
    std::uint32_t y,
    OwnedImageAlphaMode alphaMode) noexcept
{
    auto const* pixel = pixels + (static_cast<std::size_t>(y) * width + x) * 4;
    float alpha = static_cast<float>(pixel[3]) / 255.0f;
    Bgra result{
        static_cast<float>(pixel[0]),
        static_cast<float>(pixel[1]),
        static_cast<float>(pixel[2]),
        static_cast<float>(pixel[3])};
    if (alphaMode == OwnedImageAlphaMode::Straight)
    {
        result.b *= alpha;
        result.g *= alpha;
        result.r *= alpha;
    }
    return result;
}

std::vector<std::uint8_t> ResampleImage(
    std::uint8_t const* pixels,
    std::uint32_t sourceWidth,
    std::uint32_t sourceHeight,
    std::uint32_t targetWidth,
    std::uint32_t targetHeight,
    OwnedImageAlphaMode alphaMode)
{
    std::vector<std::uint8_t> output(static_cast<std::size_t>(targetWidth) * targetHeight * 4);

    for (std::uint32_t y = 0; y < targetHeight; ++y)
    {
        double const top = static_cast<double>(y) * sourceHeight / targetHeight;
        double const bottom = static_cast<double>(y + 1) * sourceHeight / targetHeight;
        auto const firstY = static_cast<std::uint32_t>(std::floor(top));
        auto const lastY = static_cast<std::uint32_t>(std::ceil(bottom));

        for (std::uint32_t x = 0; x < targetWidth; ++x)
        {
            double const left = static_cast<double>(x) * sourceWidth / targetWidth;
            double const right = static_cast<double>(x + 1) * sourceWidth / targetWidth;
            auto const firstX = static_cast<std::uint32_t>(std::floor(left));
            auto const lastX = static_cast<std::uint32_t>(std::ceil(right));

            double totalWeight = 0;
            double sumB = 0, sumG = 0, sumR = 0, sumA = 0;
            for (std::uint32_t sy = firstY; sy < lastY; ++sy)
            {
                double const weightY = std::max(0.0, std::min(bottom, static_cast<double>(sy + 1)) - std::max(top, static_cast<double>(sy)));
                for (std::uint32_t sx = firstX; sx < lastX; ++sx)
                {
                    double const weightX = std::max(0.0, std::min(right, static_cast<double>(sx + 1)) - std::max(left, static_cast<double>(sx)));
                    double const weight = weightX * weightY;
                    Bgra const pixel = ReadPixel(pixels, sourceWidth, sx, sy, alphaMode);
                    sumB += pixel.b * weight;
                    sumG += pixel.g * weight;
                    sumR += pixel.r * weight;
                    sumA += pixel.a * weight;
                    totalWeight += weight;
                }
            }

            auto* destination = output.data() + (static_cast<std::size_t>(y) * targetWidth + x) * 4;
            double const inverseWeight = totalWeight > 0 ? 1.0 / totalWeight : 0;
            destination[0] = static_cast<std::uint8_t>(std::clamp(std::lround(sumB * inverseWeight), 0l, 255l));
            destination[1] = static_cast<std::uint8_t>(std::clamp(std::lround(sumG * inverseWeight), 0l, 255l));
            destination[2] = static_cast<std::uint8_t>(std::clamp(std::lround(sumR * inverseWeight), 0l, 255l));
            destination[3] = static_cast<std::uint8_t>(std::clamp(std::lround(sumA * inverseWeight), 0l, 255l));
        }
    }
    return output;
}

void ValidateFrame(OwnedImageGlassFrame const& frame)
{
    Require(frame.widthPx > 0 && frame.heightPx > 0);
    Require(static_cast<std::uint64_t>(frame.widthPx) * frame.heightPx <= kMaxOutputPixels);
    Require(frame.dpiScaleX >= 0.5f && frame.dpiScaleX <= 4.0f);
    Require(frame.dpiScaleY >= 0.5f && frame.dpiScaleY <= 4.0f);
    Require(Finite(frame.windowOriginPxX) && Finite(frame.windowOriginPxY));
    Require(Finite(frame.imageOriginPxX) && Finite(frame.imageOriginPxY));
    Require(Finite(frame.imageDisplayWidthPx) && frame.imageDisplayWidthPx >= 1);
    Require(Finite(frame.imageDisplayHeightPx) && frame.imageDisplayHeightPx >= 1);

    auto const& body = frame.bodyBoundsPx;
    Require(Finite(body.x) && Finite(body.y) && Finite(body.width) && Finite(body.height));
    Require(body.x >= 0 && body.y >= 0 && body.width > 0 && body.height > 0);
    Require(body.x + body.width <= static_cast<float>(frame.widthPx) + 1);
    Require(body.y + body.height <= static_cast<float>(frame.heightPx) + 1);
    Require(Finite(frame.radiusDip) && frame.radiusDip >= 0 && frame.radiusDip <= 256);
    Require(Finite(frame.surfaceTension) && std::abs(frame.surfaceTension) <= 8);
    Require(Finite(frame.baseZoom) && frame.baseZoom >= 0 && frame.baseZoom <= 0.3f);
    Require(Finite(frame.bend) && frame.bend >= 0 && frame.bend <= 2);
    Require(Finite(frame.softnessDip) && frame.softnessDip >= 0 && frame.softnessDip <= 0.35f);
    Require(Finite(frame.tint) && frame.tint >= 0 && frame.tint <= 0.5f);
    Require(Finite(frame.lightIntensity) && frame.lightIntensity >= 0 && frame.lightIntensity <= 2);
    Require(Finite(frame.backgroundRed) && Finite(frame.backgroundGreen) && Finite(frame.backgroundBlue));
    Require(frame.lensCount <= frame.lenses.size());

    for (std::uint32_t i = 0; i < frame.lensCount; ++i)
    {
        auto const& lens = frame.lenses[i];
        auto const& bounds = lens.boundsPx;
        Require(Finite(bounds.x) && Finite(bounds.y) && Finite(bounds.width) && Finite(bounds.height));
        Require(bounds.width > 0 && bounds.height > 0);
        Require(Finite(lens.radiusDip) && lens.radiusDip >= 0 && lens.radiusDip <= 128);
        Require(Finite(lens.opacity) && lens.opacity >= 0 && lens.opacity <= 1);
        Require(Finite(lens.lightTone) && lens.lightTone >= 0 && lens.lightTone <= 1);
        Require(Finite(lens.emphasis) && lens.emphasis >= 0 && lens.emphasis <= 2);
    }
}

} // namespace

struct OwnedImageGlass::Impl
{
    explicit Impl(ID3D11Device* existingDevice)
        : ownerThread(GetCurrentThreadId())
    {
        Require(existingDevice != nullptr);
        existingDevice->AddRef();
        device.attach(existingDevice);
        device->GetImmediateContext(context.put());
        Require(context.get() != nullptr);


        D3D11_BUFFER_DESC bufferDesc{};
        bufferDesc.ByteWidth = static_cast<UINT>(sizeof(FrameConstants));
        bufferDesc.Usage = D3D11_USAGE_DYNAMIC;
        bufferDesc.BindFlags = D3D11_BIND_CONSTANT_BUFFER;
        bufferDesc.CPUAccessFlags = D3D11_CPU_ACCESS_WRITE;
        winrt::check_hresult(device->CreateBuffer(&bufferDesc, nullptr, constants.put()));

        D3D11_SAMPLER_DESC samplerDesc{};
        samplerDesc.Filter = D3D11_FILTER_MIN_MAG_MIP_LINEAR;
        samplerDesc.AddressU = D3D11_TEXTURE_ADDRESS_CLAMP;
        samplerDesc.AddressV = D3D11_TEXTURE_ADDRESS_CLAMP;
        samplerDesc.AddressW = D3D11_TEXTURE_ADDRESS_CLAMP;
        samplerDesc.MaxLOD = D3D11_FLOAT32_MAX;
        winrt::check_hresult(device->CreateSamplerState(&samplerDesc, sampler.put()));

        D3D11_RASTERIZER_DESC rasterizerDesc{};
        rasterizerDesc.FillMode = D3D11_FILL_SOLID;
        rasterizerDesc.CullMode = D3D11_CULL_NONE;
        rasterizerDesc.DepthClipEnable = TRUE;
        rasterizerDesc.ScissorEnable = FALSE;
        winrt::check_hresult(device->CreateRasterizerState(&rasterizerDesc, rasterizer.put()));

        winrt::check_hresult(device->CreateVertexShader(
            OwnedImageGlassVertexShader,
            sizeof(OwnedImageGlassVertexShader),
            nullptr,
            vertexShader.put()));
        winrt::check_hresult(device->CreatePixelShader(
            OwnedImageGlassPixelShader,
            sizeof(OwnedImageGlassPixelShader),
            nullptr,
            pixelShader.put()));
        winrt::check_hresult(device->CreatePixelShader(
            OwnedImageGlassBlurPixelShader,
            sizeof(OwnedImageGlassBlurPixelShader),
            nullptr,
            blurPixelShader.put()));
    }

    void CheckThread() const
    {
        if (ownerThread != GetCurrentThreadId())
            throw winrt::hresult_wrong_thread();
    }

    void SetImage(
        std::uint8_t const* bgra,
        std::uint32_t width,
        std::uint32_t height,
        std::size_t bytes,
        OwnedImageAlphaMode alphaMode)
    {
        CheckThread();
        cachedSigmaX = cachedSigmaY = -1;
        if (!bgra)
        {
            Require(width == 0 && height == 0 && bytes == 0);
            imageView = nullptr;
            imageTexture = nullptr;
            imageWidth = imageHeight = 0;
            blurImages = {};
            blurTargets = {};
            blurWidth = blurHeight = 0;
            cachedSigmaX = cachedSigmaY = -1;
            alphaModeStored = OwnedImageAlphaMode::Opaque;
            return;
        }

        Require(width > 0 && height > 0);
        Require(width <= 16384 && height <= 16384);
        std::uint64_t const pixels = static_cast<std::uint64_t>(width) * height;
        Require(pixels <= kMaxInputPixels);
        Require(pixels * 4 == bytes);
        Require(alphaMode == OwnedImageAlphaMode::Opaque
            || alphaMode == OwnedImageAlphaMode::Straight
            || alphaMode == OwnedImageAlphaMode::Premultiplied);

        double const edgeScale = static_cast<double>(kMaxImageEdge) / std::max(width, height);
        double const areaScale = std::sqrt(static_cast<double>(kMaxCachedImagePixels) / pixels);
        double const scale = std::min(1.0, std::min(edgeScale, areaScale));
        std::uint32_t targetWidth = std::max(1u, static_cast<std::uint32_t>(std::lround(width * scale)));
        std::uint32_t targetHeight = std::max(1u, static_cast<std::uint32_t>(std::lround(height * scale)));
        while (static_cast<std::uint64_t>(targetWidth) * targetHeight > kMaxCachedImagePixels)
        {
            if (targetWidth >= targetHeight)
                --targetWidth;
            else
                --targetHeight;
        }
        bool const needsResample = targetWidth != width || targetHeight != height;
        bool const needsPremultiply = alphaMode == OwnedImageAlphaMode::Straight;

        std::vector<std::uint8_t> converted;
        std::uint8_t const* uploadPixels = bgra;
        if (needsResample)
        {
            converted = ResampleImage(bgra, width, height, targetWidth, targetHeight, alphaMode);
            uploadPixels = converted.data();
        }
        else if (needsPremultiply)
        {
            converted.assign(bgra, bgra + bytes);
            for (std::size_t offset = 0; offset < converted.size(); offset += 4)
            {
                float const alpha = static_cast<float>(converted[offset + 3]) / 255.0f;
                converted[offset + 0] = static_cast<std::uint8_t>(std::lround(converted[offset + 0] * alpha));
                converted[offset + 1] = static_cast<std::uint8_t>(std::lround(converted[offset + 1] * alpha));
                converted[offset + 2] = static_cast<std::uint8_t>(std::lround(converted[offset + 2] * alpha));
            }
            uploadPixels = converted.data();
        }

        OwnedImageAlphaMode const uploadedAlphaMode = needsPremultiply
            ? OwnedImageAlphaMode::Premultiplied
            : alphaMode;
        std::uint64_t const gpuImageBytes = static_cast<std::uint64_t>(targetWidth) * targetHeight * 4;
        Require(targetWidth <= kMaxImageEdge && targetHeight <= kMaxImageEdge);
        Require(static_cast<std::uint64_t>(targetWidth) * targetHeight <= kMaxCachedImagePixels);
        Require(gpuImageBytes < kMaxGpuBytes);

        if (targetWidth != imageWidth || targetHeight != imageHeight || !imageTexture || !imageView)
        {
            imageView = nullptr;
            imageTexture = nullptr;
            blurImages = {};
            blurTargets = {};
            blurWidth = blurHeight = 0;
            cachedSigmaX = cachedSigmaY = -1;
            imageWidth = targetWidth;
            imageHeight = targetHeight;

            D3D11_TEXTURE2D_DESC textureDesc{};
            textureDesc.Width = targetWidth;
            textureDesc.Height = targetHeight;
            textureDesc.MipLevels = 0;
            textureDesc.ArraySize = 1;
            textureDesc.Format = DXGI_FORMAT_B8G8R8A8_UNORM;
            textureDesc.SampleDesc.Count = 1;
            textureDesc.Usage = D3D11_USAGE_DEFAULT;
            textureDesc.BindFlags = D3D11_BIND_SHADER_RESOURCE | D3D11_BIND_RENDER_TARGET;
            textureDesc.MiscFlags = D3D11_RESOURCE_MISC_GENERATE_MIPS;
            winrt::check_hresult(device->CreateTexture2D(&textureDesc, nullptr, imageTexture.put()));
            context->UpdateSubresource(imageTexture.get(), 0, nullptr, uploadPixels, targetWidth * 4, 0);
            winrt::check_hresult(device->CreateShaderResourceView(imageTexture.get(), nullptr, imageView.put()));
            context->GenerateMips(imageView.get());
        }
        else
        {
            context->UpdateSubresource(
                imageTexture.get(),
                0,
                nullptr,
                uploadPixels,
                targetWidth * 4,
                0);
            context->GenerateMips(imageView.get());
        }
        alphaModeStored = uploadedAlphaMode;
    }

    void EnsureBlurTargets(std::uint32_t width, std::uint32_t height)
    {
        if (blurTargets[0] && blurTargets[1] && blurWidth == width && blurHeight == height)
            return;

        blurImages = {};
        blurTargets = {};
        blurWidth = blurHeight = 0;

        D3D11_TEXTURE2D_DESC textureDesc{};
        textureDesc.Width = width;
        textureDesc.Height = height;
        textureDesc.MipLevels = 1;
        textureDesc.ArraySize = 1;
        textureDesc.Format = DXGI_FORMAT_B8G8R8A8_UNORM;
        textureDesc.SampleDesc.Count = 1;
        textureDesc.Usage = D3D11_USAGE_DEFAULT;
        textureDesc.BindFlags = D3D11_BIND_RENDER_TARGET | D3D11_BIND_SHADER_RESOURCE;

        for (std::size_t i = 0; i < blurTargets.size(); ++i)
        {
            winrt::com_ptr<ID3D11Texture2D> texture;
            winrt::check_hresult(device->CreateTexture2D(&textureDesc, nullptr, texture.put()));
            winrt::check_hresult(device->CreateRenderTargetView(texture.get(), nullptr, blurTargets[i].put()));
            winrt::check_hresult(device->CreateShaderResourceView(texture.get(), nullptr, blurImages[i].put()));
        }
        blurWidth = width;
        blurHeight = height;
    }

    void EnsureOutput(
        std::uint32_t width,
        std::uint32_t height,
        std::uint32_t blurOutputWidth,
        std::uint32_t blurOutputHeight)
    {
        std::uint64_t const outputBytes = static_cast<std::uint64_t>(width) * height * 4;
        std::uint64_t const previousOutputBytes =
            outputTexture && (outputWidth != width || outputHeight != height)
            ? static_cast<std::uint64_t>(outputWidth) * outputHeight * 4
            : 0;
        std::uint64_t const blurBytes =
            static_cast<std::uint64_t>(blurOutputWidth) * blurOutputHeight * 8;
        std::uint64_t const ownedBytes =
            MipChainBytes(imageWidth, imageHeight)
            + blurBytes
            + outputBytes
            + previousOutputBytes
            + kPresenterReserveBytes;
        Require(ownedBytes <= kMaxGpuBytes);

        if (outputTexture && outputWidth == width && outputHeight == height)
            return;

        outputBitmap = nullptr;
        outputD2dDevice = nullptr;
        outputD2dIdentity = nullptr;
        outputRtv = nullptr;
        outputTexture = nullptr;
        outputWidth = outputHeight = 0;

        D3D11_TEXTURE2D_DESC textureDesc{};
        textureDesc.Width = width;
        textureDesc.Height = height;
        textureDesc.MipLevels = 1;
        textureDesc.ArraySize = 1;
        textureDesc.Format = DXGI_FORMAT_B8G8R8A8_UNORM;
        textureDesc.SampleDesc.Count = 1;
        textureDesc.Usage = D3D11_USAGE_DEFAULT;
        textureDesc.BindFlags = D3D11_BIND_RENDER_TARGET | D3D11_BIND_SHADER_RESOURCE;
        winrt::check_hresult(device->CreateTexture2D(&textureDesc, nullptr, outputTexture.put()));
        winrt::check_hresult(device->CreateRenderTargetView(outputTexture.get(), nullptr, outputRtv.put()));
        outputWidth = width;
        outputHeight = height;
    }

    void ComputeBlurLayout(
        OwnedImageGlassFrame const& frame,
        float& sigmaX,
        float& sigmaY,
        float& divisor,
        std::uint32_t& width,
        std::uint32_t& height) const
    {
        sigmaX = std::max(
            frame.softnessDip * frame.dpiScaleX * static_cast<float>(imageWidth) / frame.imageDisplayWidthPx,
            0.001f);
        sigmaY = std::max(
            frame.softnessDip * frame.dpiScaleY * static_cast<float>(imageHeight) / frame.imageDisplayHeightPx,
            0.001f);
        divisor = std::max(1.0f, std::max(sigmaX, sigmaY) / 3.0f);
        width = std::max(1u, static_cast<std::uint32_t>(std::ceil(static_cast<float>(imageWidth) / divisor)));
        height = std::max(1u, static_cast<std::uint32_t>(std::ceil(static_cast<float>(imageHeight) / divisor)));
    }

    void UploadConstants(FrameConstants const& value)
    {
        D3D11_MAPPED_SUBRESOURCE mapped{};
        winrt::check_hresult(context->Map(constants.get(), 0, D3D11_MAP_WRITE_DISCARD, 0, &mapped));
        std::memcpy(mapped.pData, &value, sizeof(value));
        context->Unmap(constants.get(), 0);
    }

    void PrepareBlur(
        float sigmaX,
        float sigmaY,
        float divisor)
    {
        if (std::abs(sigmaX - cachedSigmaX) < 0.0001f
            && std::abs(sigmaY - cachedSigmaY) < 0.0001f
            && blurImages[1])
            return;

        FrameConstants blurConstants{};
        blurConstants.window.z = static_cast<float>(alphaModeStored);
        auto constantBuffer = constants.get();
        context->PSSetConstantBuffers(0, 1, &constantBuffer);
        auto samplerState = sampler.get();
        context->PSSetSamplers(0, 1, &samplerState);
        context->VSSetShader(vertexShader.get(), nullptr, 0);
        context->PSSetShader(blurPixelShader.get(), nullptr, 0);
        auto const lod = divisor > 1 ? std::log2(divisor) : 0.0f;

        for (UINT pass = 0; pass < 2; ++pass)
        {
            float const sigma = pass == 0
                ? sigmaX * static_cast<float>(blurWidth) / static_cast<float>(imageWidth)
                : sigmaY * static_cast<float>(blurHeight) / static_cast<float>(imageHeight);
            blurConstants.blurParams = pass == 0
                ? Float4{1.0f / static_cast<float>(blurWidth), 0, sigma, lod}
                : Float4{0, 1.0f / static_cast<float>(blurHeight), sigma, 0};
            UploadConstants(blurConstants);

            auto target = blurTargets[pass].get();
            context->OMSetRenderTargets(1, &target, nullptr);
            D3D11_VIEWPORT viewport{
                0,
                0,
                static_cast<float>(blurWidth),
                static_cast<float>(blurHeight),
                0,
                1};
            context->RSSetViewports(1, &viewport);

            ID3D11ShaderResourceView* sources[2]{
                pass == 0 ? imageView.get() : blurImages[0].get(),
                nullptr};
            context->PSSetShaderResources(0, 2, sources);
            float transparent[4]{};
            context->ClearRenderTargetView(target, transparent);
            context->Draw(3, 0);

            ID3D11ShaderResourceView* noSources[2]{};
            context->PSSetShaderResources(0, 2, noSources);
            context->OMSetRenderTargets(0, nullptr, nullptr);
        }
        cachedSigmaX = sigmaX;
        cachedSigmaY = sigmaY;
    }
    FrameConstants MakeConstants(OwnedImageGlassFrame const& frame) const
    {
        FrameConstants frameData{};
        float const heightDip = frame.bodyBoundsPx.height / frame.dpiScaleY;
        float const progress = Clamp01((heightDip - 52.0f) / 124.0f);

        frameData.dimensions = {
            static_cast<float>(frame.widthPx),
            static_cast<float>(frame.heightPx),
            frame.dpiScaleX,
            frame.dpiScaleY};
        frameData.body = {
            frame.bodyBoundsPx.x,
            frame.bodyBoundsPx.y,
            frame.bodyBoundsPx.width,
            frame.bodyBoundsPx.height};
        frameData.shape = {
            frame.radiusDip,
            frame.surfaceTension,
            progress,
            frame.baseZoom};
        frameData.material = {
            frame.bend,
            frame.softnessDip,
            frame.tint,
            frame.lightIntensity};
        frameData.wallpaper = {
            frame.imageOriginPxX,
            frame.imageOriginPxY,
            frame.imageDisplayWidthPx,
            frame.imageDisplayHeightPx};
        frameData.window = {
            frame.windowOriginPxX,
            frame.windowOriginPxY,
            static_cast<float>(alphaModeStored),
            0};
        frameData.flags = {
            frame.peek ? 1.0f : 0.0f,
            frame.musicControls ? 1.0f : 0.0f,
            static_cast<float>(frame.lensCount),
            0};
        frameData.backgroundColor = {
            frame.backgroundRed,
            frame.backgroundGreen,
            frame.backgroundBlue,
            0};

        for (std::size_t i = 0; i < frame.lenses.size(); ++i)
        {
            auto const& lens = frame.lenses[i];
            frameData.lensRect[i] = {
                lens.boundsPx.x,
                lens.boundsPx.y,
                lens.boundsPx.width,
                lens.boundsPx.height};
            frameData.lensStyle[i] = {
                lens.radiusDip,
                lens.opacity,
                lens.lightTone,
                lens.emphasis};
        }
        return frameData;
    }

    winrt::com_ptr<ID2D1Bitmap1> Render(
        ID2D1DeviceContext* drawingContext,
        OwnedImageGlassFrame const& frame)
    {
        CheckThread();
        Require(drawingContext != nullptr);
        Require(imageView.get() != nullptr && imageTexture.get() != nullptr);
        ValidateFrame(frame);

        float sigmaX{}, sigmaY{}, divisor{};
        std::uint32_t blurOutputWidth{}, blurOutputHeight{};
        ComputeBlurLayout(frame, sigmaX, sigmaY, divisor, blurOutputWidth, blurOutputHeight);
        EnsureBlurTargets(blurOutputWidth, blurOutputHeight);
        EnsureOutput(frame.widthPx, frame.heightPx, blurOutputWidth, blurOutputHeight);

        winrt::com_ptr<ID2D1Device> contextD2dDevice;
        drawingContext->GetDevice(contextD2dDevice.put());
        Require(contextD2dDevice.get() != nullptr);

        // The host calls this before Composition BeginDraw. Do not Flush
        // an idle D2D context; D3D commands are ordered before the next D2D draw.

        FrameConstants frameConstants = MakeConstants(frame);
        {
            ContextState saved(context.get());
            context->OMSetBlendState(nullptr, nullptr, 0xffffffff);
            context->OMSetDepthStencilState(nullptr, 0);
            context->RSSetState(rasterizer.get());
            context->IASetInputLayout(nullptr);
            context->IASetPrimitiveTopology(D3D11_PRIMITIVE_TOPOLOGY_TRIANGLELIST);
            context->SetPredication(nullptr, FALSE);

            PrepareBlur(sigmaX, sigmaY, divisor);
            UploadConstants(frameConstants);

            auto target = outputRtv.get();
            context->OMSetRenderTargets(1, &target, nullptr);
            D3D11_VIEWPORT viewport{
                0,
                0,
                static_cast<float>(frame.widthPx),
                static_cast<float>(frame.heightPx),
                0,
                1};
            context->RSSetViewports(1, &viewport);
            context->VSSetShader(vertexShader.get(), nullptr, 0);
            context->GSSetShader(nullptr, nullptr, 0);
            context->HSSetShader(nullptr, nullptr, 0);
            context->DSSetShader(nullptr, nullptr, 0);
            context->PSSetShader(pixelShader.get(), nullptr, 0);

            auto constantBuffer = constants.get();
            context->PSSetConstantBuffers(0, 1, &constantBuffer);
            ID3D11ShaderResourceView* sources[2]{imageView.get(), blurImages[1].get()};
            context->PSSetShaderResources(0, 2, sources);
            auto samplerState = sampler.get();
            context->PSSetSamplers(0, 1, &samplerState);

            float transparent[4]{};
            context->ClearRenderTargetView(outputRtv.get(), transparent);
            context->Draw(3, 0);

            ID3D11ShaderResourceView* noSources[2]{};
            context->PSSetShaderResources(0, 2, noSources);
            context->OMSetRenderTargets(0, nullptr, nullptr);
        }
        context->Flush();

        auto contextD2dIdentity = ComIdentity(contextD2dDevice.get());
        if (!outputBitmap || outputD2dIdentity.get() != contextD2dIdentity.get()
            || outputBitmapDpiX != 96.0f * frame.dpiScaleX
            || outputBitmapDpiY != 96.0f * frame.dpiScaleY)
        {
            outputBitmap = nullptr;
            outputD2dDevice = nullptr;
            outputD2dIdentity = nullptr;

            winrt::com_ptr<IDXGISurface> surface;
            winrt::check_hresult(outputTexture->QueryInterface(__uuidof(IDXGISurface), surface.put_void()));
            D2D1_BITMAP_PROPERTIES1 properties{};
            properties.pixelFormat.format = DXGI_FORMAT_B8G8R8A8_UNORM;
            properties.pixelFormat.alphaMode = D2D1_ALPHA_MODE_PREMULTIPLIED;
            properties.dpiX = 96.0f * frame.dpiScaleX;
            properties.dpiY = 96.0f * frame.dpiScaleY;
            properties.bitmapOptions = D2D1_BITMAP_OPTIONS_NONE;
            winrt::check_hresult(drawingContext->CreateBitmapFromDxgiSurface(
                surface.get(),
                &properties,
                outputBitmap.put()));
            outputD2dDevice = contextD2dDevice;
            outputD2dIdentity = contextD2dIdentity;
            outputBitmapDpiX = properties.dpiX;
            outputBitmapDpiY = properties.dpiY;
        }
        return outputBitmap;
    }
    DWORD ownerThread{};
    winrt::com_ptr<ID3D11Device> device;
    winrt::com_ptr<ID3D11DeviceContext> context;

    winrt::com_ptr<ID3D11VertexShader> vertexShader;
    winrt::com_ptr<ID3D11PixelShader> pixelShader;
    winrt::com_ptr<ID3D11PixelShader> blurPixelShader;
    winrt::com_ptr<ID3D11SamplerState> sampler;
    winrt::com_ptr<ID3D11RasterizerState> rasterizer;
    winrt::com_ptr<ID3D11Buffer> constants;

    winrt::com_ptr<ID3D11Texture2D> imageTexture;
    winrt::com_ptr<ID3D11ShaderResourceView> imageView;
    std::uint32_t imageWidth{};
    std::uint32_t imageHeight{};
    OwnedImageAlphaMode alphaModeStored{OwnedImageAlphaMode::Opaque};
    std::array<winrt::com_ptr<ID3D11ShaderResourceView>, 2> blurImages;
    std::array<winrt::com_ptr<ID3D11RenderTargetView>, 2> blurTargets;
    std::uint32_t blurWidth{};
    std::uint32_t blurHeight{};
    float cachedSigmaX{-1.0f};
    float cachedSigmaY{-1.0f};

    winrt::com_ptr<ID3D11Texture2D> outputTexture;
    winrt::com_ptr<ID3D11RenderTargetView> outputRtv;
    std::uint32_t outputWidth{};
    std::uint32_t outputHeight{};
    winrt::com_ptr<ID2D1Device> outputD2dDevice;
    winrt::com_ptr<IUnknown> outputD2dIdentity;
    winrt::com_ptr<ID2D1Bitmap1> outputBitmap;
    float outputBitmapDpiX{};
    float outputBitmapDpiY{};
};

OwnedImageGlass::OwnedImageGlass(ID3D11Device* existingDevice)
    : impl_(std::make_unique<Impl>(existingDevice))
{
}

OwnedImageGlass::~OwnedImageGlass() = default;
OwnedImageGlass::OwnedImageGlass(OwnedImageGlass&&) noexcept = default;
OwnedImageGlass& OwnedImageGlass::operator=(OwnedImageGlass&&) noexcept = default;

void OwnedImageGlass::SetImage(
    std::uint8_t const* bgra,
    std::uint32_t width,
    std::uint32_t height,
    std::size_t bytes,
    OwnedImageAlphaMode alphaMode)
{
    if (!impl_)
        winrt::check_hresult(E_UNEXPECTED);
    impl_->SetImage(bgra, width, height, bytes, alphaMode);
}

winrt::com_ptr<ID2D1Bitmap1> OwnedImageGlass::Render(
    ID2D1DeviceContext* drawingContext,
    OwnedImageGlassFrame const& frame)
{
    if (!impl_)
        winrt::check_hresult(E_UNEXPECTED);
    return impl_->Render(drawingContext, frame);
}
} // namespace gd


















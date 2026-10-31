#define WIN32_LEAN_AND_MEAN
#define NOMINMAX
#include <windows.h>
#include <inspectable.h>
#include <winrt/Windows.Foundation.h>
#include <winrt/Windows.UI.Composition.h>

#include <atomic>
#include <cstdint>

namespace
{
    using namespace winrt;
    using FoundationInspectable = winrt::Windows::Foundation::IInspectable;
    using winrt::Windows::Graphics::DirectX::DirectXAlphaMode;
    using winrt::Windows::Graphics::DirectX::DirectXPixelFormat;
    using winrt::Windows::UI::Composition::ICompositionSurface;
    using winrt::Windows::UI::Composition::ICompositionDrawingSurface;

    std::atomic<int32_t> liveSurfaces{};

    // A no-graphics stand-in for CompositionDrawingSurface consumed by
    // GDCreateSystemGlassInk. It owns no Compositor, device, window, or surface.
    struct DrawingSurfaceStub : implements<DrawingSurfaceStub,
        ICompositionDrawingSurface, ICompositionSurface>
    {
        DrawingSurfaceStub() noexcept { ++liveSurfaces; }
        ~DrawingSurfaceStub() noexcept { --liveSurfaces; }

        DirectXAlphaMode AlphaMode() const { return static_cast<DirectXAlphaMode>(0); }
        DirectXPixelFormat PixelFormat() const { return static_cast<DirectXPixelFormat>(0); }
        winrt::Windows::Foundation::Size Size() const { return {64.0f, 32.0f}; }

        hstring GetRuntimeClassName() const override
        {
            return L"Windows.UI.Composition.CompositionDrawingSurface";
        }
    };
}

extern "C" __declspec(dllexport) HRESULT __stdcall InkInteropCreateSurface(
    ::IInspectable** result) noexcept
{
    if (!result) return E_POINTER;
    *result = nullptr;
    try
    {
        auto implementation = winrt::make_self<DrawingSurfaceStub>();
        auto surface = implementation.as<ICompositionDrawingSurface>();
        *result = static_cast<::IInspectable*>(winrt::detach_abi(surface));
        return S_OK;
    }
    catch (...) { return winrt::to_hresult(); }
}

extern "C" __declspec(dllexport) HRESULT __stdcall InkInteropCloneSurface(
    void* context, ::IInspectable* input, float x, float y, float width, float height,
    ::IInspectable** result) noexcept
{
    if (!result) return E_POINTER;
    *result = nullptr;
    // Keep the probe's native signature aligned with GDCreateSystemGlassInk.
    // The marker is compared only; it is never dereferenced.
    if (context != reinterpret_cast<void*>(uintptr_t{0x1357}) ||
        x != 1.25f || y != -2.5f || width != 128.5f || height != 48.25f)
        return E_INVALIDARG;
    if (!input) return E_POINTER;
    try
    {
        // This is the same inspectable copy and interface query used by the
        // production entry point, isolated from all Compositor operations.
        FoundationInspectable copy{nullptr};
        winrt::copy_from_abi(copy, input);
        auto drawingSurface = copy.try_as<ICompositionDrawingSurface>();
        if (!drawingSurface) return E_NOINTERFACE;
        auto surface = copy.try_as<ICompositionSurface>();
        if (!surface) return E_NOINTERFACE;

        // Transfer the owned copy reference to the caller. The local typed
        // query releases independently on return.
        *result = static_cast<::IInspectable*>(winrt::detach_abi(copy));
        return S_OK;
    }
    catch (...) { return winrt::to_hresult(); }
}

extern "C" __declspec(dllexport) HRESULT __stdcall InkInteropVerifySurface(
    ::IInspectable* input, int32_t* matched) noexcept
{
    if (!matched) return E_POINTER;
    *matched = 0;
    if (!input) return E_POINTER;
    try
    {
        FoundationInspectable copy{nullptr};
        winrt::copy_from_abi(copy, input);
        auto drawingSurface = copy.try_as<ICompositionDrawingSurface>();
        if (!drawingSurface) return E_NOINTERFACE;
        auto surface = copy.try_as<ICompositionSurface>();
        if (!surface) return E_NOINTERFACE;
        *matched = 1;
        return S_OK;
    }
    catch (...) { return winrt::to_hresult(); }
}

extern "C" __declspec(dllexport) int32_t __stdcall InkInteropLiveSurfaceCount() noexcept
{
    return liveSurfaces.load();
}

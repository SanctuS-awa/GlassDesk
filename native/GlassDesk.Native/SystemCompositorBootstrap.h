#pragma once

#ifndef WIN32_LEAN_AND_MEAN
#define WIN32_LEAN_AND_MEAN
#endif
#ifndef NOMINMAX
#define NOMINMAX
#endif
#ifndef WINVER
#define WINVER 0x0A00
#endif
#ifndef _WIN32_WINNT
#define _WIN32_WINNT 0x0A00
#endif

#include <windows.h>
#include <d2d1_1.h>
#include <d3d11.h>
#include <d3d11_4.h>
#include <dxgi.h>

#include "DesktopSharedSource.h"
#include "InteropCompositorAbi.h"

#include <winrt/Windows.System.h>
#include <winrt/Windows.UI.Composition.h>
#include <winrt/base.h>

namespace gd::systemglass
{
    using PrivateInteropPartner =
        system_compositor_lens::private_composition_abi::IInteropCompositorPartner;

    struct BootstrapResources final
    {
        winrt::com_ptr<ID3D11Device> d3dDevice;
        winrt::com_ptr<ID3D11DeviceContext> d3dContext;
        winrt::com_ptr<ID2D1Factory1> d2dFactory;
        winrt::com_ptr<ID2D1Device> d2dDevice;

        // Declare compositor-facing outputs last so their references are released
        // before the interop partner and graphics devices during normal teardown.
        winrt::com_ptr<PrivateInteropPartner> interopPartner;
        winrt::com_ptr<IDCompositionDesktopDevice> dcompDesktopDevice;
        winrt::Windows::UI::Composition::Compositor compositor{ nullptr };

        void Reset() noexcept;
    };

    enum class BootstrapStage : unsigned
    {
        none,
        validateCallerThread,
        verifySystemModules,
        loadSystemModules,
        createD3DDevice,
        getImmediateContext,
        enableD3DMultithreadProtection,
        createD2DFactory,
        createD2DDevice,
        activateInteropFactory,
        createInteropCompositor,
        queryCompositor,
        queryDesktopDevice,
        complete,
        alreadyAttempted,
        shutdown,
    };

    struct BootstrapStatus final
    {
        BootstrapStage stage{ BootstrapStage::none };
        HRESULT stageStatus{ S_FALSE };
    };

    class SystemCompositorBootstrap final
    {
    public:
        SystemCompositorBootstrap() noexcept = default;
        ~SystemCompositorBootstrap() noexcept;

        SystemCompositorBootstrap(SystemCompositorBootstrap const&) = delete;
        SystemCompositorBootstrap& operator=(SystemCompositorBootstrap const&) = delete;
        SystemCompositorBootstrap(SystemCompositorBootstrap&&) = delete;
        SystemCompositorBootstrap& operator=(SystemCompositorBootstrap&&) = delete;

        // Uses only the caller's existing COM apartment and DispatcherQueue.
        // Creation is single-attempt: failures are reported with the stage and are
        // not retried with another driver, factory, or activation path.
        HRESULT Create(HWND notificationHost = nullptr) noexcept;
        // Status query only: no rendering submission or device recreation.
        HRESULT CheckDeviceHealth() const noexcept;
        void AcknowledgeDirty() noexcept;
        static constexpr UINT DirtyMessage=WM_APP+88;

        // Releases this bootstrap's COM/WinRT references. Call only after the host
        // has released materials and foreground dependents. It never calls RealClose.
        // System DLLs stay loaded until this bootstrap is destroyed so returned
        // ordinal function pointers remain valid for the object's lifetime.
        HRESULT Shutdown() noexcept;

        BootstrapStatus Status() const noexcept { return m_status; }
        bool IsCreated() const noexcept { return m_created; }

        // Both outputs are borrowed and may only be used on the Create thread.
        HRESULT GetResources(BootstrapResources const** resources) const noexcept;
        HRESULT GetDesktopVisualFunctions(
            system_compositor_lens::CreateSharedDesktopVisualFn* createSharedVisual,
            system_compositor_lens::UpdateSharedDesktopVisualFn* updateSharedVisual) const noexcept;

    private:
        struct SystemModule final
        {
            HMODULE value{};
            ~SystemModule() noexcept;
            SystemModule() noexcept = default;
            SystemModule(SystemModule const&) = delete;
            SystemModule& operator=(SystemModule const&) = delete;
        };

        HRESULT Fail(BootstrapStage stage, HRESULT status) noexcept;
        void SetStatus(BootstrapStage stage, HRESULT status) noexcept;
        HRESULT VerifyCallingThread() const noexcept;
        HRESULT VerifySystemFiles() noexcept;
        HRESULT LoadSystemModules() noexcept;
        HRESULT CreateGraphicsDevices() noexcept;
        HRESULT CreateInteropResources() noexcept;

        // Declaration order is intentional. Destruction runs in reverse order:
        // outputs first, then dcomp.dll, then dwmapi.dll.
        winrt::com_ptr<system_compositor_lens::private_composition_abi::IInteropCompositorPartnerCallback> m_dirtyCallback;
        SystemModule m_dwmapiModule;
        SystemModule m_dcompModule;
        BootstrapResources m_resources;

        DWORD m_ownerThreadId{};
        bool m_attempted{};
        bool m_creating{};
        bool m_created{};
        bool m_shutdown{};
        HRESULT m_shutdownResult{S_OK};
        system_compositor_lens::CreateSharedDesktopVisualFn m_createSharedVisual{};
        system_compositor_lens::UpdateSharedDesktopVisualFn m_updateSharedVisual{};
        BootstrapStatus m_status{};
    };
}

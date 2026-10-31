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
#include <dwmapi.h>
#include <dcomp.h>
#include <wrl/client.h>

namespace system_compositor_lens
{
    using CreateSharedDesktopVisualFn = HRESULT(WINAPI*)(
        HWND destination,
        void* dcompDevice,
        void** visual,
        PHTHUMBNAIL thumbnailId);

    using UpdateSharedDesktopVisualFn = HRESULT(WINAPI*)(
        HTHUMBNAIL thumbnailId,
        HWND* includeWindows,
        DWORD includeCount,
        HWND* excludeWindows,
        DWORD excludeCount,
        RECT* sourceRect,
        SIZE* destinationSize,
        DWORD flags);

    enum class DesktopSharedSourceStage : DWORD
    {
        None = 0,
        ValidateArguments,
        ValidateHost,
        ValidateSourceRegion,
        ExcludeHostFromLivePreview,
        CreateSharedVisual,
        UpdateSharedVisual,
        Complete
    };

    struct DesktopSharedSourceStatus
    {
        DesktopSharedSourceStage stage{DesktopSharedSourceStage::None};
        HRESULT stageStatus{S_FALSE};
    };

    struct DesktopSharedSourceUpdateMetadata
    {
        RECT requestedRegion{};
        SIZE requestedCanvas{};
        RECT returnedRegion{};
        SIZE returnedCanvas{};
        DWORD includeCount{};
        DWORD excludeCount{};
        DWORD flags{};
        HRESULT callbackStatus{S_FALSE};
        bool regionChanged{};
        bool canvasChanged{};
    };

    HRESULT InvokeSharedDesktopVisualUpdate(
        UpdateSharedDesktopVisualFn update, HTHUMBNAIL thumbnailId,
        HWND* includeWindows, DWORD includeCount, HWND* excludeWindows,
        DWORD excludeCount, RECT region, SIZE canvas, DWORD flags,
        DesktopSharedSourceUpdateMetadata* metadata) noexcept;

    // Uses the caller's hash-verified private dwmapi entry points. The returned
    // visual remains detached; no target root, HWND visibility, or composition
    // transaction is changed by this helper.
    HRESULT UpdateDesktopSource(HWND hostWindow, HWND shadowWindow, UpdateSharedDesktopVisualFn update,
        HTHUMBNAIL id, RECT region, SIZE canvas,
        DesktopSharedSourceUpdateMetadata* metadata = nullptr) noexcept;

    class DesktopSharedSource final
    {
    public:
        static HRESULT Create(
            HWND hostWindow,
            IDCompositionDesktopDevice* dcompDevice,
            CreateSharedDesktopVisualFn createSharedVisual,
            UpdateSharedDesktopVisualFn updateSharedVisual,
            RECT physicalSourceRegion,
            SIZE sourceCanvas,
            IDCompositionVisual2** visual,
            PHTHUMBNAIL thumbnailId,
            DesktopSharedSourceStatus* detail, HWND shadowWindow = nullptr) noexcept;
    };
}

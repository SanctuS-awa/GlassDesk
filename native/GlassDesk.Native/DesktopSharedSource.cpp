#define WIN32_LEAN_AND_MEAN
#define NOMINMAX
#include "DesktopSharedSource.h"

#include <cstddef>
#include <cstdint>
#if defined(GD_SYSTEM_COMPOSITOR_CANDIDATE)
#include <cstdio>
#endif

namespace system_compositor_lens
{
    namespace
    {
        constexpr DWORD kExcludedFromLivePreview = 13;
        constexpr DWORD kSharedVisualUpdateFlags = 1;
        constexpr DWORD kMaximumSourceDimension = 1024;

        struct WindowCompositionAttributeData
        {
            DWORD attribute;
            void* data;
            SIZE_T dataSize;
        };

        using SetWindowCompositionAttributeFn = BOOL(WINAPI*)(
            HWND window,
            WindowCompositionAttributeData* data);

        static_assert(sizeof(void*) == 8, "This helper requires the x64 Windows ABI.");
        static_assert(sizeof(RECT)==16 && sizeof(SIZE)==8);
        static_assert(sizeof(WindowCompositionAttributeData) == 24);
        static_assert(offsetof(WindowCompositionAttributeData, data) == 8);
        static_assert(offsetof(WindowCompositionAttributeData, dataSize) == 16);

        HRESULT LastWin32Error() noexcept
        {
            const DWORD error = GetLastError();
            return HRESULT_FROM_WIN32(error == ERROR_SUCCESS ? ERROR_GEN_FAILURE : error);
        }

        HRESULT SetFailureDetail(
            DesktopSharedSourceStatus* detail,
            DesktopSharedSourceStage stage,
            HRESULT stageStatus,
            HRESULT returnedStatus) noexcept
        {
            if (detail)
            {
                detail->stage = stage;
                detail->stageStatus = stageStatus;
            }
            return returnedStatus;
        }

        HRESULT ValidateHiddenOwnedTopLevelHost(HWND hostWindow) noexcept
        {
            if (!hostWindow || !IsWindow(hostWindow))
                return E_INVALIDARG;
            if (GetAncestor(hostWindow, GA_ROOT) != hostWindow)
                return E_INVALIDARG;

            DWORD hostProcessId = 0;
            if (GetWindowThreadProcessId(hostWindow, &hostProcessId) == 0)
                return LastWin32Error();
            if (hostProcessId == 0 || hostProcessId != GetCurrentProcessId())
                return E_ACCESSDENIED;

            SetLastError(ERROR_SUCCESS);
            const LONG_PTR style = GetWindowLongPtrW(hostWindow, GWL_STYLE);
            const DWORD styleError = GetLastError();
            if (style == 0 && styleError != ERROR_SUCCESS)
                return HRESULT_FROM_WIN32(styleError);
            if ((style & WS_CHILD) != 0 || (style & WS_VISIBLE) != 0 || IsWindowVisible(hostWindow))
                return E_ACCESSDENIED;

            return S_OK;
        }

        HRESULT ValidatePhysicalSourceRegion(RECT const& region, SIZE const& canvas) noexcept
        {
            const int64_t width = static_cast<int64_t>(region.right) - region.left;
            const int64_t height = static_cast<int64_t>(region.bottom) - region.top;
            if (width <= 0 || height <= 0 ||
                width > static_cast<int64_t>(kMaximumSourceDimension) ||
                height > static_cast<int64_t>(kMaximumSourceDimension) ||
                canvas.cx <= 0 || canvas.cy <= 0 ||
                canvas.cx > static_cast<LONG>(kMaximumSourceDimension) ||
                canvas.cy > static_cast<LONG>(kMaximumSourceDimension) ||
                canvas.cx != width || canvas.cy != height)
                return E_INVALIDARG;

            const DPI_AWARENESS_CONTEXT threadDpi = GetThreadDpiAwarenessContext();
            if (!AreDpiAwarenessContextsEqual(
                    threadDpi, DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2))
                return E_ACCESSDENIED;

            const LONG left=GetSystemMetrics(SM_XVIRTUALSCREEN), top=GetSystemMetrics(SM_YVIRTUALSCREEN);
            const LONG virtualWidth=GetSystemMetrics(SM_CXVIRTUALSCREEN), virtualHeight=GetSystemMetrics(SM_CYVIRTUALSCREEN);
            if(virtualWidth<=0 || virtualHeight<=0 || region.left<left || region.top<top ||
               int64_t(region.right)>int64_t(left)+virtualWidth || int64_t(region.bottom)>int64_t(top)+virtualHeight ||
               (width==virtualWidth && height==virtualHeight)) return E_INVALIDARG;

            return S_OK;
        }

        HRESULT ExcludeHostFromLivePreview(HWND hostWindow) noexcept
        {
            HMODULE user32 = GetModuleHandleW(L"user32.dll");
            if (!user32)
                return LastWin32Error();

            auto setWindowCompositionAttribute =
                reinterpret_cast<SetWindowCompositionAttributeFn>(
                    GetProcAddress(user32, "SetWindowCompositionAttribute"));
            if (!setWindowCompositionAttribute)
                return HRESULT_FROM_WIN32(ERROR_PROC_NOT_FOUND);

            BOOL excluded = TRUE;
            WindowCompositionAttributeData data{
                kExcludedFromLivePreview,
                &excluded,
                sizeof(excluded)};
            if (!setWindowCompositionAttribute(hostWindow, &data))
                return LastWin32Error();
            return S_OK;
        }

#if defined(GD_SYSTEM_COMPOSITOR_CANDIDATE)
        void AppendSourceUpdateMetadata(HWND host, DesktopSharedSourceUpdateMetadata const& metadata) noexcept
        {
            wchar_t path[32768]{};
            const DWORD pathLength=GetEnvironmentVariableW(L"GLASSDESK_QA_SOURCE_METADATA_PATH",path,
                static_cast<DWORD>(sizeof(path)/sizeof(path[0])));
            if(pathLength==0||pathLength>=sizeof(path)/sizeof(path[0]))return;
            static volatile LONG emitted{};
            const LONG sequence=InterlockedIncrement(&emitted);
            // Include the fully expanded state: the first sixteen updates can
            // all occur during capsule-to-Peek animation. This is still a
            // bounded, explicitly enabled metadata-only diagnostic.
            if(sequence<1||sequence>256)return;

            RECT windowRect{},clientRect{};
            const bool haveWindowRect=GetWindowRect(host,&windowRect)!=FALSE;
            const bool haveClientRect=GetClientRect(host,&clientRect)!=FALSE;
            POINT clientCorners[2]{{clientRect.left,clientRect.top},{clientRect.right,clientRect.bottom}};
            SetLastError(ERROR_SUCCESS);
            (void)MapWindowPoints(host,nullptr,clientCorners,2);
            const DWORD mapError=GetLastError();
            const bool haveClientScreen=haveClientRect&&(mapError==ERROR_SUCCESS);
            const UINT dpi=GetDpiForWindow(host);

            char line[2048]{};
            const int length=std::snprintf(line,sizeof(line),
                "{\"kind\":\"DesktopSharedSourceUpdate\",\"sequence\":%ld,\"requestedRegion\":[%ld,%ld,%ld,%ld],"
                "\"requestedCanvas\":[%ld,%ld],\"returnedRegion\":[%ld,%ld,%ld,%ld],\"returnedCanvas\":[%ld,%ld],"
                "\"callbackHresult\":\"0x%08lX\",\"includeCount\":%lu,\"excludeCount\":%lu,\"flags\":%lu,"
                "\"hostWindowRect\":[%ld,%ld,%ld,%ld],\"hostClientRect\":[%ld,%ld,%ld,%ld],"
                "\"hostClientPhysicalScreenRect\":[%ld,%ld,%ld,%ld],\"hostWindowRectValid\":%s,"
                "\"hostClientScreenRectValid\":%s,\"hostDpi\":%u}\r\n",
                sequence,
                metadata.requestedRegion.left,metadata.requestedRegion.top,metadata.requestedRegion.right,metadata.requestedRegion.bottom,
                metadata.requestedCanvas.cx,metadata.requestedCanvas.cy,
                metadata.returnedRegion.left,metadata.returnedRegion.top,metadata.returnedRegion.right,metadata.returnedRegion.bottom,
                metadata.returnedCanvas.cx,metadata.returnedCanvas.cy,
                static_cast<unsigned long>(metadata.callbackStatus),metadata.includeCount,metadata.excludeCount,metadata.flags,
                windowRect.left,windowRect.top,windowRect.right,windowRect.bottom,
                clientRect.left,clientRect.top,clientRect.right,clientRect.bottom,
                clientCorners[0].x,clientCorners[0].y,clientCorners[1].x,clientCorners[1].y,
                haveWindowRect?"true":"false",haveClientScreen?"true":"false",dpi);
            if(length<=0||static_cast<size_t>(length)>=sizeof(line))return;

            HANDLE file=CreateFileW(path,FILE_APPEND_DATA|FILE_READ_ATTRIBUTES,
                FILE_SHARE_READ|FILE_SHARE_WRITE|FILE_SHARE_DELETE,nullptr,OPEN_ALWAYS,FILE_ATTRIBUTE_NORMAL,nullptr);
            if(file==INVALID_HANDLE_VALUE)return;
            LARGE_INTEGER currentSize{};
            const bool sizeOk=GetFileSizeEx(file,&currentSize)!=FALSE&&currentSize.QuadPart>=0;
            const bool withinLimit=sizeOk&&currentSize.QuadPart<=262144&&
                static_cast<unsigned long long>(length)<=262144ull-static_cast<unsigned long long>(currentSize.QuadPart);
            if(withinLimit){
                DWORD written{};
                (void)WriteFile(file,line,static_cast<DWORD>(length),&written,nullptr);
            }
            CloseHandle(file);
        }
#endif
    }

    HRESULT DesktopSharedSource::Create(
        HWND hostWindow,
        IDCompositionDesktopDevice* dcompDevice,
        CreateSharedDesktopVisualFn createSharedVisual,
        UpdateSharedDesktopVisualFn updateSharedVisual,
        RECT physicalSourceRegion,
        SIZE sourceCanvas,
        IDCompositionVisual2** visual,
        PHTHUMBNAIL thumbnailId,
        DesktopSharedSourceStatus* detail, HWND shadowWindow) noexcept
    {
        using Microsoft::WRL::ComPtr;

        if (detail)
        {
            detail->stage = DesktopSharedSourceStage::ValidateArguments;
            detail->stageStatus = E_INVALIDARG;
        }
        if (visual)
            *visual = nullptr;
        if (thumbnailId)
            *thumbnailId = nullptr;

        if (!visual || !thumbnailId || !hostWindow || !dcompDevice ||
            !createSharedVisual || !updateSharedVisual)
            return SetFailureDetail(detail, DesktopSharedSourceStage::ValidateArguments,
                                    E_INVALIDARG, E_INVALIDARG);

        HRESULT hr = ValidateHiddenOwnedTopLevelHost(hostWindow);
        if (hr != S_OK)
            return SetFailureDetail(detail, DesktopSharedSourceStage::ValidateHost, hr, hr);

        hr = ValidatePhysicalSourceRegion(physicalSourceRegion, sourceCanvas);
        if (hr != S_OK)
            return SetFailureDetail(detail, DesktopSharedSourceStage::ValidateSourceRegion, hr, hr);

        hr = ExcludeHostFromLivePreview(hostWindow);
        if (hr != S_OK)
            return SetFailureDetail(detail, DesktopSharedSourceStage::ExcludeHostFromLivePreview, hr, hr);

        void* visualObject = nullptr;
        HTHUMBNAIL privateId = nullptr;
        hr = createSharedVisual(
            hostWindow,
            dcompDevice,
            &visualObject,
            &privateId);
        if (FAILED(hr))
        {
            if (visualObject)
                static_cast<IUnknown*>(visualObject)->Release();
            return SetFailureDetail(detail, DesktopSharedSourceStage::CreateSharedVisual, hr, hr);
        }
        if (!visualObject || !privateId)
        {
            if(visualObject)static_cast<IUnknown*>(visualObject)->Release();
            return SetFailureDetail(detail, DesktopSharedSourceStage::CreateSharedVisual,
                                    E_UNEXPECTED, E_UNEXPECTED);
        }

        ComPtr<IDCompositionVisual2> createdVisual;
        createdVisual.Attach(static_cast<IDCompositionVisual2*>(visualObject));

        hr=UpdateDesktopSource(hostWindow,shadowWindow,updateSharedVisual,privateId,physicalSourceRegion,sourceCanvas);
        if (hr != S_OK)
        {
            const HRESULT returnedStatus = FAILED(hr) ? hr : E_UNEXPECTED;
            return SetFailureDetail(detail, DesktopSharedSourceStage::UpdateSharedVisual,
                                    hr, returnedStatus);
        }
        if (!privateId)
            return SetFailureDetail(detail, DesktopSharedSourceStage::UpdateSharedVisual,
                                    E_UNEXPECTED, E_UNEXPECTED);

        *thumbnailId = privateId;
        *visual = createdVisual.Detach();
        if (detail)
        {
            detail->stage = DesktopSharedSourceStage::Complete;
            detail->stageStatus = S_OK;
        }
        return S_OK;
    }
    HRESULT InvokeSharedDesktopVisualUpdate(UpdateSharedDesktopVisualFn update, HTHUMBNAIL id,
        HWND* includeWindows,DWORD includeCount,HWND* excludeWindows,DWORD excludeCount,
        RECT region,SIZE canvas,DWORD flags,DesktopSharedSourceUpdateMetadata* metadata) noexcept
    {
        if(metadata){*metadata={};metadata->requestedRegion=region;metadata->requestedCanvas=canvas;
            metadata->includeCount=includeCount;metadata->excludeCount=excludeCount;metadata->flags=flags;}
        if(!update||!id)return E_INVALIDARG;
        const RECT requestedRegion=region;const SIZE requestedCanvas=canvas;
        const HRESULT hr=update(id,includeWindows,includeCount,excludeWindows,excludeCount,&region,&canvas,flags);
        if(metadata){metadata->returnedRegion=region;metadata->returnedCanvas=canvas;metadata->callbackStatus=hr;
            metadata->regionChanged=region.left!=requestedRegion.left||region.top!=requestedRegion.top||
                region.right!=requestedRegion.right||region.bottom!=requestedRegion.bottom;
            metadata->canvasChanged=canvas.cx!=requestedCanvas.cx||canvas.cy!=requestedCanvas.cy;}
        return hr==S_OK?S_OK:FAILED(hr)?hr:E_UNEXPECTED;
    }

    HRESULT UpdateDesktopSource(HWND host, HWND shadow, UpdateSharedDesktopVisualFn update, HTHUMBNAIL id,
        RECT region, SIZE canvas, DesktopSharedSourceUpdateMetadata* metadata) noexcept
    {
        if(metadata)*metadata={};
        if(!host || !id || !update) return E_INVALIDARG;
        auto hr=ValidatePhysicalSourceRegion(region,canvas); if(hr!=S_OK)return hr;
        HWND excluded[2]{host,shadow}; DWORD count=shadow ? 2u : 1u;
        if(shadow==host)return E_INVALIDARG;
        for(DWORD i=0;i<count;++i){DWORD pid{};
            if(!IsWindow(excluded[i]) || !GetWindowThreadProcessId(excluded[i],&pid) || pid!=GetCurrentProcessId())return E_ACCESSDENIED;
            hr=ExcludeHostFromLivePreview(excluded[i]);if(hr!=S_OK)return hr;
        }
        DesktopSharedSourceUpdateMetadata actual{};
        hr=InvokeSharedDesktopVisualUpdate(update,id,nullptr,0,excluded,count,region,canvas,1,&actual);
#if defined(GD_SYSTEM_COMPOSITOR_CANDIDATE)
        AppendSourceUpdateMetadata(host,actual);
#endif
        if(metadata)*metadata=actual;
        return hr;
    }

}

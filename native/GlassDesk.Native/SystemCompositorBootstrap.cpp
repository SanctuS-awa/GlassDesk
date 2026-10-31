#include "SystemCompositorBootstrap.h"
#include "CleanupResult.h"

#include <bcrypt.h>
#include <array>
#include <cwchar>
#include <string>
#include <vector>

#pragma comment(lib, "bcrypt.lib")
#pragma comment(lib, "d2d1.lib")
#pragma comment(lib, "d3d11.lib")
#pragma comment(lib, "dxgi.lib")
#pragma comment(lib, "ole32.lib")
#pragma comment(lib, "windowsapp.lib")

namespace gd::systemglass
{
    namespace
    {
        struct DirtyCallback : winrt::implements<DirtyCallback,system_compositor_lens::private_composition_abi::IInteropCompositorPartnerCallback> {
            HWND window{};volatile LONG pending{};
            explicit DirtyCallback(HWND value):window(value){}
            HRESULT STDMETHODCALLTYPE NotifyDirty() noexcept override {
                if(InterlockedCompareExchange(&pending,1,0)==0 && !PostMessageW(window,SystemCompositorBootstrap::DirtyMessage,0,0))InterlockedExchange(&pending,0);
                return S_OK;
            }
            HRESULT STDMETHODCALLTYPE NotifyDeferralState(bool) noexcept override {return S_OK;}
        };
        constexpr std::array<UCHAR, 32> kDwmapiSha256{
            0x73, 0x3C, 0x4D, 0xBF, 0x3E, 0x3F, 0xC5, 0x21,
            0x19, 0x9A, 0xE5, 0x7A, 0x5A, 0xDC, 0x0B, 0x78,
            0x0D, 0x8C, 0x63, 0xD3, 0xDE, 0x96, 0x67, 0x66,
            0x70, 0xCB, 0xF6, 0xD2, 0x7C, 0xFE, 0xD5, 0xE2,
        };
        constexpr std::array<UCHAR, 32> kDcompSha256{
            0x40, 0x77, 0xBF, 0x81, 0xA9, 0xA1, 0x78, 0xD0,
            0x97, 0x28, 0x8E, 0xD5, 0xB4, 0x7A, 0x66, 0xB2,
            0x18, 0x81, 0xFB, 0x75, 0x81, 0xDD, 0xD1, 0x42,
            0xE5, 0x3E, 0x66, 0xF0, 0xF9, 0x43, 0x76, 0xED,
        };

        HRESULT LastWin32Error() noexcept
        {
            const DWORD error = GetLastError();
            return HRESULT_FROM_WIN32(error == ERROR_SUCCESS ? ERROR_GEN_FAILURE : error);
        }

        HRESULT ExceptionCode() noexcept
        {
            try
            {
                throw;
            }
            catch (winrt::hresult_error const& error)
            {
                return error.code();
            }
            catch (std::bad_alloc const&)
            {
                return E_OUTOFMEMORY;
            }
            catch (...)
            {
                return E_FAIL;
            }
        }

        void LogStage(BootstrapStage stage, HRESULT status) noexcept
        {
            wchar_t message[112]{};
            const int written = ::swprintf_s(
                message,
                _countof(message),
                L"[SystemCompositorBootstrap] stage=%u hr=0x%08lX\r\n",
                static_cast<unsigned>(stage),
                static_cast<unsigned long>(status));
            if (written > 0)
                OutputDebugStringW(message);
        }

        struct FileHandle final
        {
            HANDLE value{ INVALID_HANDLE_VALUE };
            ~FileHandle() noexcept
            {
                if (value != INVALID_HANDLE_VALUE)
                    CloseHandle(value);
            }
        };

        struct Sha256Handles final
        {
            BCRYPT_ALG_HANDLE algorithm{};
            BCRYPT_HASH_HANDLE hash{};
            ~Sha256Handles() noexcept
            {
                if (hash)
                    BCryptDestroyHash(hash);
                if (algorithm)
                    BCryptCloseAlgorithmProvider(algorithm, 0);
            }
        };

        HRESULT SystemModulePath(wchar_t const* moduleName, std::wstring& path) noexcept
        {
            if (!moduleName)
                return E_INVALIDARG;
            try
            {
                std::array<wchar_t, MAX_PATH> directory{};
                const UINT length = GetSystemDirectoryW(directory.data(),
                                                        static_cast<UINT>(directory.size()));
                if (length == 0)
                    return LastWin32Error();
                if (length >= directory.size())
                    return HRESULT_FROM_WIN32(ERROR_INSUFFICIENT_BUFFER);

                path.assign(directory.data(), length);
                if (!path.empty() && path.back() != L'\\')
                    path.push_back(L'\\');
                path.append(moduleName);
                return S_OK;
            }
            catch (std::bad_alloc const&)
            {
                return E_OUTOFMEMORY;
            }
            catch (...)
            {
                return E_FAIL;
            }
        }

        HRESULT HashSystemFile(std::wstring const& path, std::array<UCHAR, 32>& digest) noexcept
        {
            FileHandle file{};
            file.value = CreateFileW(path.c_str(), GENERIC_READ, FILE_SHARE_READ | FILE_SHARE_DELETE,
                                     nullptr, OPEN_EXISTING, FILE_ATTRIBUTE_NORMAL | FILE_FLAG_SEQUENTIAL_SCAN,
                                     nullptr);
            if (file.value == INVALID_HANDLE_VALUE)
                return LastWin32Error();

            std::vector<UCHAR> hashObject; // Buffer outlives BCryptDestroyHash in sha destructor.
            Sha256Handles sha{};
            NTSTATUS status = BCryptOpenAlgorithmProvider(&sha.algorithm, BCRYPT_SHA256_ALGORITHM,
                                                          nullptr, 0);
            if (status < 0)
                return static_cast<HRESULT>(status);

            ULONG objectLength = 0;
            ULONG returned = 0;
            status = BCryptGetProperty(sha.algorithm, BCRYPT_OBJECT_LENGTH,
                                       reinterpret_cast<PUCHAR>(&objectLength), sizeof(objectLength),
                                       &returned, 0);
            if (status < 0)
                return static_cast<HRESULT>(status);
            if (objectLength == 0 || objectLength > 1024u * 1024u)
                return HRESULT_FROM_WIN32(ERROR_INVALID_DATA);

            try
            {
                hashObject.resize(objectLength);
            }
            catch (std::bad_alloc const&)
            {
                return E_OUTOFMEMORY;
            }
            catch (...)
            {
                return E_FAIL;
            }

            status = BCryptCreateHash(sha.algorithm, &sha.hash, hashObject.data(), objectLength,
                                      nullptr, 0, 0);
            if (status < 0)
                return static_cast<HRESULT>(status);

            std::array<UCHAR, 64u * 1024u> buffer{};
            for (;;)
            {
                DWORD bytesRead = 0;
                if (!ReadFile(file.value, buffer.data(), static_cast<DWORD>(buffer.size()),
                              &bytesRead, nullptr))
                    return LastWin32Error();
                if (bytesRead == 0)
                    break;
                status = BCryptHashData(sha.hash, buffer.data(), bytesRead, 0);
                if (status < 0)
                    return static_cast<HRESULT>(status);
            }

            status = BCryptFinishHash(sha.hash, digest.data(), static_cast<ULONG>(digest.size()), 0);
            return status < 0 ? static_cast<HRESULT>(status) : S_OK;
        }

        HRESULT VerifySystemFile(wchar_t const* moduleName,
                                 std::array<UCHAR, 32> const& expectedHash) noexcept
        {
            std::wstring path;
            HRESULT hr = SystemModulePath(moduleName, path);
            if (FAILED(hr))
                return hr;

            std::array<UCHAR, 32> actualHash{};
            hr = HashSystemFile(path, actualHash);
            if (FAILED(hr))
                return hr;
            return actualHash == expectedHash ? S_OK : HRESULT_FROM_WIN32(ERROR_REVISION_MISMATCH);
        }

        bool ModuleMatchesPath(HMODULE module, std::wstring const& expectedPath) noexcept
        {
            if (!module)
                return false;
            std::array<wchar_t, MAX_PATH> actualPath{};
            const DWORD length = GetModuleFileNameW(module, actualPath.data(),
                                                     static_cast<DWORD>(actualPath.size()));
            if (length == 0 || length >= actualPath.size())
                return false;
            return CompareStringOrdinal(actualPath.data(), static_cast<int>(length),
                                        expectedPath.c_str(), static_cast<int>(expectedPath.size()),
                                        TRUE) == CSTR_EQUAL;
        }

        HRESULT LoadVerifiedSystemModule(wchar_t const* moduleName,
                                        HMODULE& output) noexcept
        {
            output = nullptr;
            std::wstring expectedPath;
            HRESULT hr = SystemModulePath(moduleName, expectedPath);
            if (FAILED(hr))
                return hr;

            HMODULE module = LoadLibraryExW(moduleName, nullptr, LOAD_LIBRARY_SEARCH_SYSTEM32);
            if (!module)
                return LastWin32Error();
            if (!ModuleMatchesPath(module, expectedPath))
            {
                FreeLibrary(module);
                return HRESULT_FROM_WIN32(ERROR_BAD_PATHNAME);
            }
            output = module;
            return S_OK;
        }

        HRESULT CreateHardwareD3D11Device(BootstrapResources& resources) noexcept
        {
            constexpr D3D_FEATURE_LEVEL featureLevels[]{
                D3D_FEATURE_LEVEL_11_1,
                D3D_FEATURE_LEVEL_11_0,
                D3D_FEATURE_LEVEL_10_1,
                D3D_FEATURE_LEVEL_10_0,
            };
            D3D_FEATURE_LEVEL selectedFeatureLevel{};
            const HRESULT hr = D3D11CreateDevice(
                nullptr,
                D3D_DRIVER_TYPE_HARDWARE,
                nullptr,
                D3D11_CREATE_DEVICE_BGRA_SUPPORT,
                featureLevels,
                static_cast<UINT>(_countof(featureLevels)),
                D3D11_SDK_VERSION,
                resources.d3dDevice.put(),
                &selectedFeatureLevel,
                nullptr);
            if (FAILED(hr))
                return hr;
            if (!resources.d3dDevice)
                return E_UNEXPECTED;
            (void)selectedFeatureLevel;
            return S_OK;
        }
    }

    void BootstrapResources::Reset() noexcept
    {
        compositor = nullptr;
        dcompDesktopDevice = nullptr;
        interopPartner = nullptr;
        d2dDevice = nullptr;
        d2dFactory = nullptr;
        d3dContext = nullptr;
        d3dDevice = nullptr;
    }

    SystemCompositorBootstrap::SystemModule::~SystemModule() noexcept
    {
        if (value)
        {
            FreeLibrary(value);
            value = nullptr;
        }
    }

    SystemCompositorBootstrap::~SystemCompositorBootstrap() noexcept
    {
        (void)Shutdown();
    }

    HRESULT SystemCompositorBootstrap::Fail(BootstrapStage stage, HRESULT status) noexcept
    {
        m_resources.Reset();
        m_created = false;
        m_creating = false;
        SetStatus(stage, status);
        return status;
    }

    void SystemCompositorBootstrap::SetStatus(BootstrapStage stage, HRESULT status) noexcept
    {
        m_status = { stage, status };
        LogStage(stage, status);
    }

    HRESULT SystemCompositorBootstrap::VerifyCallingThread() const noexcept
    {
        if (m_ownerThreadId == 0 || m_ownerThreadId != GetCurrentThreadId())
            return RPC_E_WRONG_THREAD;

        APTTYPE apartmentType{};
        APTTYPEQUALIFIER apartmentQualifier{};
        const HRESULT apartmentStatus = CoGetApartmentType(&apartmentType, &apartmentQualifier);
        if (FAILED(apartmentStatus))
            return apartmentStatus;
        (void)apartmentType;
        (void)apartmentQualifier;

        try
        {
            const auto dispatcher = winrt::Windows::System::DispatcherQueue::GetForCurrentThread();
            return dispatcher ? S_OK : E_UNEXPECTED;
        }
        catch (...)
        {
            return ExceptionCode();
        }
    }

    HRESULT SystemCompositorBootstrap::VerifySystemFiles() noexcept
    {
        HRESULT hr = VerifySystemFile(L"dwmapi.dll", kDwmapiSha256);
        if (FAILED(hr))
            return hr;
        return VerifySystemFile(L"dcomp.dll", kDcompSha256);
    }

    HRESULT SystemCompositorBootstrap::LoadSystemModules() noexcept
    {
        HRESULT hr = LoadVerifiedSystemModule(L"dwmapi.dll", m_dwmapiModule.value);
        if (FAILED(hr))
            return hr;
        hr = LoadVerifiedSystemModule(L"dcomp.dll", m_dcompModule.value);
        if (FAILED(hr))
            return hr;

        m_createSharedVisual = reinterpret_cast<system_compositor_lens::CreateSharedDesktopVisualFn>(
            GetProcAddress(m_dwmapiModule.value, MAKEINTRESOURCEA(163)));
        m_updateSharedVisual = reinterpret_cast<system_compositor_lens::UpdateSharedDesktopVisualFn>(
            GetProcAddress(m_dwmapiModule.value, MAKEINTRESOURCEA(164)));
        if (!m_createSharedVisual || !m_updateSharedVisual)
            return HRESULT_FROM_WIN32(ERROR_PROC_NOT_FOUND);
        return S_OK;
    }

    HRESULT SystemCompositorBootstrap::CreateGraphicsDevices() noexcept
    {
        SetStatus(BootstrapStage::createD3DDevice, E_PENDING);
        HRESULT hr = CreateHardwareD3D11Device(m_resources);
        SetStatus(BootstrapStage::createD3DDevice, hr);
        if (FAILED(hr))
            return hr;

        SetStatus(BootstrapStage::getImmediateContext, E_PENDING);
        m_resources.d3dDevice->GetImmediateContext(m_resources.d3dContext.put());
        hr = m_resources.d3dContext ? S_OK : E_UNEXPECTED;
        SetStatus(BootstrapStage::getImmediateContext, hr);
        if (FAILED(hr))
            return hr;

        SetStatus(BootstrapStage::enableD3DMultithreadProtection, E_PENDING);
        winrt::com_ptr<ID3D11Multithread> multithread;
        try
        {
            multithread = m_resources.d3dContext.as<ID3D11Multithread>();
            hr = multithread ? S_OK : E_NOINTERFACE;
        }
        catch (...)
        {
            hr = ExceptionCode();
        }
        if (SUCCEEDED(hr))
        {
            multithread->SetMultithreadProtected(TRUE);
            if (!multithread->GetMultithreadProtected())
                hr = E_FAIL;
        }
        SetStatus(BootstrapStage::enableD3DMultithreadProtection, hr);
        if (FAILED(hr))
            return hr;

        SetStatus(BootstrapStage::createD2DFactory, E_PENDING);
        D2D1_FACTORY_OPTIONS factoryOptions{};
        hr = D2D1CreateFactory(D2D1_FACTORY_TYPE_SINGLE_THREADED,
                               __uuidof(ID2D1Factory1),
                               &factoryOptions,
                               m_resources.d2dFactory.put_void());
        SetStatus(BootstrapStage::createD2DFactory, hr);
        if (FAILED(hr))
            return hr;
        if (!m_resources.d2dFactory)
        {
            SetStatus(BootstrapStage::createD2DFactory, E_UNEXPECTED);
            return E_UNEXPECTED;
        }

        SetStatus(BootstrapStage::createD2DDevice, E_PENDING);
        winrt::com_ptr<IDXGIDevice> dxgiDevice;
        try
        {
            dxgiDevice = m_resources.d3dDevice.as<IDXGIDevice>();
            hr = dxgiDevice ? S_OK : E_NOINTERFACE;
        }
        catch (...)
        {
            hr = ExceptionCode();
        }
        if (SUCCEEDED(hr))
            hr = m_resources.d2dFactory->CreateDevice(dxgiDevice.get(), m_resources.d2dDevice.put());
        if (SUCCEEDED(hr) && !m_resources.d2dDevice)
            hr = E_UNEXPECTED;
        SetStatus(BootstrapStage::createD2DDevice, hr);
        return hr;
    }

    HRESULT SystemCompositorBootstrap::CreateInteropResources() noexcept
    {
        using InteropFactory =
            system_compositor_lens::private_composition_abi::IInteropCompositorFactoryPartner;

        winrt::com_ptr<InteropFactory> factory;
        SetStatus(BootstrapStage::activateInteropFactory, E_PENDING);
        try
        {
            factory = winrt::get_activation_factory<
                winrt::Windows::UI::Composition::Compositor, InteropFactory>();
        }
        catch (...)
        {
            const HRESULT hr = ExceptionCode();
            SetStatus(BootstrapStage::activateInteropFactory, hr);
            return hr;
        }
        if (!factory)
        {
            SetStatus(BootstrapStage::activateInteropFactory, E_UNEXPECTED);
            return E_UNEXPECTED;
        }
        SetStatus(BootstrapStage::activateInteropFactory, S_OK);

        SetStatus(BootstrapStage::createInteropCompositor, E_PENDING);
        void* partnerObject = nullptr;
        HRESULT hr = factory->CreateInteropCompositor(
            m_resources.d2dDevice.get(),
            m_dirtyCallback.get(),
            __uuidof(PrivateInteropPartner),
            &partnerObject);
        if (FAILED(hr))
        {
            if (partnerObject)
                static_cast<IUnknown*>(partnerObject)->Release();
            SetStatus(BootstrapStage::createInteropCompositor, hr);
            return hr;
        }
        if (!partnerObject)
        {
            SetStatus(BootstrapStage::createInteropCompositor, E_UNEXPECTED);
            return E_UNEXPECTED;
        }
        m_resources.interopPartner.attach(static_cast<PrivateInteropPartner*>(partnerObject));
        SetStatus(BootstrapStage::createInteropCompositor, S_OK);

        SetStatus(BootstrapStage::queryCompositor, E_PENDING);
        void* compositorAbi = nullptr;
        hr = m_resources.interopPartner->QueryInterface(
            winrt::guid_of<winrt::Windows::UI::Composition::ICompositor>(),
            &compositorAbi);
        if (FAILED(hr))
        {
            if (compositorAbi)
                static_cast<IUnknown*>(compositorAbi)->Release();
            SetStatus(BootstrapStage::queryCompositor, hr);
            return hr;
        }
        if (!compositorAbi)
        {
            SetStatus(BootstrapStage::queryCompositor, E_UNEXPECTED);
            return E_UNEXPECTED;
        }
        m_resources.compositor = winrt::Windows::UI::Composition::Compositor{
            compositorAbi, winrt::take_ownership_from_abi };
        SetStatus(BootstrapStage::queryCompositor, S_OK);

        SetStatus(BootstrapStage::queryDesktopDevice, E_PENDING);
        try
        {
            m_resources.dcompDesktopDevice = m_resources.interopPartner.as<IDCompositionDesktopDevice>();
            hr = m_resources.dcompDesktopDevice ? S_OK : E_NOINTERFACE;
        }
        catch (...)
        {
            hr = ExceptionCode();
        }
        SetStatus(BootstrapStage::queryDesktopDevice, hr);
        return hr;
    }

    HRESULT SystemCompositorBootstrap::Create(HWND notificationHost) noexcept
    {
        if (m_creating)
        {
            const HRESULT hr = HRESULT_FROM_WIN32(ERROR_BUSY);
            LogStage(BootstrapStage::alreadyAttempted, hr);
            return hr;
        }
        if (m_attempted || m_shutdown)
        {
            const HRESULT hr = HRESULT_FROM_WIN32(ERROR_ALREADY_INITIALIZED);
            SetStatus(BootstrapStage::alreadyAttempted, hr);
            return hr;
        }

        m_attempted = true;
        m_creating = true;
        m_ownerThreadId = GetCurrentThreadId();

        SetStatus(BootstrapStage::validateCallerThread, E_PENDING);
        HRESULT hr = VerifyCallingThread();
        SetStatus(BootstrapStage::validateCallerThread, hr);
        if (FAILED(hr))
            return Fail(BootstrapStage::validateCallerThread, hr);

        SetStatus(BootstrapStage::verifySystemModules, E_PENDING);
        hr = VerifySystemFiles();
        SetStatus(BootstrapStage::verifySystemModules, hr);
        if (FAILED(hr))
            return Fail(BootstrapStage::verifySystemModules, hr);

        SetStatus(BootstrapStage::loadSystemModules, E_PENDING);
        hr = LoadSystemModules();
        SetStatus(BootstrapStage::loadSystemModules, hr);
        if (FAILED(hr))
            return Fail(BootstrapStage::loadSystemModules, hr);

        hr = CreateGraphicsDevices();
        if (FAILED(hr))
            return Fail(m_status.stage, hr);

        try{if(notificationHost)m_dirtyCallback=winrt::make_self<DirtyCallback>(notificationHost);}
        catch(...){return Fail(BootstrapStage::activateInteropFactory,ExceptionCode());}
        hr = CreateInteropResources();
        if (FAILED(hr))
            return Fail(m_status.stage, hr);

        m_created = true;
        m_creating = false;
        SetStatus(BootstrapStage::complete, S_OK);
        return S_OK;
    }

    HRESULT SystemCompositorBootstrap::Shutdown() noexcept
    {
        if (m_creating)
            return HRESULT_FROM_WIN32(ERROR_BUSY);
        if (m_ownerThreadId != 0 && m_ownerThreadId != GetCurrentThreadId())
            return RPC_E_WRONG_THREAD;
        if (m_shutdown)
            return m_shutdownResult;

        m_shutdown = true;
        m_created = false;
        gd::cleanup::Result result(m_shutdownResult);
        if(m_resources.interopPartner)result.Record(m_resources.interopPartner->ClearCallback());
        m_resources.Reset();m_dirtyCallback=nullptr;
        m_shutdownResult=result.Value();
        LogStage(BootstrapStage::shutdown, m_shutdownResult);
        return m_shutdownResult;
    }

    HRESULT SystemCompositorBootstrap::CheckDeviceHealth() const noexcept
    {
        BootstrapResources const* resources = nullptr;
        const HRESULT hr = GetResources(&resources);
        if (FAILED(hr)) return hr;
        if (!resources || !resources->d3dDevice) return E_UNEXPECTED;
        return resources->d3dDevice->GetDeviceRemovedReason();
    }

    void SystemCompositorBootstrap::AcknowledgeDirty() noexcept {
        if(m_dirtyCallback)InterlockedExchange(&winrt::get_self<DirtyCallback>(m_dirtyCallback)->pending,0);
    }
    HRESULT SystemCompositorBootstrap::GetResources(
        BootstrapResources const** resources) const noexcept
    {
        if (!resources)
            return E_POINTER;
        *resources = nullptr;
        if (m_ownerThreadId == 0 || m_ownerThreadId != GetCurrentThreadId())
            return RPC_E_WRONG_THREAD;
        if (!m_created || m_shutdown)
            return E_UNEXPECTED;
        *resources = &m_resources;
        return S_OK;
    }

    HRESULT SystemCompositorBootstrap::GetDesktopVisualFunctions(
        system_compositor_lens::CreateSharedDesktopVisualFn* createSharedVisual,
        system_compositor_lens::UpdateSharedDesktopVisualFn* updateSharedVisual) const noexcept
    {
        if (!createSharedVisual || !updateSharedVisual)
            return E_POINTER;
        *createSharedVisual = nullptr;
        *updateSharedVisual = nullptr;
        if (m_ownerThreadId == 0 || m_ownerThreadId != GetCurrentThreadId())
            return RPC_E_WRONG_THREAD;
        if (!m_createSharedVisual || !m_updateSharedVisual)
            return E_UNEXPECTED;
        *createSharedVisual = m_createSharedVisual;
        *updateSharedVisual = m_updateSharedVisual;
        return S_OK;
    }
}


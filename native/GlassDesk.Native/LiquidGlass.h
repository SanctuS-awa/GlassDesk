#pragma once
#include <windows.h>
#include <d3d11.h>
#include <winrt/Windows.UI.Composition.h>
#include <memory>
#include <string>

namespace gd
{
// Guard a timestamp taken before startup against unsigned subtraction wrap.
inline bool CaptureWaitExpired(uint64_t now,uint64_t started,uint64_t timeout=5000)noexcept
{return now>=started&&now-started>=timeout;}
struct LiquidGlassStats
{
    uint64_t frames{},estimatedTextureBytes{},gpuSamples{};
    unsigned captureWidth{},captureHeight{},panelWidth{},panelHeight{};
    double gpuAverageMilliseconds{},gpuMaximumMilliseconds{},gpuPipelineAverageMilliseconds{},gpuPipelineMaximumMilliseconds{};
    uint64_t captureCallbacks{},receivedCaptureFrames{};
    unsigned workerPid{};
};
// Public methods run on the host UI thread. Product material is capture-free.
// Historical capture entry points are disabled ABI stubs; the implementation
// contains no capture worker, screen source, or window-affinity manipulation.
class LiquidGlass
{
public:
    LiquidGlass(HWND body,HWND shadow,ID3D11Device* device,ID3D11DeviceContext* context,
        winrt::Windows::UI::Composition::CompositionGraphicsDevice const& graphics);
    ~LiquidGlass();
    LiquidGlass(LiquidGlass const&)=delete;
    LiquidGlass& operator=(LiquidGlass const&)=delete;
    void SetEnabled(bool enabled) noexcept;
    void Suspend(bool hidden,bool solid) noexcept;
    void Tick(RECT panel,float radiusPixels,float blurPixels) noexcept;
    // Procedural light only. Never starts/reads desktop capture.
    winrt::Windows::UI::Composition::CompositionDrawingSurface OpticalSurface(unsigned width,unsigned height,float radiusPixels,float scale,float tension=0) noexcept;
    // The old system-desktop light entry point was deleted in 0.2.1.1beta
    // with the DWM shared-surface route; see LiquidGlassCapture.cpp.
    HRESULT Close() noexcept;
    bool Enabled()const noexcept;
    bool Ready()const noexcept;
    // Historical capture readiness is always false in this implementation.
    bool MatchesFrame(RECT panel,float radiusPixels,float blurPixels)const noexcept;
    std::wstring Status()const;
    LiquidGlassStats Stats()const noexcept;
    winrt::Windows::UI::Composition::CompositionDrawingSurface Surface()const;
    HRESULT SaveDiagnostic(wchar_t const* path) noexcept;
    // Explicit fixture only. Real GPU shaders over a synthetic grid, never desktop pixels.
    HRESULT SaveReferencePreview(wchar_t const* path,unsigned width,unsigned height,float radiusPixels,float scale,unsigned theme,float tension=0) noexcept;
    // Historical desktop screenshot entry points return E_NOTIMPL. They do not
    // change display affinity or obtain a desktop/window image.
    HRESULT BeginDiagnosticSnapshot(unsigned durationMilliseconds) noexcept;
    HRESULT EndDiagnosticSnapshot() noexcept;
    std::wstring DiagnosticSnapshotStatus()const;
private:
    struct Impl;
    std::unique_ptr<Impl> impl;
};
}

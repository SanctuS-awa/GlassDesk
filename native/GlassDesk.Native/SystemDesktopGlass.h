#pragma once
#include "SystemCompositorBootstrap.h"
#include "SystemGlassScene.h"
#include "SystemGlassInk.h"
#include <winrt/Windows.Foundation.Numerics.h>

namespace gd::systemglass {
class SystemDesktopGlass final {
public:
    HRESULT Initialize(HWND host, HWND shadow, SystemCompositorBootstrap& bootstrap,
        winrt::Windows::UI::Composition::ContainerVisual const& root,
        float dpi, float radius, float strain) noexcept;
    HRESULT Update(float dpi,float radius,float strain,bool sourceRefresh=false) noexcept;
    HRESULT Commit() noexcept;
    HRESULT SetLenses(std::span<LensRegion const> lenses) noexcept;
    HRESULT CreateInk(winrt::Windows::UI::Composition::ICompositionSurface const& glyphMask,
        float x,float y,float width,float height,
        winrt::Windows::UI::Composition::CompositionEffectBrush& result) noexcept;
    HRESULT UpdateInk(winrt::Windows::UI::Composition::CompositionEffectBrush const& brush,
        float x,float y,float width,float height) noexcept;
    HRESULT ReleaseInk(winrt::Windows::UI::Composition::CompositionEffectBrush const& brush) noexcept;
    HRESULT SetInkTint(float tint) noexcept;
    uint32_t InkStage() const noexcept { return static_cast<uint32_t>(inkStage); }
    void SetVisible(bool visible);
    HRESULT ValidateColorProfile() const noexcept;
    HRESULT Close() noexcept;
    ~SystemDesktopGlass() noexcept {(void)Close();}
    bool Ready() const noexcept {return initialized&&!closed;}
    unsigned Stage() const noexcept {return stage;}
    // Geometry and local control descriptors only; never exposes desktop data.
    Frame const& OpticalFrame() const noexcept {return lastFrame;}
private:
    HRESULT CalculateFrame(float dpi,float radius,float strain,Frame& frame,RECT& area,SIZE& canvas,RECT& desktopBounds) noexcept;
    HRESULT EnsureInkFactory() noexcept;
    HRESULT UpdateInkFrame(Frame const& frame) noexcept;
    HRESULT PruneInkBindings() noexcept;
    HRESULT DetachInkBinding(gd::systemglass::ink::Binding& binding) noexcept;
    HRESULT SetInkBrushTint(gd::systemglass::ink::Binding& binding,float tint) noexcept;
    HRESULT SetInkBrushFrame(gd::systemglass::ink::Binding& binding,Frame const& frame) noexcept;
    HMONITOR colorMonitor{};
    HWND host{},shadow{};DWORD thread{};bool attempted{},initialized{},closed{};unsigned stage{};
    SystemCompositorBootstrap* bootstrap{};
    BootstrapResources const* resources{};
    system_compositor_lens::CreateSharedDesktopVisualFn create{};
    system_compositor_lens::UpdateSharedDesktopVisualFn update{};
    winrt::com_ptr<IDCompositionVisual2> sourceParent;
    winrt::com_ptr<IDCompositionVisual2> shared;
    HTHUMBNAIL privateId{};
    winrt::Windows::UI::Composition::ContainerVisual root{nullptr};
    // Move only these root-facing containers. SourceVisual coordinates remain
    // local to the unshifted source visuals used by the two VisualSurfaces.
    winrt::Windows::UI::Composition::ContainerVisual sourcePlacement{nullptr},correctionPlacement{nullptr};
    bool sourcePlaced{},correctionPlaced{},sourceAttached{},correctionAttached{},sceneAttached{};
    winrt::Windows::UI::Composition::Visual source{nullptr};
    winrt::Windows::UI::Composition::CompositionVisualSurface surface{nullptr};
    winrt::Windows::UI::Composition::CompositionEffectFactory correctionFactory{nullptr};
    winrt::Windows::UI::Composition::CompositionEffectBrush correctionBrush{nullptr};
    winrt::Windows::UI::Composition::CompositionSurfaceBrush colorInput{nullptr};
    winrt::Windows::UI::Composition::SpriteVisual correctionSprite{nullptr};
    winrt::Windows::UI::Composition::CompositionVisualSurface correctedSurface{nullptr};
    winrt::Windows::UI::Composition::CompositionEffectFactory inkFactory{nullptr};
    winrt::Windows::UI::Composition::CompositionColorBrush inkWhite{nullptr};
    std::vector<gd::systemglass::ink::Binding> inkBindings;
    gd::systemglass::ink::InkStage inkStage{gd::systemglass::ink::InkStage::None};
    float inkTint{};
    system_compositor_lens::SystemGlassScene scene;
    std::array<LensRegion,3> regions{};uint32_t regionCount{};Frame lastFrame{};bool haveFrame{};
    RECT lastArea{},lastDesktopBounds{};SIZE lastCanvas{};bool haveRegion{};
    HRESULT closeResult{S_OK};
};
}

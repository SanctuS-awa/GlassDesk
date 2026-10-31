#pragma once

#ifndef WIN32_LEAN_AND_MEAN
#define WIN32_LEAN_AND_MEAN
#endif
#ifndef NOMINMAX
#define NOMINMAX
#endif
#include <windows.h>

#include "SystemGlassGeometry.h"

#include <winrt/Windows.Foundation.h>
#include <winrt/Windows.UI.Composition.h>
#include <winrt/Windows.UI.Composition.Scenes.h>

#include <array>
#include <span>

namespace system_compositor_lens
{
    // One compositor-only scene visual. Geometry and UVs are app-owned CPU
    // data from SystemGlassGeometry; source pixels stay in the supplied
    // composition surface.
    class SystemGlassScene final
    {
    public:
        SystemGlassScene() noexcept = default;
        ~SystemGlassScene() noexcept;

        SystemGlassScene(SystemGlassScene const&) = delete;
        SystemGlassScene& operator=(SystemGlassScene const&) = delete;
        SystemGlassScene(SystemGlassScene&&) = delete;
        SystemGlassScene& operator=(SystemGlassScene&&) = delete;

        // The compositor and source surface remain caller-owned. The scene
        // never creates a device, target, window, source surface, or pixel
        // texture. Initialize may be attempted exactly once. Any create/update
        // failure permanently trips this object; it will not retry or fall
        // back, and later Initialize/Update calls return E_ABORT.
        HRESULT Initialize(
            winrt::Windows::UI::Composition::Compositor const& compositor,
            winrt::Windows::UI::Composition::ICompositionSurface const& fullSource,
            gd::systemglass::Frame const& frame) noexcept;

        // Updates the one mesh in place on the Initialize thread. Identical
        // frames are a no-op; index data is uploaded again only when the
        // generated grid topology changes.
        HRESULT Update(gd::systemglass::Frame const& frame) noexcept;

        // Stores at most three GDOwnedGlassLens-compatible DIP regions in
        // fixed storage. Before Initialize this primes the first frame; after
        // Initialize it updates only the cached mesh UV attribute.
        HRESULT SetLenses(std::span<gd::systemglass::LensRegion const> lenses) noexcept;

        // Returns the caller-insertable SceneVisual only while initialized and
        // on the owning UI thread. The caller controls its position and clip.
        winrt::Windows::UI::Composition::Scenes::SceneVisual Visual() const noexcept;

        // Idempotently detaches the scene graph and releases the source handle.
        // Must be called on the thread that initialized the scene.
        HRESULT Close() noexcept;

        bool IsInitialized() const noexcept { return m_initialized; }
        bool IsTripped() const noexcept { return m_tripped; }
        unsigned DiagnosticStage() const noexcept { return m_stage; }
        HRESULT Failure() const noexcept { return m_failure; }

    private:
        HRESULT CheckOwnerThread() const noexcept;
        HRESULT CreateSceneObjects(
            winrt::Windows::UI::Composition::Compositor const& compositor,
            winrt::Windows::UI::Composition::ICompositionSurface const& fullSource) noexcept;
        HRESULT Rebuild(gd::systemglass::Frame const& frame) noexcept;
        HRESULT ApplyMeshData(gd::systemglass::SceneMesh const& data,
                              bool uploadPositions,
                              bool uploadNormals,
                              bool uploadIndices) noexcept;
        HRESULT Trip(HRESULT failure) noexcept;
        HRESULT DetachAndReleaseGraph() noexcept;

        DWORD m_ownerThreadId{};
        unsigned m_stage{};
        bool m_createAttempted{};
        bool m_initialized{};
        bool m_tripped{};
        bool m_closed{};
        bool m_haveFrame{};
        bool m_haveTopology{};
        std::uint32_t m_lastColumns{};
        std::uint32_t m_lastRows{};
        HRESULT m_failure{ S_OK };
        HRESULT m_cleanupResult{ S_OK };
        gd::systemglass::Frame m_lastFrame{};
        gd::systemglass::SceneMesh m_cachedGeometry{};
        std::array<gd::systemglass::LensRegion, 3> m_pendingLenses{};
        std::uint32_t m_pendingLensCount{};
        bool m_hasPendingLenses{};

        winrt::Windows::UI::Composition::Compositor m_compositor{ nullptr };
        winrt::Windows::UI::Composition::ICompositionSurface m_fullSource{ nullptr };
        winrt::Windows::UI::Composition::Scenes::SceneVisual m_visual{ nullptr };
        winrt::Windows::UI::Composition::Scenes::SceneNode m_rootNode{ nullptr };
        winrt::Windows::UI::Composition::Scenes::SceneMesh m_mesh{ nullptr };
        winrt::Windows::UI::Composition::Scenes::SceneMeshRendererComponent m_renderer{ nullptr };
        winrt::Windows::UI::Composition::Scenes::SceneMetallicRoughnessMaterial m_material{ nullptr };
        winrt::Windows::UI::Composition::Scenes::SceneSurfaceMaterialInput m_surfaceInput{ nullptr };
    };
}

#include "SystemGlassScene.h"
#include "CleanupResult.h"

#include <winrt/Windows.Foundation.Collections.h>
#include <winrt/Windows.Foundation.Numerics.h>
#include <winrt/Windows.Graphics.DirectX.h>

#include <cstddef>
#include <cstdint>
#include <cstring>
#include <limits>
#include <new>
#include <vector>

#pragma comment(lib, "windowsapp.lib")

namespace system_compositor_lens
{
    namespace
    {
        using namespace winrt;
        using namespace winrt::Windows::Foundation;
        using namespace winrt::Windows::Graphics::DirectX;
        using namespace winrt::Windows::UI::Composition;
        using namespace winrt::Windows::UI::Composition::Scenes;

        using GeometryMesh = gd::systemglass::SceneMesh;

        static_assert(sizeof(gd::systemglass::Float2) == sizeof(float) * 2u);
        static_assert(sizeof(gd::systemglass::Float3) == sizeof(float) * 3u);
        static_assert(sizeof(std::uint16_t) == 2u);

        HRESULT ExceptionCode() noexcept
        {
            try
            {
                throw;
            }
            catch (hresult_error const& error)
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

        HRESULT GeometryStatusToHresult(gd::systemglass::BuildStatus status) noexcept
        {
            using gd::systemglass::BuildStatus;
            switch (status)
            {
            case BuildStatus::success:
                return S_OK;
            case BuildStatus::invalidFrame:
                return E_INVALIDARG;
            case BuildStatus::capacityExceeded:
                return HRESULT_FROM_WIN32(ERROR_ARITHMETIC_OVERFLOW);
            case BuildStatus::nonFiniteGeometry:
                return HRESULT_FROM_WIN32(ERROR_INVALID_DATA);
            case BuildStatus::allocationFailure:
                return E_OUTOFMEMORY;
            default:
                return E_UNEXPECTED;
            }
        }

        bool SameFrame(gd::systemglass::Frame const& left,
                       gd::systemglass::Frame const& right) noexcept
        {
            if (left.lensCount > left.lenses.size() || right.lensCount > right.lenses.size())
                return false;
            if (left.widthDips != right.widthDips ||
                left.heightDips != right.heightDips ||
                left.radiusDips != right.radiusDips ||
                left.dpiScale != right.dpiScale ||
                left.strain != right.strain ||
                left.progress != right.progress ||
                left.sourceCanvasWidthPx != right.sourceCanvasWidthPx ||
                left.sourceCanvasHeightPx != right.sourceCanvasHeightPx ||
                left.bodySourceXPx != right.bodySourceXPx ||
                left.bodySourceYPx != right.bodySourceYPx ||
                left.lensCount != right.lensCount)
                return false;

            for (std::uint32_t i = 0; i < left.lensCount; ++i)
            {
                auto const& a = left.lenses[i];
                auto const& b = right.lenses[i];
                // lightInk belongs to the independent color path, not UV geometry.
                if (a.centerXDips != b.centerXDips || a.centerYDips != b.centerYDips ||
                    a.widthDips != b.widthDips || a.heightDips != b.heightDips ||
                    a.radiusDips != b.radiusDips || a.opacity != b.opacity ||
                    a.emphasis != b.emphasis)
                    return false;
            }
            return true;
        }

        bool ValidLensList(std::span<gd::systemglass::LensRegion const> lenses,
                           gd::systemglass::Frame const* frame) noexcept
        {
            if (lenses.size() > 3u)
                return false;
            for (auto const& lens : lenses)
            {
                if (!std::isfinite(lens.centerXDips) || !std::isfinite(lens.centerYDips) ||
                    !std::isfinite(lens.widthDips) || !std::isfinite(lens.heightDips) ||
                    !std::isfinite(lens.radiusDips) || !std::isfinite(lens.opacity) ||
                    !std::isfinite(lens.emphasis) || !std::isfinite(lens.lightInk) ||
                    lens.widthDips <= 0.0f || lens.widthDips > gd::systemglass::kMaxSystemLensWidthDips ||
                    lens.heightDips <= 0.0f || lens.heightDips > gd::systemglass::kMaxSystemLensHeightDips ||
                    lens.radiusDips < 0.0f || lens.radiusDips > 32.0f ||
                    lens.opacity < 0.0f || lens.opacity > 1.0f ||
                    lens.emphasis < 0.0f || lens.emphasis > 1.0f ||
                    lens.lightInk < 0.0f || lens.lightInk > 1.0f)
                    return false;
                if (frame &&
                    (lens.centerXDips - lens.widthDips * 0.5f < 0.0f ||
                     lens.centerYDips - lens.heightDips * 0.5f < 0.0f ||
                     lens.centerXDips + lens.widthDips * 0.5f > frame->widthDips ||
                     lens.centerYDips + lens.heightDips * 0.5f > frame->heightDips))
                    return false;
            }
            return true;
        }

        HRESULT ValidateMesh(GeometryMesh const& mesh) noexcept
        {
            if (mesh.columns == 0u || mesh.rows == 0u || mesh.gridStep == 0u ||
                mesh.positions.empty() ||
                mesh.positions.size() != mesh.normals.size() ||
                mesh.positions.size() != mesh.uvs.size() ||
                mesh.positions.size() > 65000u ||
                mesh.indices.empty() ||
                mesh.indices.size() % 3u != 0u ||
                mesh.indices.size() > 400000u)
                return HRESULT_FROM_WIN32(ERROR_INVALID_DATA);

            const std::uint64_t expectedVertices =
                (static_cast<std::uint64_t>(mesh.columns) + 1u) *
                (static_cast<std::uint64_t>(mesh.rows) + 1u);
            const std::uint64_t expectedIndices =
                static_cast<std::uint64_t>(mesh.columns) *
                static_cast<std::uint64_t>(mesh.rows) * 6u;
            if (expectedVertices != mesh.positions.size() ||
                expectedIndices != mesh.indices.size())
                return HRESULT_FROM_WIN32(ERROR_INVALID_DATA);

            return S_OK;
        }

        template<typename T>
        HRESULT CopyGeometryToMemoryBuffer(std::vector<T> const& source,
                                           MemoryBuffer& output) noexcept
        {
            if (source.empty() ||
                source.size() > std::numeric_limits<std::uint32_t>::max() / sizeof(T))
                return HRESULT_FROM_WIN32(ERROR_ARITHMETIC_OVERFLOW);

            const auto byteCount = static_cast<std::uint32_t>(source.size() * sizeof(T));
            if (byteCount == 0u)
                return E_INVALIDARG;

            try
            {
                MemoryBuffer buffer{ byteCount };
                auto reference = buffer.CreateReference();
                auto access = reference.as<impl::IMemoryBufferByteAccess>();
                std::uint8_t* destination{};
                std::uint32_t capacity{};
                check_hresult(access->GetBuffer(&destination, &capacity));
                if (!destination || capacity < byteCount)
                    return E_OUTOFMEMORY;

                std::memcpy(destination, source.data(), byteCount);
                output = std::move(buffer);
                return S_OK;
            }
            catch (...)
            {
                return ExceptionCode();
            }
        }
    }

    SystemGlassScene::~SystemGlassScene() noexcept
    {
        (void)Close();
    }

    HRESULT SystemGlassScene::CheckOwnerThread() const noexcept
    {
        if (m_ownerThreadId != 0u && GetCurrentThreadId() != m_ownerThreadId)
            return RPC_E_WRONG_THREAD;
        return S_OK;
    }

    HRESULT SystemGlassScene::Initialize(
        Compositor const& compositor,
        ICompositionSurface const& fullSource,
        gd::systemglass::Frame const& frame) noexcept
    {
        if (m_tripped || m_closed || m_createAttempted)
            return E_ABORT;

        const HRESULT threadResult = CheckOwnerThread();
        if (FAILED(threadResult))
            return threadResult;

        m_ownerThreadId = GetCurrentThreadId();
        m_createAttempted = true;
        if (!compositor || !fullSource)
            return Trip(E_POINTER);

        try
        {
            m_compositor = compositor;
            m_fullSource = fullSource;

            gd::systemglass::Frame initialFrame = frame;
            if (m_hasPendingLenses)
            {
                initialFrame.lensCount = m_pendingLensCount;
                initialFrame.lenses = m_pendingLenses;
            }

            HRESULT hr = CreateSceneObjects(compositor, fullSource);
            if (FAILED(hr))
                return Trip(hr);

            hr = Rebuild(initialFrame);
            if (FAILED(hr))
                return Trip(hr);

            m_initialized = true;
            return S_OK;
        }
        catch (...)
        {
            return Trip(ExceptionCode());
        }
    }

    HRESULT SystemGlassScene::CreateSceneObjects(
        Compositor const& compositor,
        ICompositionSurface const& fullSource) noexcept
    {
        try
        {
            m_stage = 2u;
            m_visual = SceneVisual::Create(compositor);
            m_stage = 3u;
            m_rootNode = SceneNode::Create(compositor);
            m_stage = 4u;
            m_mesh = SceneMesh::Create(compositor);
            m_stage = 5u;
            m_renderer = SceneMeshRendererComponent::Create(compositor);
            m_stage = 6u;
            m_material = SceneMetallicRoughnessMaterial::Create(compositor);
            m_stage = 7u;
            m_surfaceInput = SceneSurfaceMaterialInput::Create(compositor);

            m_stage = 8u;
            m_surfaceInput.Surface(fullSource);
            m_surfaceInput.WrappingUMode(SceneWrappingMode::ClampToEdge);
            m_surfaceInput.WrappingVMode(SceneWrappingMode::ClampToEdge);
            m_surfaceInput.BitmapInterpolationMode(CompositionBitmapInterpolationMode::Linear);

            // Emission supplies the compositor-owned color without scene
            // lighting. Use the same source alpha for the base input so a
            // transparent source region doesn't become opaque black. The
            // scene remains clipped to the rounded glass silhouette.
            m_stage = 9u;
            m_material.BaseColorFactor({ 0.0f, 0.0f, 0.0f, 1.0f });
            m_material.BaseColorInput(m_surfaceInput);
            m_material.EmissiveFactor({ 1.0f, 1.0f, 1.0f });
            m_material.EmissiveInput(m_surfaceInput);
            m_material.MetallicFactor(1.0f);
            m_material.RoughnessFactor(1.0f);
            m_material.AlphaMode(SceneAlphaMode::Blend);
            m_material.IsDoubleSided(true);

            m_stage = 10u;
            m_mesh.PrimitiveTopology(DirectXPrimitiveTopology::TriangleList);
            m_renderer.Mesh(m_mesh);
            m_renderer.Material(m_material);
            m_renderer.UVMappings().Insert(L"EmissiveInput", SceneAttributeSemantic::TexCoord0);
            m_renderer.UVMappings().Insert(L"BaseColorInput", SceneAttributeSemantic::TexCoord0);
            m_rootNode.Components().Append(m_renderer);

            // Scene space is Y-up while the compositor frame is Y-down.
            // Keep geometry/UV data in frame coordinates and flip the model
            // once at the SceneNode boundary.
            m_stage = 11u;
            auto transform = m_rootNode.Transform();
            transform.Scale({ 1.0f, -1.0f, 1.0f });
            transform.Translation({ 0.0f, 0.0f, -1.0f });

            m_stage = 12u;
            m_visual.Root(m_rootNode);
            m_visual.Opacity(1.0f);
            m_visual.IsVisible(false);
            return S_OK;
        }
        catch (...)
        {
            return ExceptionCode();
        }
    }

    HRESULT SystemGlassScene::Rebuild(gd::systemglass::Frame const& frame) noexcept
    {
        m_stage = 1u;
        const HRESULT buildResult = GeometryStatusToHresult(
            gd::systemglass::BuildSceneMesh(frame, m_cachedGeometry));
        if (FAILED(buildResult))
            return buildResult;

        const HRESULT meshResult = ValidateMesh(m_cachedGeometry);
        if (FAILED(meshResult))
            return meshResult;

        const bool topologyChanged = !m_haveTopology ||
                                     m_cachedGeometry.columns != m_lastColumns ||
                                     m_cachedGeometry.rows != m_lastRows;
        const bool sizeChanged = !m_haveFrame ||
                                 frame.widthDips != m_lastFrame.widthDips ||
                                 frame.heightDips != m_lastFrame.heightDips;
        const bool scaleChanged = !m_haveFrame || frame.dpiScale != m_lastFrame.dpiScale;
        HRESULT hr = ApplyMeshData(m_cachedGeometry,
                                   topologyChanged || sizeChanged,
                                   topologyChanged,
                                   topologyChanged);
        if (FAILED(hr))
            return hr;

        try
        {
            m_stage = 28u;
            if (sizeChanged)
                m_visual.Size({ frame.widthDips, frame.heightDips });
            if (scaleChanged)
                m_visual.Scale({ frame.dpiScale, frame.dpiScale, 1.0f });
            if (!m_initialized)
                m_visual.IsVisible(true);

            m_lastFrame = frame;
            m_haveFrame = true;
            m_lastColumns = m_cachedGeometry.columns;
            m_lastRows = m_cachedGeometry.rows;
            m_haveTopology = true;
            m_stage = 29u;
            return S_OK;
        }
        catch (...)
        {
            return ExceptionCode();
        }
    }

    HRESULT SystemGlassScene::ApplyMeshData(GeometryMesh const& data,
                                             bool uploadPositions,
                                             bool uploadNormals,
                                             bool uploadIndices) noexcept
    {
        MemoryBuffer positions{ nullptr };
        MemoryBuffer normals{ nullptr };
        MemoryBuffer coordinates{ nullptr };
        MemoryBuffer indices{ nullptr };

        HRESULT hr = S_OK;
        if (uploadPositions)
        {
            m_stage = 20u;
            hr = CopyGeometryToMemoryBuffer(data.positions, positions);
            if (FAILED(hr))
                return hr;
        }

        if (uploadNormals)
        {
            m_stage = 21u;
            hr = CopyGeometryToMemoryBuffer(data.normals, normals);
            if (FAILED(hr))
                return hr;
        }

        m_stage = 22u;
        hr = CopyGeometryToMemoryBuffer(data.uvs, coordinates);
        if (FAILED(hr))
            return hr;

        if (uploadIndices)
        {
            m_stage = 23u;
            hr = CopyGeometryToMemoryBuffer(data.indices, indices);
            if (FAILED(hr))
                return hr;
        }

        try
        {
            if (uploadPositions)
            {
                m_stage = 24u;
                m_mesh.FillMeshAttribute(SceneAttributeSemantic::Vertex,
                                         DirectXPixelFormat::R32G32B32Float, positions);
            }
            if (uploadNormals)
            {
                m_stage = 25u;
                m_mesh.FillMeshAttribute(SceneAttributeSemantic::Normal,
                                         DirectXPixelFormat::R32G32B32Float, normals);
            }
            m_stage = 26u;
            m_mesh.FillMeshAttribute(SceneAttributeSemantic::TexCoord0,
                                     DirectXPixelFormat::R32G32Float, coordinates);
            if (uploadIndices)
            {
                m_stage = 27u;
                m_mesh.FillMeshAttribute(SceneAttributeSemantic::Index,
                                         DirectXPixelFormat::R16UInt, indices);
            }
            return S_OK;
        }
        catch (...)
        {
            return ExceptionCode();
        }
    }

    HRESULT SystemGlassScene::Update(gd::systemglass::Frame const& frame) noexcept
    {
        if (m_tripped || m_closed)
            return E_ABORT;

        const HRESULT threadResult = CheckOwnerThread();
        if (FAILED(threadResult))
            return threadResult;
        if (!m_initialized)
            return E_UNEXPECTED;
        if (m_haveFrame && SameFrame(m_lastFrame, frame))
            return S_OK;

        const HRESULT hr = Rebuild(frame);
        if (FAILED(hr))
            return Trip(hr);
        return S_OK;
    }

    HRESULT SystemGlassScene::SetLenses(
        std::span<gd::systemglass::LensRegion const> lenses) noexcept
    {
        if (m_tripped || m_closed)
            return E_ABORT;

        const HRESULT threadResult = CheckOwnerThread();
        if (FAILED(threadResult))
            return threadResult;
        if (m_ownerThreadId == 0u)
            m_ownerThreadId = GetCurrentThreadId();

        gd::systemglass::Frame candidate = m_haveFrame ? m_lastFrame : gd::systemglass::Frame{};
        if (!ValidLensList(lenses, m_haveFrame ? &candidate : nullptr))
            return E_INVALIDARG;

        std::array<gd::systemglass::LensRegion, 3> copy{};
        std::copy(lenses.begin(), lenses.end(), copy.begin());
        candidate.lensCount = static_cast<std::uint32_t>(lenses.size());
        candidate.lenses = copy;

        if (!m_initialized)
        {
            m_pendingLenses = copy;
            m_pendingLensCount = candidate.lensCount;
            m_hasPendingLenses = true;
            return S_OK;
        }
        return Update(candidate);
    }

    SceneVisual SystemGlassScene::Visual() const noexcept
    {
        if (!m_initialized || m_tripped || m_closed || FAILED(CheckOwnerThread()))
            return nullptr;
        return m_visual;
    }

    HRESULT SystemGlassScene::Trip(HRESULT failure) noexcept
    {
        if (SUCCEEDED(failure))
            failure = E_FAIL;
        if (!m_tripped)
        {
            m_tripped = true;
            m_failure = failure;
            m_initialized = false;
            DetachAndReleaseGraph();
        }
        return m_failure;
    }

    HRESULT SystemGlassScene::DetachAndReleaseGraph() noexcept
    {
        gd::cleanup::Result result(m_cleanupResult);
        auto attempt = [&](auto&& operation) noexcept {
            result.Attempt(operation, []() noexcept { return ExceptionCode(); });
        };
        attempt([&] { if (m_visual) m_visual.IsVisible(false); });
        attempt([&] { if (m_visual) m_visual.Root(nullptr); });
        attempt([&] { if (m_rootNode) m_rootNode.Components().Clear(); });
        attempt([&] { if (m_renderer) m_renderer.Mesh(nullptr); });
        attempt([&] { if (m_renderer) m_renderer.Material(nullptr); });
        attempt([&] { if (m_material) m_material.EmissiveInput(nullptr); });
            attempt([&] { if (m_material) m_material.BaseColorInput(nullptr); });
        attempt([&] { if (m_surfaceInput) m_surfaceInput.Surface(nullptr); });
        attempt([&] { if (m_visual) m_visual.Close(); });

        m_surfaceInput = nullptr;
        m_material = nullptr;
        m_renderer = nullptr;
        m_mesh = nullptr;
        m_rootNode = nullptr;
        m_visual = nullptr;
        m_fullSource = nullptr;
        m_compositor = nullptr;
        m_initialized = false;
        m_haveFrame = false;
        m_haveTopology = false;
        m_lastColumns = 0u;
        m_lastRows = 0u;
        m_cachedGeometry = GeometryMesh{};
        m_pendingLenses = {};
        m_pendingLensCount = 0u;
        m_hasPendingLenses = false;
        m_cleanupResult = result.Value();
        return m_cleanupResult;
    }

    HRESULT SystemGlassScene::Close() noexcept
    {
        if (m_closed)
            return m_cleanupResult;

        const HRESULT threadResult = CheckOwnerThread();
        if (FAILED(threadResult))
            return threadResult;
        if (m_ownerThreadId == 0u)
            m_ownerThreadId = GetCurrentThreadId();

        m_closed = true;
        return DetachAndReleaseGraph();
    }
}

#pragma once

// Pure CPU geometry for the private system-glass compositor experiment.
// This module produces geometry and UVs only; it never reads a source surface.
#include "SurfaceTension.h"

#include <algorithm>
#include <array>
#include <cmath>
#include <cstdint>
#include <limits>
#include <vector>

namespace gd::systemglass
{
    // Geometry-facing subset of GDOwnedGlassLens. Values are DIPs relative to
    // the primary surface; tone is retained for API parity, while this
    // single-texture UV mesh applies opacity and emphasis only.
    struct LensRegion
    {
        float centerXDips{};
        float centerYDips{};
        float widthDips{};
        float heightDips{};
        float radiusDips{};
        float opacity{};
        float emphasis{};
        float lightInk{};
    };
    static_assert(sizeof(LensRegion) == sizeof(float) * 8u);
    constexpr float kMaxSystemLensWidthDips = 320.0f;
    constexpr float kMaxSystemLensHeightDips = 320.0f;
    inline bool ValidSystemLensInput(LensRegion const& lens) noexcept
    {
        return std::isfinite(lens.centerXDips) && std::isfinite(lens.centerYDips) &&
            std::isfinite(lens.widthDips) && std::isfinite(lens.heightDips) &&
            std::isfinite(lens.radiusDips) && std::isfinite(lens.opacity) &&
            std::isfinite(lens.emphasis) && std::isfinite(lens.lightInk) &&
            std::abs(lens.centerXDips) <= 4096.0f && std::abs(lens.centerYDips) <= 4096.0f &&
            lens.widthDips > 0.0f && lens.widthDips <= kMaxSystemLensWidthDips &&
            lens.heightDips > 0.0f && lens.heightDips <= kMaxSystemLensHeightDips &&
            lens.radiusDips >= 0.0f && lens.radiusDips <= std::min(lens.widthDips, lens.heightDips) * 0.5f &&
            lens.opacity >= 0.0f && lens.opacity <= 1.0f &&
            lens.emphasis >= 0.0f && lens.emphasis <= 1.0f &&
            lens.lightInk >= 0.0f && lens.lightInk <= 1.0f;
    }

    struct Frame
    {
        // Window shape, local mesh positions and corner radius are in DIPs.
        float widthDips{};
        float heightDips{};
        float radiusDips{};
        float dpiScale{ 1.0f };
        float strain{};

        // Normalized expansion progress [0, 1], supplied by the caller.
        float progress{};

        // Dimensions and body origin are in physical source-surface pixels.
        // The source body extent is widthDips/heightDips multiplied by dpiScale.
        float sourceCanvasWidthPx{};
        float sourceCanvasHeightPx{};
        float bodySourceXPx{};
        float bodySourceYPx{};

        // At most three local control lenses. Fixed storage keeps updates
        // allocation-free and makes invalid caller buffers impossible here.
        std::uint32_t lensCount{};
        std::array<LensRegion, 3> lenses{};
    };

    // Equality for the compositor's last-frame cache. Keep this beside the
    // canonical frame shape so every optical input, including tone-only lens
    // changes, invalidates the frame. Unused fixed-capacity lens slots do not
    // participate; invalid counts can never compare equal.
    inline bool SameOpticalFrame(Frame const& a, Frame const& b) noexcept
    {
        if (a.lensCount > a.lenses.size() || b.lensCount > b.lenses.size())
            return false;

        if (a.widthDips != b.widthDips || a.heightDips != b.heightDips ||
            a.radiusDips != b.radiusDips || a.dpiScale != b.dpiScale ||
            a.strain != b.strain || a.progress != b.progress ||
            a.sourceCanvasWidthPx != b.sourceCanvasWidthPx ||
            a.sourceCanvasHeightPx != b.sourceCanvasHeightPx ||
            a.bodySourceXPx != b.bodySourceXPx || a.bodySourceYPx != b.bodySourceYPx ||
            a.lensCount != b.lensCount)
            return false;

        for (std::uint32_t i = 0; i < a.lensCount; ++i)
        {
            LensRegion const& left = a.lenses[i];
            LensRegion const& right = b.lenses[i];
            if (left.centerXDips != right.centerXDips ||
                left.centerYDips != right.centerYDips ||
                left.widthDips != right.widthDips || left.heightDips != right.heightDips ||
                left.radiusDips != right.radiusDips || left.opacity != right.opacity ||
                left.emphasis != right.emphasis || left.lightInk != right.lightInk)
                return false;
        }
        return true;
    }

    struct Float2
    {
        float x{};
        float y{};
    };

    struct PrimarySampleCacheKey
    {
        float widthDips{};
        float heightDips{};
        float radiusDips{};
        float dpiScale{};
        float strain{};
        float progress{};
        std::uint32_t columns{};
        std::uint32_t rows{};
        std::uint64_t length{};
    };

    struct Float3
    {
        float x{};
        float y{};
        float z{};
    };

    struct SceneMesh
    {
        std::vector<Float3> positions;
        std::vector<Float3> normals;
        std::vector<Float2> uvs;
        // Body-local primary refraction sample, before FullSourceUv applies
        // the current source-canvas/ROI mapping.
        std::vector<Float2> primaryBodySamples;
        std::vector<std::uint16_t> indices;

        std::uint32_t columns{};
        std::uint32_t rows{};
        std::uint32_t gridStep{};
        float zoom{};
        float bend{ 1.0f };
        float ior{ 1.45f };
        // Supported band drives only the normal/refraction profile.
        float bandPx{};
        // Legacy recipe band keeps the original interior zoom transition.
        float primaryZoomBandPx{};

        // Reused by BuildSceneMesh: lens animation changes UVs while retaining
        // the one mesh's position, normal, and index arrays.
        float geometryWidthDips{};
        float geometryHeightDips{};
        bool geometryValid{};

        PrimarySampleCacheKey primarySampleKey{};
        bool primarySampleCacheValid{};
#if defined(GD_SYSTEMGLASS_GEOMETRY_TESTING)
        // Private-candidate-only observability. Never defined in product builds.
        std::uint64_t primarySampleCacheHitsForTest{};
        std::uint64_t primarySampleCacheMissesForTest{};
#endif
    };

    enum class BuildStatus
    {
        success,
        invalidFrame,
        capacityExceeded,
        nonFiniteGeometry,
        allocationFailure,
    };

    namespace detail
    {
        constexpr std::uint64_t kMaxVertices = 65000u;
        constexpr std::uint64_t kMaxIndices = 400000u;
        constexpr float kBend = 1.0f;
        constexpr float kIor = 1.45f;
        constexpr float kPrimaryHeightRatio = 0.62f;
        // Deliberate, bounded edge stretch target for the primary lens. This
        // is a visual control, not a claim about Apple's material parameters.
        constexpr float kPrimaryRimTargetNormalScale = 0.30f;
        constexpr float kPrimaryRimMinimumNormalScale = 0.12f;

        inline bool IsFinite(float value) noexcept
        {
            return std::isfinite(value) != 0;
        }

        inline float Saturate(float value) noexcept
        {
            return std::clamp(value, 0.0f, 1.0f);
        }

        inline float Phase(float value, float start, float end) noexcept
        {
            const float t = Saturate((value - start) / (end - start));
            return t * t * (3.0f - 2.0f * t);
        }

        // Shared primary optical-band recipe for the mesh and app-owned
        // light surface. Keep the capsule and 320x176 Peek baselines intact;
        // larger, taller cards gain at most six DIPs using a geometric-mean
        // size phase, gated by height so a wide short surface stays thin.
        inline float PrimaryBandDips(float widthDips, float heightDips,
                                     float progress) noexcept
        {
            if (!IsFinite(widthDips) || !IsFinite(heightDips) || !IsFinite(progress) ||
                widthDips <= 0.0f || heightDips <= 0.0f)
                return 0.0f;

            const float base = 18.0f + 12.0f * Phase(progress, 0.08f, 1.0f);
            const float geometricMean = std::sqrt(widthDips) * std::sqrt(heightDips);
            const float areaPhase = Phase(geometricMean,
                std::sqrt(320.0f * 176.0f), std::sqrt(360.0f * 476.0f));
            const float heightPhase = Phase(heightDips, 176.0f, 476.0f);
            return base + 6.0f * std::min(areaPhase, heightPhase);
        }

        // A positive rounded radius bounds the primary normal fan before its medial center.
        // Keep the historical profile when the effective radius is exactly zero.
        inline float SupportedPrimaryBandDips(float widthDips, float heightDips,
                                              float effectiveRadiusDips,
                                              float progress) noexcept
        {
            if (!IsFinite(effectiveRadiusDips) || effectiveRadiusDips < 0.0f)
                return 0.0f;

            const float band = PrimaryBandDips(widthDips, heightDips, progress);
            if (band <= 0.0f || effectiveRadiusDips == 0.0f)
                return band;

            const float halfExtent = std::min(widthDips, heightDips) * 0.5f;
            const float supportedBand = std::min(band, std::min(effectiveRadiusDips, halfExtent));
            // Keep the physical band normal at the minimum supported DPI (0.5).
            // Below this DIP floor, treat the corner as effectively sharp and retain
            // the legacy profile rather than emitting a zero/subnormal shader band.
            const float minimumNormalDips = 2.0f * (std::numeric_limits<float>::min)();
            return supportedBand < minimumNormalDips ? band : supportedBand;
        }

        inline bool ValidateFrame(Frame const& frame) noexcept
        {
            if (!IsFinite(frame.widthDips) || !IsFinite(frame.heightDips) ||
                !IsFinite(frame.radiusDips) || !IsFinite(frame.dpiScale) ||
                !IsFinite(frame.strain) || !IsFinite(frame.progress) ||
                !IsFinite(frame.sourceCanvasWidthPx) || !IsFinite(frame.sourceCanvasHeightPx) ||
                !IsFinite(frame.bodySourceXPx) || !IsFinite(frame.bodySourceYPx))
                return false;

            if (frame.widthDips < 1.0f || frame.widthDips > 1024.0f ||
                frame.heightDips < 1.0f || frame.heightDips > 1024.0f ||
                frame.dpiScale < 0.5f || frame.dpiScale > 4.0f ||
                frame.strain < -1.15f || frame.strain > 1.15f ||
                frame.progress < 0.0f || frame.progress > 1.0f ||
                frame.sourceCanvasWidthPx < 1.0f || frame.sourceCanvasWidthPx > 1024.0f ||
                frame.sourceCanvasHeightPx < 1.0f || frame.sourceCanvasHeightPx > 1024.0f ||
                frame.radiusDips < 0.0f ||
                frame.radiusDips > std::min(frame.widthDips, frame.heightDips) * 0.5f ||
                frame.bodySourceXPx < 0.0f || frame.bodySourceYPx < 0.0f ||
                frame.lensCount > frame.lenses.size())
                return false;

            for (std::uint32_t i = 0; i < frame.lensCount; ++i)
            {
                LensRegion const& lens = frame.lenses[i];
                if (!IsFinite(lens.centerXDips) || !IsFinite(lens.centerYDips) ||
                    !IsFinite(lens.widthDips) || !IsFinite(lens.heightDips) ||
                    !IsFinite(lens.radiusDips) || !IsFinite(lens.opacity) ||
                    !IsFinite(lens.emphasis) || !IsFinite(lens.lightInk) ||
                    lens.widthDips <= 0.0f || lens.widthDips > kMaxSystemLensWidthDips ||
                    lens.heightDips <= 0.0f || lens.heightDips > kMaxSystemLensHeightDips ||
                    lens.radiusDips < 0.0f || lens.radiusDips > 32.0f ||
                    lens.opacity < 0.0f || lens.opacity > 1.0f ||
                    lens.emphasis < 0.0f || lens.emphasis > 1.0f ||
                    lens.lightInk < 0.0f || lens.lightInk > 1.0f)
                    return false;

                const float left = lens.centerXDips - lens.widthDips * 0.5f;
                const float top = lens.centerYDips - lens.heightDips * 0.5f;
                const float right = lens.centerXDips + lens.widthDips * 0.5f;
                const float bottom = lens.centerYDips + lens.heightDips * 0.5f;
                if (left < 0.0f || top < 0.0f ||
                    right > frame.widthDips || bottom > frame.heightDips)
                    return false;
            }

            const double bodyWidthPx = static_cast<double>(frame.widthDips) * frame.dpiScale;
            const double bodyHeightPx = static_cast<double>(frame.heightDips) * frame.dpiScale;
            return static_cast<double>(frame.bodySourceXPx) + bodyWidthPx <= static_cast<double>(frame.sourceCanvasWidthPx) + 0.001 &&
                   static_cast<double>(frame.bodySourceYPx) + bodyHeightPx <= static_cast<double>(frame.sourceCanvasHeightPx) + 0.001;
        }

        inline float Distance(Frame const& frame, float x, float y, float radius) noexcept
        {
            // SurfaceDistance applies the same tension profile that shapes the native contour.
            // Evaluate TensionFactor here as well so invalid profile math fails before mesh upload.
            const float tension = gd::TensionFactor(y, frame.widthDips, frame.heightDips,
                                                    1.0f, frame.strain);
            if (!IsFinite(tension) || tension <= 0.0f)
                return std::numeric_limits<float>::quiet_NaN();
            return gd::SurfaceDistance(x, y, frame.widthDips, frame.heightDips,
                                       radius, 1.0f, frame.strain);
        }

        inline bool SurfaceOutward(Frame const& frame, float x, float y, float radius,
                                   Float2& outward) noexcept
        {
            // Match PreviewGlass.hlsl's symmetric finite difference in physical pixels.
            const float epsilonDips = 0.4f / frame.dpiScale;
            const float dx = Distance(frame, x + epsilonDips, y, radius) -
                             Distance(frame, x - epsilonDips, y, radius);
            const float dy = Distance(frame, x, y + epsilonDips, radius) -
                             Distance(frame, x, y - epsilonDips, radius);
            const float length = std::hypot(dx, dy);
            if (!IsFinite(dx) || !IsFinite(dy) || !IsFinite(length))
                return false;

            // A perfectly symmetric flat center has a zero gradient in the reference shader.
            const float divisor = std::max(length, 0.0001f);
            outward = { dx / divisor, dy / divisor };
            return IsFinite(outward.x) && IsFinite(outward.y);
        }

        inline bool PrimaryZoomProjection(Frame const& frame, SceneMesh const& mesh,
                                          float depthPx, float x, float y,
                                          Float2& projected) noexcept
        {
            projected = {};
            const float zoomBand = mesh.primaryZoomBandPx;
            if (!IsFinite(depthPx) || depthPx < 0.0f ||
                !IsFinite(zoomBand) || zoomBand <= 0.0f ||
                !IsFinite(mesh.zoom) || !IsFinite(x) || !IsFinite(y) ||
                !IsFinite(frame.widthDips) || !IsFinite(frame.heightDips))
                return false;

            // Keep the projection continuous. The base zoom is unity so the
            // undistorted interior lines up with the desktop underneath; rim
            // refraction is applied separately by RefractedSample.
            const float weight = 1.0f;
            const float zoomFactor = 1.0f - 1.0f / std::max(mesh.zoom, 1.0f);
            const float centerX = frame.widthDips * 0.5f;
            const float centerY = frame.heightDips * 0.5f;
            projected = {
                x - (x - centerX) * zoomFactor * weight,
                y - (y - centerY) * zoomFactor * weight,
            };
            return IsFinite(projected.x) && IsFinite(projected.y);
        }

        inline bool RefractedSample(Frame const& frame, SceneMesh const& mesh,
                                    float radius, float x, float y, Float2& sample) noexcept
        {
            const float d = Distance(frame, x, y, radius);
            if (!IsFinite(d))
                return false;

            const float depthPx = std::max(0.0f, -d) * frame.dpiScale;
            const float band = mesh.bandPx;
            const float zoomBand = mesh.primaryZoomBandPx;
            if (!IsFinite(band) || band <= 0.0f || !IsFinite(zoomBand) || zoomBand <= 0.0f)
                return false;
            Float2 outward{};
            if (!SurfaceOutward(frame, x, y, radius, outward))
                return false;
            Float2 zoomOnly{};
            if (!PrimaryZoomProjection(frame, mesh, depthPx, x, y, zoomOnly))
                return false;

            // Inverse image mapping, not an unbounded forward ray displacement.
            // A forward offset can reverse source order at the rim, producing
            // duplicated glyphs and hooks even when every UV remains finite.
            // A quadratic is tangent-continuous with the flat core, while
            // keeping more displacement through the rounded rim than the
            // previous cubic. Its maximum normal compression occurs at the
            // edge and is limited by the same positive source-scale target.
            // The radius-supported band also bounds tangential shrink at a
            // curved corner. No pixel sampling or extra composition node is
            // introduced by this mapping.
            // This is a display-optics approximation, not Apple's parameters.
            const float t = 1.0f - Saturate(depthPx / band);
            const float zoomNormalScale = 1.0f / std::max(mesh.zoom, 1.0f);
            const float requestedCompression =
                std::max(0.0f, zoomNormalScale - kPrimaryRimTargetNormalScale);
            const float boundedCompression = std::min(requestedCompression,
                std::max(0.0f, zoomNormalScale - kPrimaryRimMinimumNormalScale));
            const float offsetDips = (band / frame.dpiScale) *
                                     (boundedCompression / 2.0f) * t * t;
            sample = {
                zoomOnly.x - outward.x * offsetDips,
                zoomOnly.y - outward.y * offsetDips,
            };
            return IsFinite(sample.x) && IsFinite(sample.y);
        }

        inline float LensDistance(LensRegion const& lens, float x, float y) noexcept
        {
            const float radius = std::clamp(
                lens.radiusDips, 0.0f, std::min(lens.widthDips, lens.heightDips) * 0.5f);
            const float qx = std::abs(x - lens.centerXDips) - lens.widthDips * 0.5f + radius;
            const float qy = std::abs(y - lens.centerYDips) - lens.heightDips * 0.5f + radius;
            return std::hypot(std::max(qx, 0.0f), std::max(qy, 0.0f)) +
                   std::min(std::max(qx, qy), 0.0f) - radius;
        }

        inline bool LensOutward(Frame const& frame, LensRegion const& lens, float x, float y,
                                Float2& outward) noexcept
        {
            const float epsilonDips = 0.4f / frame.dpiScale;
            const float dx = LensDistance(lens, x + epsilonDips, y) -
                             LensDistance(lens, x - epsilonDips, y);
            const float dy = LensDistance(lens, x, y + epsilonDips) -
                             LensDistance(lens, x, y - epsilonDips);
            const float length = std::hypot(dx, dy);
            if (!IsFinite(dx) || !IsFinite(dy) || !IsFinite(length))
                return false;
            const float divisor = std::max(length, 0.0001f);
            outward = { dx / divisor, dy / divisor };
            return IsFinite(outward.x) && IsFinite(outward.y);
        }

        inline bool SecondaryLensSample(Frame const& frame, LensRegion const& lens,
                                        float x, float y, Float2& sample) noexcept
        {
            const float distanceDips = LensDistance(lens, x, y);
            if (!IsFinite(distanceDips))
                return false;

            const float depthPx = std::max(0.0f, -distanceDips) * frame.dpiScale;
            const float bandPx = std::max(
                3.0f * frame.dpiScale,
                std::min(lens.widthDips, lens.heightDips) * 0.42f * frame.dpiScale);
            const float heightPx = bandPx * 0.46f;
            Float2 outward{};
            if (!LensOutward(frame, lens, x, y, outward))
                return false;

            const float u = 1.0f - Saturate(depthPx / bandPx);
            const float slope = (heightPx / bandPx) * u /
                                std::sqrt(std::max(1.0f - u * u, 0.003f));
            float nx = outward.x * slope;
            float ny = outward.y * slope;
            float nz = 1.0f;
            const float normalLength = std::sqrt(nx * nx + ny * ny + nz * nz);
            if (!IsFinite(normalLength) || normalLength <= 0.0f)
                return false;
            nx /= normalLength;
            ny /= normalLength;
            nz /= normalLength;

            // Match the HLSL secondary LensSample profile and its 1.45 IOR.
            constexpr float ix = 0.0f;
            constexpr float iy = 0.0f;
            constexpr float iz = -1.0f;
            constexpr float eta = 1.0f / kIor;
            const float dotNI = nx * ix + ny * iy + nz * iz;
            const float k = 1.0f - eta * eta * (1.0f - dotNI * dotNI);
            float rayX = 0.0f;
            float rayY = 0.0f;
            float rayZ = 0.0f;
            if (k >= 0.0f)
            {
                const float factor = eta * dotNI + std::sqrt(k);
                rayX = eta * ix - factor * nx;
                rayY = eta * iy - factor * ny;
                rayZ = eta * iz - factor * nz;
            }

            const float capBase = 1.0f - Saturate(depthPx / bandPx);
            const float cap = std::sqrt(Saturate(1.0f - capBase * capBase));
            const float edgePhase = Phase(depthPx, 0.0f, 3.0f);
            const float denominator = std::max(-rayZ, 0.1f);
            const float bend = 0.85f + 0.25f * lens.emphasis;
            const float edgeScale = (bandPx * 0.48f + heightPx * cap) * bend * edgePhase;
            const float edgeXDips = (rayX / denominator) * edgeScale / frame.dpiScale;
            const float edgeYDips = (rayY / denominator) * edgeScale / frame.dpiScale;

            const float zoom = 1.14f + 0.055f * lens.emphasis;
            const float weight = Phase(depthPx, 0.0f, bandPx);
            const float zoomFactor = 1.0f - 1.0f / std::max(zoom, 1.0f);
            sample = {
                x - (x - lens.centerXDips) * zoomFactor * weight + edgeXDips,
                y - (y - lens.centerYDips) * zoomFactor * weight + edgeYDips,
            };
            return IsFinite(sample.x) && IsFinite(sample.y);
        }

        inline bool FullSourceUv(Frame const& frame, Float2 bodyPoint, Float2& uv) noexcept
        {
            if (!IsFinite(bodyPoint.x) || !IsFinite(bodyPoint.y))
                return false;
            const float sourceX = std::clamp(
                frame.bodySourceXPx + bodyPoint.x * frame.dpiScale,
                0.0f, frame.sourceCanvasWidthPx);
            const float sourceY = std::clamp(
                frame.bodySourceYPx + bodyPoint.y * frame.dpiScale,
                0.0f, frame.sourceCanvasHeightPx);
            uv = { sourceX / frame.sourceCanvasWidthPx,
                   sourceY / frame.sourceCanvasHeightPx };
            return IsFinite(uv.x) && IsFinite(uv.y);
        }

        inline bool IsFinite(Float3 const& value) noexcept
        {
            return IsFinite(value.x) && IsFinite(value.y) && IsFinite(value.z);
        }

        inline bool IsFinite(Float2 const& value) noexcept
        {
            return IsFinite(value.x) && IsFinite(value.y);
        }

        inline bool SamePrimarySampleKey(PrimarySampleCacheKey const& a,
                                         PrimarySampleCacheKey const& b) noexcept
        {
            return a.widthDips == b.widthDips &&
                   a.heightDips == b.heightDips &&
                   a.radiusDips == b.radiusDips &&
                   a.dpiScale == b.dpiScale &&
                   a.strain == b.strain &&
                   a.progress == b.progress &&
                   a.columns == b.columns &&
                   a.rows == b.rows &&
                   a.length == b.length;
        }

        inline void Clear(SceneMesh& mesh) noexcept
        {
            mesh.positions.clear();
            mesh.normals.clear();
            mesh.uvs.clear();
            mesh.primaryBodySamples.clear();
            mesh.indices.clear();
            mesh.columns = 0;
            mesh.rows = 0;
            mesh.gridStep = 0;
            mesh.zoom = 0.0f;
            mesh.bend = 0.0f;
            mesh.ior = 0.0f;
            mesh.bandPx = 0.0f;
            mesh.primaryZoomBandPx = 0.0f;
            mesh.geometryWidthDips = 0.0f;
            mesh.geometryHeightDips = 0.0f;
            mesh.geometryValid = false;
            mesh.primarySampleKey = {};
            mesh.primarySampleCacheValid = false;
        }
    }

    // The single public geometry endpoint. The returned UVs address the full
    // source surface; bodySourceXPx/bodySourceYPx are only its input body ROI.
    // Warped samples may move into that ROI, while clamping is deliberately
    // limited to the full source canvas so ROI edges do not flatten the lens.
    // Reusing output retains position/normal/index storage for UV-only animation.
    inline BuildStatus BuildSceneMesh(Frame const& frame, SceneMesh& output) noexcept
    {
        if (!detail::ValidateFrame(frame))
        {
            detail::Clear(output);
            return BuildStatus::invalidFrame;
        }

        // The center must represent the same screen position as the window.
        // Scaling the entire source also moves text and window edges in the
        // supposedly flat interior by several pixels in every widget form.
        constexpr float zoom = 1.0f;
        const float radius = gd::SurfaceRadius(frame.widthDips, frame.heightDips,
                                               frame.radiusDips, frame.strain);
        const float primaryZoomBandPx = detail::PrimaryBandDips(
            frame.widthDips, frame.heightDips, frame.progress) * frame.dpiScale;
        const float bandPx = detail::SupportedPrimaryBandDips(
            frame.widthDips, frame.heightDips, radius, frame.progress) * frame.dpiScale;
        if (!detail::IsFinite(zoom) || !detail::IsFinite(radius) ||
            !detail::IsFinite(primaryZoomBandPx) || primaryZoomBandPx <= 0.0f ||
            !detail::IsFinite(bandPx) || bandPx <= 0.0f)
        {
            detail::Clear(output);
            return BuildStatus::invalidFrame;
        }

        std::uint32_t step = 1;
        std::uint32_t columns = 0;
        std::uint32_t rows = 0;
        std::uint64_t vertexCount = 0;
        std::uint64_t indexCount = 0;
        for (;; ++step)
        {
            columns = static_cast<std::uint32_t>(std::ceil(frame.widthDips / static_cast<float>(step)));
            rows = static_cast<std::uint32_t>(std::ceil(frame.heightDips / static_cast<float>(step)));
            vertexCount = static_cast<std::uint64_t>(columns + 1u) * (rows + 1u);
            indexCount = static_cast<std::uint64_t>(columns) * rows * 6u;
            if (vertexCount <= detail::kMaxVertices && indexCount <= detail::kMaxIndices)
                break;
            if (step >= 1024u)
            {
                detail::Clear(output);
                return BuildStatus::capacityExceeded;
            }
        }

        const bool cachedMeshValid = output.geometryValid &&
            output.positions.size() == vertexCount && output.normals.size() == vertexCount &&
            output.uvs.size() == vertexCount && output.indices.size() == indexCount;
        const bool reusePositions = cachedMeshValid &&
            output.columns == columns && output.rows == rows &&
            output.geometryWidthDips == frame.widthDips &&
            output.geometryHeightDips == frame.heightDips;
        const bool reuseNormals = cachedMeshValid && output.columns == columns && output.rows == rows;
        const bool reuseIndices = cachedMeshValid && output.columns == columns && output.rows == rows;
        // Both profile bands are derived only from the inputs captured here:
        // dimensions, corner inputs, strain, DPI and progress.
        const PrimarySampleCacheKey primarySampleKey{
            frame.widthDips,
            frame.heightDips,
            frame.radiusDips,
            frame.dpiScale,
            frame.strain,
            frame.progress,
            columns,
            rows,
            vertexCount,
        };
        const bool reusePrimarySamples = output.primarySampleCacheValid &&
            output.primaryBodySamples.size() == vertexCount &&
            detail::SamePrimarySampleKey(output.primarySampleKey, primarySampleKey);
#if defined(GD_SYSTEMGLASS_GEOMETRY_TESTING)
        if (reusePrimarySamples)
            ++output.primarySampleCacheHitsForTest;
        else
            ++output.primarySampleCacheMissesForTest;
#endif
        if (!reusePrimarySamples)
            output.primarySampleCacheValid = false;

        output.columns = columns;
        output.rows = rows;
        output.gridStep = step;
        output.zoom = zoom;
        output.bend = detail::kBend;
        output.ior = detail::kIor;
        output.bandPx = bandPx;
        output.primaryZoomBandPx = primaryZoomBandPx;

        try
        {
            if (!reusePositions)
                output.positions.resize(static_cast<std::size_t>(vertexCount));
            if (!reuseNormals)
                output.normals.resize(static_cast<std::size_t>(vertexCount));
            output.uvs.resize(static_cast<std::size_t>(vertexCount));
            if (!reusePrimarySamples)
                output.primaryBodySamples.resize(static_cast<std::size_t>(vertexCount));
            if (!reuseIndices)
                output.indices.resize(static_cast<std::size_t>(indexCount));
        }
        catch (...)
        {
            detail::Clear(output);
            return BuildStatus::allocationFailure;
        }

        const float tensionRadius = radius;
        const float lensAaDips = 0.45f / frame.dpiScale;

        for (std::uint32_t row = 0; row <= rows; ++row)
        {
            const float y = frame.heightDips * static_cast<float>(row) / static_cast<float>(rows);
            for (std::uint32_t column = 0; column <= columns; ++column)
            {
                const float x = frame.widthDips * static_cast<float>(column) / static_cast<float>(columns);
                const std::size_t index = static_cast<std::size_t>(row) * (columns + 1u) + column;
                if (!reusePositions)
                {
                    const Float3 position{ x, y, 0.0f };
                    if (!detail::IsFinite(position))
                    {
                        detail::Clear(output);
                        return BuildStatus::nonFiniteGeometry;
                    }
                    output.positions[index] = position;
                }
                if (!reuseNormals)
                {
                    output.normals[index] = { 0.0f, 0.0f, -1.0f };
                }

                Float2 primaryPoint{};
                Float2 uv{};
                if (reusePrimarySamples)
                {
                    primaryPoint = output.primaryBodySamples[index];
                }
                else
                {
                    if (!detail::RefractedSample(frame, output, tensionRadius, x, y, primaryPoint))
                    {
                        detail::Clear(output);
                        return BuildStatus::nonFiniteGeometry;
                    }
                    output.primaryBodySamples[index] = primaryPoint;
                }
                if (!detail::FullSourceUv(frame, primaryPoint, uv))
                {
                    detail::Clear(output);
                    return BuildStatus::nonFiniteGeometry;
                }

                for (std::uint32_t lensIndex = 0; lensIndex < frame.lensCount; ++lensIndex)
                {
                    LensRegion const& lens = frame.lenses[lensIndex];
                    if (lens.opacity <= 0.0f ||
                        std::abs(x - lens.centerXDips) > lens.widthDips * 0.5f + lensAaDips ||
                        std::abs(y - lens.centerYDips) > lens.heightDips * 0.5f + lensAaDips)
                        continue;

                    const float distance = detail::LensDistance(lens, x, y);
                    const float coverage = 1.0f - detail::Phase(distance, -lensAaDips, lensAaDips);
                    const float blend = detail::Saturate(coverage * lens.opacity);
                    if (!detail::IsFinite(blend) || blend <= 0.0f)
                        continue;

                    Float2 secondaryPoint{};
                    Float2 lowerBodyPoint{};
                    Float2 secondaryUv{};
                    if (!detail::SecondaryLensSample(frame, lens, x, y, secondaryPoint) ||
                        !detail::RefractedSample(frame, output, tensionRadius,
                                                secondaryPoint.x, secondaryPoint.y, lowerBodyPoint) ||
                        !detail::FullSourceUv(frame, lowerBodyPoint, secondaryUv))
                    {
                        detail::Clear(output);
                        return BuildStatus::nonFiniteGeometry;
                    }

                    // The upper lens samples the lower glass: first reverse-map
                    // its local capsule, then apply the primary-body refraction.
                    uv.x += (secondaryUv.x - uv.x) * blend;
                    uv.y += (secondaryUv.y - uv.y) * blend;
                }

                if (!detail::IsFinite(uv) || uv.x < 0.0f || uv.x > 1.0f ||
                    uv.y < 0.0f || uv.y > 1.0f)
                {
                    detail::Clear(output);
                    return BuildStatus::nonFiniteGeometry;
                }
                output.uvs[index] = uv;
            }
        }

        if (!reuseIndices)
        {
            std::size_t indexCursor = 0;
            for (std::uint32_t row = 0; row < rows; ++row)
            {
                for (std::uint32_t column = 0; column < columns; ++column)
                {
                    const std::uint32_t topLeft = row * (columns + 1u) + column;
                    const std::uint32_t topRight = topLeft + 1u;
                    const std::uint32_t bottomLeft = topLeft + columns + 1u;
                    const std::uint32_t bottomRight = bottomLeft + 1u;

                    // The local y axis points down, so this winding matches the -Z normals.
                    output.indices[indexCursor++] = static_cast<std::uint16_t>(topLeft);
                    output.indices[indexCursor++] = static_cast<std::uint16_t>(bottomLeft);
                    output.indices[indexCursor++] = static_cast<std::uint16_t>(topRight);
                    output.indices[indexCursor++] = static_cast<std::uint16_t>(topRight);
                    output.indices[indexCursor++] = static_cast<std::uint16_t>(bottomLeft);
                    output.indices[indexCursor++] = static_cast<std::uint16_t>(bottomRight);
                }
            }
        }

        if (output.positions.size() != vertexCount || output.normals.size() != vertexCount ||
            output.uvs.size() != vertexCount || output.indices.size() != indexCount ||
            output.positions.size() > detail::kMaxVertices || output.indices.size() > detail::kMaxIndices)
        {
            detail::Clear(output);
            return BuildStatus::capacityExceeded;
        }
        if (!reuseNormals)
        {
            for (Float3 const& normal : output.normals)
            {
                if (!detail::IsFinite(normal) || normal.x != 0.0f || normal.y != 0.0f || normal.z != -1.0f)
                {
                    detail::Clear(output);
                    return BuildStatus::nonFiniteGeometry;
                }
            }
        }
        if (!reuseIndices)
        {
            for (std::uint16_t index : output.indices)
            {
                if (static_cast<std::size_t>(index) >= output.positions.size())
                {
                    detail::Clear(output);
                    return BuildStatus::nonFiniteGeometry;
                }
            }
        }
        output.geometryWidthDips = frame.widthDips;
        output.geometryHeightDips = frame.heightDips;
        output.geometryValid = true;
        if (!reusePrimarySamples)
        {
            output.primarySampleKey = primarySampleKey;
            output.primarySampleCacheValid = true;
        }
        return BuildStatus::success;
    }
}

#pragma once

#include "SurfaceTension.h"

#include <algorithm>
#include <cmath>
#include <cstddef>
#include <cstdint>
#include <limits>

namespace gd::shadow {

// A shadow DIB is app-generated decoration. These limits bound its allocation;
// they do not describe or constrain the shared desktop source.
inline constexpr std::uint32_t kMaximumDimension = 8192;
inline constexpr std::uint64_t kMaximumPixels = 16ull * 1024ull * 1024ull;
inline constexpr std::uint64_t kBytesPerPixel = 4;
inline constexpr std::uint64_t kMaximumBytes = kMaximumPixels * kBytesPerPixel;

struct ProfileDips {
    float sigma{};
    float offset{};
    float peakAlpha{};
    float margin{};
};

struct RasterProfile {
    float sigmaPixels{};
    float offsetPixels{};
    float peakAlpha{};
    std::uint32_t marginPixels{};
};

// Interpolate by geometric mean of the card area. The endpoints match the
// 200x52 DIP capsule and 360x476 DIP full Details card. Smoothstep avoids a
// visible parameter-rate change while the card morphs between those sizes.
inline bool MakeProfileDips(float widthDips, float heightDips, ProfileDips& out) noexcept
{
    out = {};
    if (!std::isfinite(widthDips) || !std::isfinite(heightDips)
        || widthDips <= 0.0f || heightDips <= 0.0f) return false;

    const double footprint = std::sqrt(static_cast<double>(widthDips) * heightDips);
    const double capsuleFootprint = std::sqrt(200.0 * 52.0);
    const double detailsFootprint = std::sqrt(360.0 * 476.0);
    const double u = std::clamp((footprint - capsuleFootprint)
        / (detailsFootprint - capsuleFootprint), 0.0, 1.0);
    const float t = static_cast<float>(u * u * (3.0 - 2.0 * u));

    out.sigma = 8.0f + 6.0f * t;
    out.offset = 4.0f + 3.0f * t;
    out.peakAlpha = 24.0f + 10.0f * t;
    out.margin = 3.5f * out.sigma + out.offset;
    return true;
}

inline bool MakeRasterProfile(float widthDips, float heightDips, float scale,
    RasterProfile& out) noexcept
{
    out = {};
    ProfileDips dips{};
    if (!MakeProfileDips(widthDips, heightDips, dips)
        || !std::isfinite(scale) || scale <= 0.0f) return false;

    const double sigma = static_cast<double>(dips.sigma) * scale;
    const double offset = static_cast<double>(dips.offset) * scale;
    if (!std::isfinite(sigma) || !std::isfinite(offset)
        || sigma <= 0.0 || offset < 0.0
        || sigma > std::numeric_limits<float>::max() || offset > std::numeric_limits<float>::max()) return false;

    const float sigmaPixels = static_cast<float>(sigma);
    const float offsetPixels = static_cast<float>(offset);
    const double margin = std::ceil(3.5 * static_cast<double>(sigmaPixels) + offsetPixels);
    if (!std::isfinite(margin) || margin < 1.0
        || margin > std::numeric_limits<std::uint32_t>::max()) return false;

    out.sigmaPixels = sigmaPixels;
    out.offsetPixels = offsetPixels;
    out.peakAlpha = dips.peakAlpha;
    out.marginPixels = static_cast<std::uint32_t>(margin);
    return std::isfinite(out.sigmaPixels) && std::isfinite(out.offsetPixels);
}

struct Capacity {
    std::uint32_t width{};
    std::uint32_t height{};
};

struct CapacityPlan {
    Capacity capacity{};
    std::uint64_t byteCount{};
    bool reallocate{};
};

inline bool FitsCapacity(Capacity capacity) noexcept
{
    if (!capacity.width || !capacity.height
        || capacity.width > kMaximumDimension || capacity.height > kMaximumDimension) return false;
    const std::uint64_t pixels = static_cast<std::uint64_t>(capacity.width) * capacity.height;
    return pixels <= kMaximumPixels && pixels * kBytesPerPixel <= kMaximumBytes;
}

inline std::uint32_t GrowDimension(std::uint32_t current, std::uint32_t requested) noexcept
{
    std::uint64_t grown = requested;
    const std::uint64_t increment = current
        ? std::max<std::uint64_t>(64u, static_cast<std::uint64_t>(current) / 2u)
        : std::max<std::uint64_t>(64u, static_cast<std::uint64_t>(requested) / 2u);
    grown = std::max(grown, static_cast<std::uint64_t>(current) + increment);
    grown = std::min<std::uint64_t>(grown, kMaximumDimension);
    return static_cast<std::uint32_t>(grown);
}

// Preserve both existing dimensions on shrink. Growth reserves up to 1.5x (or
// at least 64px) so small morph steps do not force a new DIB each frame. If
// reserve slack would exceed the budget, use the exact required capacity.
inline bool PlanCapacity(Capacity current, std::uint32_t requestedWidth,
    std::uint32_t requestedHeight, CapacityPlan& out) noexcept
{
    out = {};
    if (!requestedWidth || !requestedHeight
        || requestedWidth > kMaximumDimension || requestedHeight > kMaximumDimension) return false;
    if ((current.width == 0) != (current.height == 0)) return false;
    if (current.width && !FitsCapacity(current)) return false;

    const Capacity required{
        std::max(current.width, requestedWidth),
        std::max(current.height, requestedHeight)
    };
    if (!FitsCapacity(required)) return false;

    Capacity target = current;
    if (!current.width) {
        target = {GrowDimension(0, requestedWidth), GrowDimension(0, requestedHeight)};
    } else {
        if (requestedWidth > current.width)
            target.width = GrowDimension(current.width, requestedWidth);
        if (requestedHeight > current.height)
            target.height = GrowDimension(current.height, requestedHeight);
    }

    if (!FitsCapacity(target)) target = required;
    if (!FitsCapacity(target)) return false;

    out.capacity = target;
    out.byteCount = static_cast<std::uint64_t>(target.width) * target.height * kBytesPerPixel;
    out.reallocate = target.width != current.width || target.height != current.height;
    return true;
}

struct RasterStats {
    std::uint64_t totalPixels{};
    std::uint64_t interiorPixelsSkipped{};
    std::uint64_t gaussianTailPixelsSkipped{};
    std::uint64_t outsidePixelsVisited{};
    std::uint64_t spanDistanceEvaluations{};
};

namespace detail {
inline float DistanceAt(std::uint32_t x, std::uint32_t y, std::uint32_t margin,
    std::uint32_t panelWidth, std::uint32_t panelHeight, float radius, float scale,
    float tension, float yOffset = 0.0f) noexcept
{
    const float localX = static_cast<float>(x) - static_cast<float>(margin) + 0.5f;
    const float localY = static_cast<float>(y) - static_cast<float>(margin) + 0.5f - yOffset;
    return gd::SurfaceDistance(localX, localY, static_cast<float>(panelWidth),
        static_cast<float>(panelHeight), radius, scale, tension);
}

inline std::uint32_t AlphaForDistance(float signedDistance,
    const RasterProfile& profile) noexcept
{
    const float distance = std::max(0.0f, signedDistance);
    const float denominator = 2.0f * profile.sigmaPixels * profile.sigmaPixels;
    return static_cast<std::uint32_t>(std::lround(profile.peakAlpha
        * std::exp(-(distance * distance) / denominator)));
}

inline float ConservativeAlphaCutoff(const RasterProfile& profile) noexcept
{
    double exactCutoff = 0.0;
    if (profile.peakAlpha > 0.5f) {
        exactCutoff = static_cast<double>(profile.sigmaPixels)
            * std::sqrt(2.0 * std::log(2.0 * static_cast<double>(profile.peakAlpha)));
    }
    const float cutoff = static_cast<float>(exactCutoff);
    // Keep a full 1% sigma of extra pixels, plus one representable float step.
    // This is deliberately outward: the exact alpha-at-boundary check below
    // also guards against device/compiler libm rounding differences.
    const float guard = std::max(0.01f, profile.sigmaPixels * 0.01f);
    return std::nextafter(cutoff + guard, std::numeric_limits<float>::infinity());
}

enum class AlphaSpanResult : std::uint8_t { noVisibleAlpha, spanFound, uncertain };

// Find the x interval whose shifted SDF can produce a rounded alpha above zero.
// The analytic Gaussian threshold is widened; then the immediate boundaries
// are checked with the exact SDF and exact lround/exp path used by RasterAlpha.
// If the center samples disagree or the boundary cannot be established, return
// uncertain so the caller falls back to the untrimmed scalar region.
inline AlphaSpanResult FindVisibleAlphaSpan(std::uint32_t y, std::uint32_t width,
    std::uint32_t panelWidth, std::uint32_t panelHeight, const RasterProfile& profile,
    float radius, float scale, float tension, std::uint32_t& first,
    std::uint32_t& last, std::uint64_t& distanceEvaluations) noexcept
{
    const std::uint32_t centerLeft = (width - 1u) / 2u;
    const std::uint32_t centerRight = width - 1u - centerLeft;
    const float cutoff = ConservativeAlphaCutoff(profile);
    auto sampleDistance = [&](std::uint32_t x) noexcept {
        ++distanceEvaluations;
        return DistanceAt(x, y, profile.marginPixels, panelWidth, panelHeight,
            radius, scale, tension, profile.offsetPixels);
    };
    auto sampleAlpha = [&](std::uint32_t x) noexcept {
        return AlphaForDistance(sampleDistance(x), profile);
    };

    const float centerLeftDistance = sampleDistance(centerLeft);
    const float centerRightDistance = centerRight == centerLeft
        ? centerLeftDistance : sampleDistance(centerRight);
    if (!std::isfinite(centerLeftDistance) || !std::isfinite(centerRightDistance))
        return AlphaSpanResult::uncertain;
    const bool leftInsideCutoff = centerLeftDistance <= cutoff;
    const bool rightInsideCutoff = centerRightDistance <= cutoff;
    if (leftInsideCutoff != rightInsideCutoff) return AlphaSpanResult::uncertain;
    if (!leftInsideCutoff) {
        return sampleAlpha(centerLeft) == 0 && sampleAlpha(centerRight) == 0
            ? AlphaSpanResult::noVisibleAlpha : AlphaSpanResult::uncertain;
    }

    std::uint32_t outer = 0;
    std::uint32_t inner = centerLeft;
    const float leftEdgeDistance = sampleDistance(0);
    if (!std::isfinite(leftEdgeDistance)) return AlphaSpanResult::uncertain;
    if (leftEdgeDistance <= cutoff) {
        first = 0;
    } else {
        while (inner - outer > 1u) {
            const std::uint32_t middle = outer + (inner - outer) / 2u;
            const float distance = sampleDistance(middle);
            if (!std::isfinite(distance)) return AlphaSpanResult::uncertain;
            if (distance <= cutoff) inner = middle;
            else outer = middle;
        }
        first = inner;
    }

    inner = centerRight;
    outer = width - 1u;
    const float rightEdgeDistance = sampleDistance(width - 1u);
    if (!std::isfinite(rightEdgeDistance)) return AlphaSpanResult::uncertain;
    if (rightEdgeDistance <= cutoff) {
        last = width - 1u;
    } else {
        while (outer - inner > 1u) {
            const std::uint32_t middle = inner + (outer - inner) / 2u;
            const float distance = sampleDistance(middle);
            if (!std::isfinite(distance)) return AlphaSpanResult::uncertain;
            if (distance <= cutoff) inner = middle;
            else outer = middle;
        }
        last = inner;
    }

    while (first <= last && sampleAlpha(first) == 0) ++first;
    if (first > last) return AlphaSpanResult::noVisibleAlpha;
    while (first < last && sampleAlpha(last) == 0) --last;
    if (first == last && sampleAlpha(last) == 0) return AlphaSpanResult::noVisibleAlpha;
    if (first > last) return AlphaSpanResult::noVisibleAlpha;

    while (first > 0u && sampleAlpha(first - 1u) != 0u) --first;
    while (last + 1u < width && sampleAlpha(last + 1u) != 0u) ++last;
    if ((first > 0u && sampleAlpha(first - 1u) != 0u)
        || (last + 1u < width && sampleAlpha(last + 1u) != 0u))
        return AlphaSpanResult::uncertain;
    return AlphaSpanResult::spanFound;
}

// For a fixed row, TensionFactor depends only on y and remains positive for the
// supported strain range. SurfaceDistance is therefore monotone from each
// outer edge toward the x-center. Search both sides with the exact shared SDF;
// the returned discrete span contains only samples whose SDF is <= 0.
inline bool FindInteriorSpan(std::uint32_t y, std::uint32_t width,
    std::uint32_t panelWidth, std::uint32_t panelHeight, const RasterProfile& profile,
    float radius, float scale, float tension, std::uint32_t& first,
    std::uint32_t& last, std::uint64_t& distanceEvaluations) noexcept
{
    const std::uint32_t centerLeft = (width - 1u) / 2u;
    const std::uint32_t centerRight = width - 1u - centerLeft;
    const float leftCenterDistance = DistanceAt(centerLeft, y, profile.marginPixels,
        panelWidth, panelHeight, radius, scale, tension);
    ++distanceEvaluations;
    const float rightCenterDistance = centerRight == centerLeft ? leftCenterDistance
        : DistanceAt(centerRight, y, profile.marginPixels, panelWidth,
            panelHeight, radius, scale, tension);
    if (centerRight != centerLeft) ++distanceEvaluations;
    if (leftCenterDistance > 0.0f || rightCenterDistance > 0.0f) return false;

    std::uint32_t outside = 0;
    std::uint32_t inside = centerLeft;
    while (inside - outside > 1u) {
        const std::uint32_t middle = outside + (inside - outside) / 2u;
        const float distance = DistanceAt(middle, y, profile.marginPixels,
            panelWidth, panelHeight, radius, scale, tension);
        ++distanceEvaluations;
        if (distance <= 0.0f) inside = middle;
        else outside = middle;
    }
    first = inside;

    inside = centerRight;
    outside = width - 1u;
    while (outside - inside > 1u) {
        const std::uint32_t middle = inside + (outside - inside) / 2u;
        const float distance = DistanceAt(middle, y, profile.marginPixels,
            panelWidth, panelHeight, radius, scale, tension);
        ++distanceEvaluations;
        if (distance <= 0.0f) inside = middle;
        else outside = middle;
    }
    last = inside;
    return true;
}
}

inline bool RasterAlpha(std::uint32_t* pixels, std::uint32_t stridePixels,
    std::uint32_t capacityHeight, std::uint32_t width, std::uint32_t height,
    std::uint32_t panelWidth, std::uint32_t panelHeight, const RasterProfile& profile,
    float radius, float scale, float tension, RasterStats* stats = nullptr) noexcept
{
    if (stats) *stats = {};
    if (!pixels || !width || !height || !panelWidth || !panelHeight
        || stridePixels < width || capacityHeight < height
        || stridePixels > kMaximumDimension || capacityHeight > kMaximumDimension
        || static_cast<std::uint64_t>(stridePixels) * capacityHeight > kMaximumPixels)
        return false;
    const std::uint64_t expectedWidth = static_cast<std::uint64_t>(panelWidth)
        + static_cast<std::uint64_t>(profile.marginPixels) * 2u;
    const std::uint64_t expectedHeight = static_cast<std::uint64_t>(panelHeight)
        + static_cast<std::uint64_t>(profile.marginPixels) * 2u;
    if (!profile.marginPixels || expectedWidth != width || expectedHeight != height
        || panelWidth > kMaximumDimension || panelHeight > kMaximumDimension
        || !std::isfinite(profile.sigmaPixels) || profile.sigmaPixels <= 0.0f
        || !std::isfinite(profile.offsetPixels) || profile.offsetPixels < 0.0f
        || !std::isfinite(profile.peakAlpha) || profile.peakAlpha < 0.0f
        || profile.peakAlpha > 255.0f || !std::isfinite(radius) || radius < 0.0f
        || radius > std::min(static_cast<float>(panelWidth),
            static_cast<float>(panelHeight)) * 0.5f + 1e-4f
        || !std::isfinite(scale) || scale <= 0.0f || !std::isfinite(tension)
        || std::abs(tension) > 1.15f) return false;
    const double minimumMargin = 3.5 * profile.sigmaPixels + profile.offsetPixels;
    if (static_cast<double>(profile.marginPixels) + 1e-5 < minimumMargin) return false;

    RasterStats localStats{};
    localStats.totalPixels = static_cast<std::uint64_t>(width) * height;
    auto rasterRange = [&](std::uint32_t y, std::uint32_t begin, std::uint32_t end) noexcept {
        if (begin > end) return;
        auto* row = pixels + static_cast<std::size_t>(y) * stridePixels;
        for (std::uint32_t x = begin; x <= end; ++x) {
            ++localStats.outsidePixelsVisited;
            const float baseDistance = detail::DistanceAt(x, y, profile.marginPixels,
                panelWidth, panelHeight, radius, scale, tension);
            if (baseDistance <= 0.0f) continue;
            const float localX = static_cast<float>(x) - static_cast<float>(profile.marginPixels) + 0.5f;
            const float localY = static_cast<float>(y) - static_cast<float>(profile.marginPixels) + 0.5f;
            const float shiftedDistance = gd::SurfaceDistance(localX,
                localY - profile.offsetPixels, static_cast<float>(panelWidth),
                static_cast<float>(panelHeight), radius, scale, tension);
            const auto alpha = detail::AlphaForDistance(shiftedDistance, profile);
            row[x] = alpha << 24; // Premultiplied black with transparent interior.
        }
    };

    for (std::uint32_t y = 0; y < height; ++y) {
        auto* row = pixels + static_cast<std::size_t>(y) * stridePixels;
        std::fill(row, row + width, 0u);
        const std::uint64_t visitedBeforeRow = localStats.outsidePixelsVisited;

        std::uint32_t first{}, last{};
        const bool hasInterior = detail::FindInteriorSpan(y, width, panelWidth,
            panelHeight, profile, radius, scale, tension, first, last,
            localStats.spanDistanceEvaluations);
        const std::uint64_t interiorCount = hasInterior
            ? static_cast<std::uint64_t>(last) - first + 1u : 0u;
        localStats.interiorPixelsSkipped += interiorCount;

        std::uint32_t visibleFirst{}, visibleLast{};
        const auto alphaSpan = detail::FindVisibleAlphaSpan(y, width, panelWidth,
            panelHeight, profile, radius, scale, tension, visibleFirst, visibleLast,
            localStats.spanDistanceEvaluations);
        if (alphaSpan == detail::AlphaSpanResult::spanFound) {
            if (hasInterior) {
                if (first > 0u) {
                    const std::uint32_t end = std::min(visibleLast, first - 1u);
                    if (visibleFirst <= end) rasterRange(y, visibleFirst, end);
                }
                if (last + 1u < width) {
                    const std::uint32_t begin = std::max(visibleFirst, last + 1u);
                    if (begin <= visibleLast) rasterRange(y, begin, visibleLast);
                }
            } else {
                rasterRange(y, visibleFirst, visibleLast);
            }
        } else if (alphaSpan == detail::AlphaSpanResult::uncertain) {
            if (hasInterior) {
                if (first > 0u) rasterRange(y, 0u, first - 1u);
                if (last + 1u < width) rasterRange(y, last + 1u, width - 1u);
            } else {
                rasterRange(y, 0u, width - 1u);
            }
        }
        const std::uint64_t visitedThisRow = localStats.outsidePixelsVisited - visitedBeforeRow;
        localStats.gaussianTailPixelsSkipped += static_cast<std::uint64_t>(width)
            - interiorCount - visitedThisRow;
    }

    if (stats) *stats = localStats;
    return true;
}

} // namespace gd::shadow

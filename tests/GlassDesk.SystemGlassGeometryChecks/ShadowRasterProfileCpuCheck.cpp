#include <ShadowRasterProfile.h>

#include <algorithm>
#include <array>
#include <chrono>
#include <cmath>
#include <cstdint>
#include <cstdio>
#include <limits>
#include <vector>

namespace {
using gd::shadow::Capacity;
using gd::shadow::CapacityPlan;
using gd::shadow::ProfileDips;
using gd::shadow::RasterProfile;
using gd::shadow::RasterStats;

constexpr std::uint32_t kSentinel = 0xdeadbeefu;

struct RasterCase {
    const char* name;
    float widthDips;
    float heightDips;
    float scale;
    float strain;
};

bool RasterReference(std::uint32_t* pixels, std::uint32_t stride,
    std::uint32_t width, std::uint32_t height, std::uint32_t panelWidth,
    std::uint32_t panelHeight, const RasterProfile& profile, float radius,
    float scale, float tension)
{
    if (!pixels || !stride || !height) return false;
    const float denominator = 2.0f * profile.sigmaPixels * profile.sigmaPixels;
    for (std::uint32_t y = 0; y < height; ++y) {
        auto* row = pixels + static_cast<std::size_t>(y) * stride;
        std::fill(row, row + width, 0u);
        for (std::uint32_t x = 0; x < width; ++x) {
            const float localX = static_cast<float>(x) - static_cast<float>(profile.marginPixels) + 0.5f;
            const float localY = static_cast<float>(y) - static_cast<float>(profile.marginPixels) + 0.5f;
            const float baseDistance = gd::SurfaceDistance(localX, localY,
                static_cast<float>(panelWidth), static_cast<float>(panelHeight),
                radius, scale, tension);
            if (baseDistance <= 0.0f) continue;
            const float shiftedDistance = gd::SurfaceDistance(localX,
                localY - profile.offsetPixels, static_cast<float>(panelWidth),
                static_cast<float>(panelHeight), radius, scale, tension);
            const float distance = std::max(0.0f, shiftedDistance);
            const auto alpha = static_cast<std::uint32_t>(std::lround(profile.peakAlpha
                * std::exp(-(distance * distance) / denominator)));
            row[x] = alpha << 24;
        }
    }
    return true;
}

bool SameActivePixels(const std::vector<std::uint32_t>& a,
    const std::vector<std::uint32_t>& b, std::uint32_t stride,
    std::uint32_t width, std::uint32_t height)
{
    for (std::uint32_t y = 0; y < height; ++y) {
        const auto row = static_cast<std::size_t>(y) * stride;
        for (std::uint32_t x = 0; x < width; ++x) {
            if (a[row + x] != b[row + x]) return false;
        }
    }
    return true;
}

bool PaddingUntouched(const std::vector<std::uint32_t>& pixels,
    std::uint32_t stride, std::uint32_t capacityHeight,
    std::uint32_t width, std::uint32_t height)
{
    for (std::uint32_t y = 0; y < capacityHeight; ++y) {
        const auto row = static_cast<std::size_t>(y) * stride;
        const std::uint32_t begin = y < height ? width : 0u;
        for (std::uint32_t x = begin; x < stride; ++x) {
            if (pixels[row + x] != kSentinel) return false;
        }
    }
    return true;
}

bool IsSymmetric(const std::vector<std::uint32_t>& pixels,
    std::uint32_t stride, std::uint32_t width, std::uint32_t height)
{
    for (std::uint32_t y = 0; y < height; ++y) {
        const auto row = static_cast<std::size_t>(y) * stride;
        for (std::uint32_t x = 0; x < width / 2u; ++x) {
            const auto left = (pixels[row + x] >> 24) & 0xffu;
            const auto right = (pixels[row + width - 1u - x] >> 24) & 0xffu;
            if (left > right + 1u || right > left + 1u) return false;
        }
    }
    return true;
}

bool GeometryIsSymmetric(const RasterCase& test, std::uint32_t panelWidth,
    std::uint32_t panelHeight, const RasterProfile& profile, float radius,
    std::uint32_t width, std::uint32_t height)
{
    const std::uint32_t xStep = std::max(1u, width / 31u);
    const std::uint32_t yStep = std::max(1u, height / 29u);
    for (std::uint32_t y = 0; y < height; y += yStep) {
        const float localY = static_cast<float>(y) - static_cast<float>(profile.marginPixels) + 0.5f;
        for (std::uint32_t x = 0; x < width; x += xStep) {
            const std::uint32_t mirror = width - 1u - x;
            const float localX = static_cast<float>(x) - static_cast<float>(profile.marginPixels) + 0.5f;
            const float mirroredX = static_cast<float>(mirror)
                - static_cast<float>(profile.marginPixels) + 0.5f;
            const float left = gd::SurfaceDistance(localX, localY,
                static_cast<float>(panelWidth), static_cast<float>(panelHeight),
                radius, test.scale, test.strain);
            const float right = gd::SurfaceDistance(mirroredX, localY,
                static_cast<float>(panelWidth), static_cast<float>(panelHeight),
                radius, test.scale, test.strain);
            if (std::abs(left - right) > 0.0001f) return false;
        }
    }
    return true;
}

double Median(std::vector<double> values)
{
    std::sort(values.begin(), values.end());
    return values[values.size() / 2u];
}

template<typename TAction>
double TimeMilliseconds(TAction&& action)
{
    const auto start = std::chrono::steady_clock::now();
    action();
    const auto end = std::chrono::steady_clock::now();
    return std::chrono::duration<double, std::milli>(end - start).count();
}

bool VerifyRasterCase(const RasterCase& test, bool benchmark)
{
    RasterProfile profile{};
    if (!gd::shadow::MakeRasterProfile(test.widthDips, test.heightDips,
        test.scale, profile)) return false;

    const auto panelWidth = static_cast<std::uint32_t>(std::lround(test.widthDips * test.scale));
    const auto panelHeight = static_cast<std::uint32_t>(std::lround(test.heightDips * test.scale));
    const std::uint64_t width64 = static_cast<std::uint64_t>(panelWidth) + profile.marginPixels * 2ull;
    const std::uint64_t height64 = static_cast<std::uint64_t>(panelHeight) + profile.marginPixels * 2ull;
    if (!panelWidth || !panelHeight || width64 > gd::shadow::kMaximumDimension
        || height64 > gd::shadow::kMaximumDimension) return false;
    const auto width = static_cast<std::uint32_t>(width64);
    const auto height = static_cast<std::uint32_t>(height64);

    CapacityPlan planned{};
    if (!gd::shadow::PlanCapacity({}, width, height, planned)) return false;
    // Use the grow-only plan for the smaller cases to exercise a larger DIB
    // stride. Keep the largest case tight to limit this check's memory use.
    const bool useReservedCapacity = test.scale <= 1.5f;
    const Capacity capacity = useReservedCapacity ? planned.capacity : Capacity{width, height};
    const std::uint32_t stride = capacity.width;
    const std::uint32_t capacityHeight = capacity.height;
    const std::uint64_t storage = static_cast<std::uint64_t>(stride) * capacityHeight;
    if (storage > gd::shadow::kMaximumPixels) return false;

    const std::uint32_t preferredRadius = static_cast<std::uint32_t>(std::lround(26.0f * test.scale));
    const float radius = gd::SurfaceRadius(static_cast<float>(panelWidth),
        static_cast<float>(panelHeight), std::min(static_cast<float>(preferredRadius),
            std::min(static_cast<float>(panelWidth), static_cast<float>(panelHeight)) * 0.5f),
        test.strain);
    std::vector<std::uint32_t> reference(static_cast<std::size_t>(storage), kSentinel);
    std::vector<std::uint32_t> optimized(static_cast<std::size_t>(storage), kSentinel);
    RasterStats stats{};
    if (!RasterReference(reference.data(), stride, width, height, panelWidth,
        panelHeight, profile, radius, test.scale, test.strain)
        || !gd::shadow::RasterAlpha(optimized.data(), stride, capacityHeight,
            width, height, panelWidth, panelHeight, profile, radius, test.scale,
            test.strain, &stats)) return false;

    if (!SameActivePixels(reference, optimized, stride, width, height)) {
        for (std::uint32_t y = 0; y < height; ++y) {
            const auto row = static_cast<std::size_t>(y) * stride;
            for (std::uint32_t x = 0; x < width; ++x) {
                if (reference[row + x] != optimized[row + x]) {
                    std::fprintf(stderr, "%s mismatch at (%u,%u): ref=%08x opt=%08x spanSkipped=%llu\n",
                        test.name, x, y, reference[row + x], optimized[row + x],
                        static_cast<unsigned long long>(stats.interiorPixelsSkipped));
                    return false;
                }
            }
        }
        return false;
    }
    if (!PaddingUntouched(reference, stride, capacityHeight, width, height)) {
        std::fprintf(stderr, "%s reference overwrote padding\n", test.name);
        return false;
    }
    if (!PaddingUntouched(optimized, stride, capacityHeight, width, height)) {
        std::fprintf(stderr, "%s optimized raster overwrote padding\n", test.name);
        return false;
    }
    if (!IsSymmetric(optimized, stride, width, height)) {
        std::fprintf(stderr, "%s alpha output lost x symmetry\n", test.name);
        return false;
    }
    if (!GeometryIsSymmetric(test, panelWidth, panelHeight, profile, radius, width, height)) {
        std::fprintf(stderr, "%s SurfaceDistance symmetry check failed\n", test.name);
        return false;
    }
    if (stats.totalPixels != static_cast<std::uint64_t>(width) * height
        || stats.interiorPixelsSkipped + stats.gaussianTailPixelsSkipped
            + stats.outsidePixelsVisited != stats.totalPixels
        || !stats.interiorPixelsSkipped || !stats.gaussianTailPixelsSkipped) {
        std::fprintf(stderr, "%s span stats inconsistent\n", test.name);
        return false;
    }

    if (benchmark) {
        // Repeated timing is strictly CPU work on the same generated alpha
        // pixels. It says nothing about DIB allocation, GDI or GPU behavior.
        std::vector<double> referenceMs, optimizedMs;
        for (unsigned i = 0; i < 2; ++i) {
            RasterReference(reference.data(), stride, width, height, panelWidth,
                panelHeight, profile, radius, test.scale, test.strain);
            gd::shadow::RasterAlpha(optimized.data(), stride, capacityHeight,
                width, height, panelWidth, panelHeight, profile, radius,
                test.scale, test.strain);
        }
        for (unsigned i = 0; i < 5; ++i) {
            referenceMs.push_back(TimeMilliseconds([&] {
                RasterReference(reference.data(), stride, width, height, panelWidth,
                    panelHeight, profile, radius, test.scale, test.strain);
            }));
            optimizedMs.push_back(TimeMilliseconds([&] {
                gd::shadow::RasterAlpha(optimized.data(), stride, capacityHeight,
                    width, height, panelWidth, panelHeight, profile, radius,
                    test.scale, test.strain);
            }));
        }
        const double referenceMedian = Median(referenceMs);
        const double optimizedMedian = Median(optimizedMs);
        const double interiorPercent = 100.0 * static_cast<double>(stats.interiorPixelsSkipped)
            / static_cast<double>(stats.totalPixels);
        const double tailsPercent = 100.0 * static_cast<double>(stats.gaussianTailPixelsSkipped)
            / static_cast<double>(stats.totalPixels);
        std::printf("shadow %-15s %ux%u @ %.2fx strain %.2f: same pixels; interior %.1f%%, zero-alpha tails %.1f%% skipped; "
            "reference %.3f ms, optimized %.3f ms, %.2fx CPU; span SDF %llu\n",
            test.name, width, height, test.scale, test.strain, interiorPercent, tailsPercent,
            referenceMedian, optimizedMedian,
            optimizedMedian > 0.0 ? referenceMedian / optimizedMedian : 0.0,
            static_cast<unsigned long long>(stats.spanDistanceEvaluations));
    }
    return true;
}

bool CheckProfiles()
{
    ProfileDips capsule{}, details{};
    if (!gd::shadow::MakeProfileDips(200.0f, 52.0f, capsule)
        || !gd::shadow::MakeProfileDips(360.0f, 476.0f, details)) return false;
    if (std::abs(capsule.sigma - 8.0f) > 0.0001f
        || std::abs(capsule.offset - 4.0f) > 0.0001f
        || std::abs(capsule.peakAlpha - 24.0f) > 0.0001f
        || std::abs(details.sigma - 14.0f) > 0.0001f
        || std::abs(details.offset - 7.0f) > 0.0001f
        || std::abs(details.peakAlpha - 34.0f) > 0.0001f) return false;

    float previousSigma = 0.0f, previousOffset = 0.0f, previousAlpha = 0.0f;
    for (unsigned i = 0; i <= 100; ++i) {
        const float t = static_cast<float>(i) / 100.0f;
        const float width = 200.0f + (360.0f - 200.0f) * t;
        const float height = 52.0f + (476.0f - 52.0f) * t;
        ProfileDips profile{};
        if (!gd::shadow::MakeProfileDips(width, height, profile)
            || profile.sigma < previousSigma || profile.offset < previousOffset
            || profile.peakAlpha < previousAlpha
            || profile.margin + 0.0001f < 3.5f * profile.sigma + profile.offset) return false;
        previousSigma = profile.sigma;
        previousOffset = profile.offset;
        previousAlpha = profile.peakAlpha;
    }

    for (float scale : {0.5f, 1.0f, 1.25f, 1.5f, 2.0f, 4.0f}) {
        RasterProfile raster{};
        if (!gd::shadow::MakeRasterProfile(360.0f, 476.0f, scale, raster)
            || raster.marginPixels + 0.0001f < 3.5f * raster.sigmaPixels + raster.offsetPixels)
            return false;
    }

    ProfileDips rejected{};
    RasterProfile rejectedRaster{};
    if (gd::shadow::MakeProfileDips(0.0f, 52.0f, rejected)
        || gd::shadow::MakeProfileDips(std::numeric_limits<float>::quiet_NaN(), 52.0f, rejected)
        || gd::shadow::MakeRasterProfile(200.0f, 52.0f,
            std::numeric_limits<float>::infinity(), rejectedRaster)
        || gd::shadow::MakeRasterProfile(200.0f, 52.0f, -1.0f, rejectedRaster)) return false;
    return true;
}

bool CheckCapacityPlan()
{
    CapacityPlan initial{};
    if (!gd::shadow::PlanCapacity({}, 640, 300, initial)
        || !initial.reallocate || initial.capacity.width < 640
        || initial.capacity.height < 300 || !gd::shadow::FitsCapacity(initial.capacity)
        || initial.byteCount != static_cast<std::uint64_t>(initial.capacity.width)
            * initial.capacity.height * gd::shadow::kBytesPerPixel) return false;

    CapacityPlan shrink{};
    if (!gd::shadow::PlanCapacity(initial.capacity, 320, 150, shrink)
        || shrink.reallocate || shrink.capacity.width != initial.capacity.width
        || shrink.capacity.height != initial.capacity.height) return false;

    std::uint32_t reallocations = 0;
    Capacity capacity{};
    for (unsigned i = 0; i <= 200; ++i) {
        const float t = static_cast<float>(i) / 200.0f;
        const auto requestedWidth = static_cast<std::uint32_t>(std::lround(400.0f + 800.0f * t));
        const auto requestedHeight = static_cast<std::uint32_t>(std::lround(240.0f + 760.0f * t));
        CapacityPlan plan{};
        if (!gd::shadow::PlanCapacity(capacity, requestedWidth, requestedHeight, plan)
            || plan.capacity.width < requestedWidth || plan.capacity.height < requestedHeight
            || !gd::shadow::FitsCapacity(plan.capacity)) return false;
        if (plan.reallocate) ++reallocations;
        capacity = plan.capacity;
    }
    for (unsigned i = 0; i <= 200; ++i) {
        const float t = static_cast<float>(i) / 200.0f;
        const auto requestedWidth = static_cast<std::uint32_t>(std::lround(1200.0f - 800.0f * t));
        const auto requestedHeight = static_cast<std::uint32_t>(std::lround(1000.0f - 760.0f * t));
        CapacityPlan plan{};
        if (!gd::shadow::PlanCapacity(capacity, requestedWidth, requestedHeight, plan)
            || plan.reallocate || plan.capacity.width != capacity.width
            || plan.capacity.height != capacity.height) return false;
    }
    if (reallocations > 8u) return false;

    CapacityPlan exactBudget{};
    if (!gd::shadow::PlanCapacity({}, 8192, 2048, exactBudget)
        || exactBudget.capacity.width < 8192 || exactBudget.capacity.height < 2048
        || exactBudget.byteCount > gd::shadow::kMaximumBytes) return false;

    CapacityPlan rejected{};
    if (gd::shadow::PlanCapacity({}, 8192, 8192, rejected)
        || gd::shadow::PlanCapacity({}, 8193, 16, rejected)
        || gd::shadow::PlanCapacity({5000, 1000}, 1000, 4000, rejected)
        || gd::shadow::PlanCapacity({0, 100}, 100, 100, rejected)) return false;
    return true;
}

bool CheckRasterValidation()
{
    RasterProfile profile{};
    if (!gd::shadow::MakeRasterProfile(200.0f, 52.0f, 1.0f, profile)) return false;
    const std::uint32_t panelWidth = 200, panelHeight = 52;
    const std::uint32_t width = panelWidth + 2u * profile.marginPixels;
    const std::uint32_t height = panelHeight + 2u * profile.marginPixels;
    const float radius = gd::SurfaceRadius(200.0f, 52.0f, 26.0f, 0.0f);
    std::vector<std::uint32_t> pixels(static_cast<std::size_t>(width) * height, 0u);
    if (gd::shadow::RasterAlpha(nullptr, width, height, width, height,
        panelWidth, panelHeight, profile, radius, 1.0f, 0.0f)
        || gd::shadow::RasterAlpha(pixels.data(), width - 1u, height, width, height,
            panelWidth, panelHeight, profile, radius, 1.0f, 0.0f)
        || gd::shadow::RasterAlpha(pixels.data(), width, height, width - 1u, height,
            panelWidth, panelHeight, profile, radius, 1.0f, 0.0f)
        || gd::shadow::RasterAlpha(pixels.data(), width, height, width, height,
            panelWidth, panelHeight, profile, 1000.0f, 1.0f, 0.0f)
        || gd::shadow::RasterAlpha(pixels.data(), width, height, width, height,
            panelWidth, panelHeight, profile, radius, 1.0f, 1.16f)) return false;
    return true;
}
}

int main()
{
    if (!CheckProfiles()) return 1;
    if (!CheckCapacityPlan()) return 2;
    if (!CheckRasterValidation()) return 3;

    constexpr std::array<RasterCase, 6> cases{{
        {"capsule-dpi-half", 200.0f, 52.0f, 0.5f, 0.0f},
        {"capsule-normal", 200.0f, 52.0f, 1.0f, 0.0f},
        {"peek-strained", 240.0f, 144.0f, 1.5f, 0.7f},
        {"details-dpi15", 360.0f, 476.0f, 1.5f, -1.15f},
        {"details-dpi2", 360.0f, 476.0f, 2.0f, -1.15f},
        {"details-dpi4", 360.0f, 476.0f, 4.0f, 1.15f}
    }};
    for (std::size_t i = 0; i < cases.size(); ++i) {
        if (!VerifyRasterCase(cases[i], i >= cases.size() - 3u)) {
            std::fprintf(stderr, "FAIL: raster equivalence/profile/symmetry: %s\n", cases[i].name);
            return 4;
        }
    }

    std::printf("PASS: profile endpoints/smoothness, grow-only bounded capacity, "
        "x-symmetric SurfaceDistance spans, exact scalar-vs-optimized alpha on %u DPI/strain cases; CPU only\n",
        static_cast<unsigned>(cases.size()));
    return 0;
}

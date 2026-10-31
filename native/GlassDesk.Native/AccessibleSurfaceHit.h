#pragma once

// Pure geometry used by the native UIA point-hit candidate. It owns only the
// current presented silhouette inputs and shares the pointer path's SDF.
#include "SurfaceTension.h"

#include <cmath>
#include <limits>

namespace gd::input {

struct AccessibleSurfaceShape {
    float widthDips{};
    float heightDips{};
    float radiusDips{};
    float effectiveTension{};
};

inline bool AccessibleSurfaceContainsDips(AccessibleSurfaceShape const& shape,
                                         float dipX, float dipY) noexcept {
    if (!std::isfinite(shape.widthDips) || !std::isfinite(shape.heightDips) ||
        !std::isfinite(shape.radiusDips) || !std::isfinite(shape.effectiveTension) ||
        !std::isfinite(dipX) || !std::isfinite(dipY) ||
        shape.widthDips < 1.0f || shape.widthDips > 4096.0f ||
        shape.heightDips < 1.0f || shape.heightDips > 4096.0f ||
        shape.radiusDips < 0.0f ||
        std::abs(shape.effectiveTension) > 1.15f)
        return false;

    const float radius = gd::SurfaceRadius(shape.widthDips, shape.heightDips,
                                           shape.radiusDips, shape.effectiveTension);
    const float distance = gd::SurfaceDistance(dipX, dipY, shape.widthDips,
                                               shape.heightDips, radius, 1.0f,
                                               shape.effectiveTension);
    return std::isfinite(radius) && std::isfinite(distance) && distance <= 0.0f;
}

inline bool AccessibleSurfaceContainsPixels(AccessibleSurfaceShape const& shape,
                                            double localPxX, double localPxY,
                                            unsigned dpi) noexcept {
    if (dpi == 0 || !std::isfinite(localPxX) || !std::isfinite(localPxY))
        return false;

    const double pxToDip = 96.0 / static_cast<double>(dpi);
    const double dipX = localPxX * pxToDip;
    const double dipY = localPxY * pxToDip;
    if (!std::isfinite(dipX) || !std::isfinite(dipY) ||
        std::abs(dipX) > std::numeric_limits<float>::max() ||
        std::abs(dipY) > std::numeric_limits<float>::max())
        return false;

    return AccessibleSurfaceContainsDips(shape, static_cast<float>(dipX),
                                          static_cast<float>(dipY));
}

} // namespace gd::input

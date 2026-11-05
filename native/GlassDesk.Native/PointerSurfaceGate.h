#pragma once

#include <limits>

namespace gd::input {
// Only positional pointer events use this value. GDPointer stays ABI-compatible;
// invoke/range events and system-status events retain their existing IDs.
inline constexpr int kOutsideSurfaceControlId = (std::numeric_limits<int>::min)();

inline int ControlIdForSurface(bool inside, int rectangularControlId) noexcept {
    return inside ? rectangularControlId : kOutsideSurfaceControlId;
}
}

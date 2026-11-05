#pragma once
#include <windows.h>
#include <wincodec.h>

namespace gd {
inline bool PropagateArtworkFailure(unsigned backend,HRESULT failure) noexcept {
    if(backend==2)return true;
    if(backend!=3)return false; // Preserve ordinary renderer's input fallback.
    // Invalid external cover bytes are data errors. Unknown/D2D/DXGI/allocation
    // failures must reach the experimental first-fault boundary, never vanish
    // inside the optional-artwork catch.
    switch(failure){
    case WINCODEC_ERR_UNKNOWNIMAGEFORMAT:
    case WINCODEC_ERR_BADIMAGE:
    case WINCODEC_ERR_BADHEADER:
    case WINCODEC_ERR_BADSTREAMDATA:
    case WINCODEC_ERR_FRAMEMISSING:
    case WINCODEC_ERR_UNSUPPORTEDPIXELFORMAT:
    case WINCODEC_ERR_STREAMREAD:
        return false;
    default:return true;
    }
}
}

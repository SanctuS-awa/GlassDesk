#pragma once
#include <algorithm>
#include <cstdint>
#include <cmath>
namespace gd::desktopviewport {
// Reserve the standard maximum Details height from the first capsule frame.
// Keeping the two VisualSurface extents stable avoids exposing an old
// 256-DIP realization when Details grows beyond Peek. Still local and capped.
inline int PreferredDesktopSourceHeight(int bodyPixels,float dpi,int desktopPixels) noexcept {
    if(bodyPixels<1||bodyPixels>1024||desktopPixels<bodyPixels||
        !std::isfinite(dpi)||dpi<0.5f||dpi>4.0f)return 0;
    return std::min(desktopPixels,std::min(1024,std::max(bodyPixels,int(std::ceil(512.0f*dpi)))));
}
struct StableSourceHeight { int height{}; int above{}; };
// Reserve the space above the fixed-top widget at creation, in addition to
// its maximum normal expansion. Otherwise a window below the desktop's top
// makes a 512-DIP expansion overflow the retained canvas and moves its source
// rectangle during the animation. A 1024-pixel cap remains in force.
inline StableSourceHeight PreferredStableSourceHeight(int bodyPixels,float dpi,int desktopPixels,
                                                       int64_t distanceFromTop) noexcept {
    const int preferred=PreferredDesktopSourceHeight(bodyPixels,dpi,desktopPixels);
    if(preferred==0)return {};
    const int maxHeight=std::min(desktopPixels,1024);
    const int64_t requested=static_cast<int64_t>(std::ceil(40.0f*dpi));
    const int above=static_cast<int>(std::min({requested,std::max<int64_t>(0,distanceFromTop),
                                                static_cast<int64_t>(maxHeight-preferred)}));
    return {preferred+above,above};
}
// Revalidate the complete retained source after a display topology change,
// even when the body itself still fits. Pure metadata: no graphics calls.
inline bool RetainedDesktopSourceFitsBounds(int desktopX,int desktopY,int desktopWidth,int desktopHeight,
    int sourceX,int sourceY,int sourceWidth,int sourceHeight) noexcept {
    if(desktopWidth<=0||desktopHeight<=0||sourceWidth<=0||sourceHeight<=0)return false;
    return sourceX>=desktopX&&sourceY>=desktopY&&
        int64_t(sourceX)+sourceWidth<=int64_t(desktopX)+desktopWidth&&
        int64_t(sourceY)+sourceHeight<=int64_t(desktopY)+desktopHeight;
}
// Pure metadata guard applied before Win32 commits a placement. It prevents a
// normal drag/resize from being mistaken for an invalid optical source request.
inline bool ClampDesktopPlacement(int desktopX,int desktopY,int desktopWidth,int desktopHeight,
    int windowWidth,int windowHeight,int& x,int& y) noexcept {
    if(desktopWidth<=0||desktopHeight<=0||windowWidth<=0||windowHeight<=0||windowWidth>desktopWidth||windowHeight>desktopHeight)return false;
    const int64_t right=int64_t(desktopX)+desktopWidth-windowWidth;
    const int64_t bottom=int64_t(desktopY)+desktopHeight-windowHeight;
    x=static_cast<int>(std::clamp<int64_t>(x,desktopX,right));
    y=static_cast<int>(std::clamp<int64_t>(y,desktopY,bottom));return true;
}
}

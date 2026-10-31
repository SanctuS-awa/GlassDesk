#pragma once
#include "SystemGlassGeometry.h"

namespace gd::systemglass {
// Shared lower-bound contract with managed ReadingMaterialPolicy. This is a
// transmission-loss ceiling for app-owned light, never a sampled pixel value.
inline constexpr float kMaximumPrimaryAbsorption = .075f;
// App-owned light constants only. No source surface, pixels, or COM objects.
struct LightProfile {
    float width{},height{},radius{},scale{},strain{},band{};
};
struct LightUpdate { unsigned width{},height{};bool full{}; };
inline LightUpdate LightUpdateRegion(unsigned width,unsigned height,bool initialized) noexcept {
    // A non-virtual Composition surface's FIRST BeginDraw must cover it fully.
    return initialized?LightUpdate{width,height,false}:LightUpdate{1024,1024,true};
}
inline bool MakeLightProfile(unsigned width,unsigned height,float radiusDips,
    float scale,float strain,LightProfile& result) noexcept {
    result={};
    if(width<1||height<1||width>1024||height>1024||!std::isfinite(radiusDips)
        ||!std::isfinite(scale)||!std::isfinite(strain)||scale<.5f||scale>4
        ||radiusDips<0||std::abs(strain)>1.15f)return false;
    const float w=width/scale,h=height/scale;
    if(w<1||h<1||w>1024||h>1024)return false;
    const float progress=std::clamp((h-52.0f)/124.0f,0.0f,1.0f);
    const float radius=gd::SurfaceRadius(w,h,std::min(radiusDips,std::min(w,h)*.5f),strain);
    const float band=detail::SupportedPrimaryBandDips(w,h,radius,progress)*scale;
    result={float(width),float(height),radius*scale,scale,strain,
        band};
    if(!std::isfinite(result.band)||result.band<=0){result={};return false;}
    return true;
}
}

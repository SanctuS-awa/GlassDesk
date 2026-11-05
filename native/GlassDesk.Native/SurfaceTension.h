#pragma once
#include <algorithm>
#include <cmath>

namespace gd {
// Shared CPU profile. The optical shader uses the same bounded two-lobe warp.
// All deformation stays within the existing HWND; text/content never scales.
inline float TensionFactor(float y,float width,float height,float scale,float strain)noexcept
{
    if(width<=0||height<=0)return 1;
    float v=std::clamp(y/height,0.0f,1.0f);
    // A broad strain wave, not a pair of pinched notches. Ends remain anchored.
    float belly=4*v*(1-v);
    float direction=strain>=0?1.0f:-1.0f;
    float amount=std::abs(strain)*belly*belly*(.78f+.22f*direction*(2*v-1));
    float limit=std::min({7*scale,height*.035f,width*.018f});
    return 1-std::min(amount,1.15f)*limit/(width*.5f);
}
inline float SurfaceRadius(float width,float height,float radius,float strain)noexcept
{
    float half=std::min(width,height)*.5f;
    radius=std::clamp(radius,0.0f,half);
    return radius+(half-radius)*.20f*std::min(std::abs(strain),1.0f);
}
inline float SurfaceDistance(float x,float y,float width,float height,float radius,float scale,float strain)noexcept
{
    float factor=TensionFactor(y,width,height,scale,strain);
    x=width*.5f+(x-width*.5f)/factor;
    float qx=std::abs(x-width*.5f)-width*.5f+radius,qy=std::abs(y-height*.5f)-height*.5f+radius;
    return (std::hypot(std::max(qx,0.0f),std::max(qy,0.0f))+std::min(std::max(qx,qy),0.0f)-radius)*factor;
}
}

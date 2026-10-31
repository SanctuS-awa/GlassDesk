#include <SystemGlassLightProfile.h>
#include <algorithm>
#include <cmath>
#include <cstdio>
#include <limits>

namespace {
bool MeshWithinBudget(gd::systemglass::SceneMesh const& mesh)
{
    if (mesh.positions.size() > 65000u || mesh.normals.size() != mesh.positions.size() ||
        mesh.uvs.size() != mesh.positions.size() || mesh.indices.size() > 400000u)
        return false;
    for (gd::systemglass::Float2 const& uv : mesh.uvs)
        if (!std::isfinite(uv.x) || !std::isfinite(uv.y) ||
            uv.x < 0.0f || uv.x > 1.0f || uv.y < 0.0f || uv.y > 1.0f)
            return false;
    return true;
}
}

int main() {
    using namespace gd::systemglass;
    unsigned checked=0;
    for(float dpi:{.5f,1.0f,1.25f,1.5f,2.0f,4.0f})
    for(unsigned height:{32u,52u,100u,176u,300u,512u,1024u})
    for(float strain:{-1.15f,-.4f,0.0f,.4f,1.15f}) {
        const unsigned width=600;
        LightProfile light{};
        if(width/dpi>1024||height/dpi>1024){
            if(MakeLightProfile(width,height,28,dpi,strain,light))return 6;
            continue;
        }
        if(!MakeLightProfile(width,height,28,dpi,strain,light))return 1;
        Frame frame{};frame.widthDips=width/dpi;frame.heightDips=height/dpi;
        frame.radiusDips=std::min(28.0f,std::min(frame.widthDips,frame.heightDips)*.5f);
        frame.dpiScale=dpi;frame.strain=strain;frame.progress=std::clamp((frame.heightDips-52)/124,0.0f,1.0f);
        frame.sourceCanvasWidthPx=1024;frame.sourceCanvasHeightPx=1024;
        SceneMesh mesh{};
        if(BuildSceneMesh(frame,mesh)!=BuildStatus::success||std::abs(light.band-mesh.bandPx)>.0001f)return 2;
        const float radius=gd::SurfaceRadius(frame.widthDips,frame.heightDips,frame.radiusDips,strain);
        if(std::abs(light.radius/dpi-radius)>.0001f)return 3;
        // The light cannot introduce a different contour during a strain wave.
        for(float x:{0.0f,float(width)*.13f,float(width)*.5f,float(width)})
        for(float y:{0.0f,float(height)*.27f,float(height)*.7f,float(height)}) {
            float physical=gd::SurfaceDistance(x,y,float(width),float(height),light.radius,dpi,strain);
            float meshDistance=detail::Distance(frame,x/dpi,y/dpi,radius)*dpi;
            if(std::abs(physical-meshDistance)>.002f)return 4;
        }
        ++checked;
    }
    LightProfile output{};
    const float peekBand=detail::PrimaryBandDips(320.0f,176.0f,1.0f);
    const float detailsBand=detail::PrimaryBandDips(360.0f,476.0f,1.0f);
    if(std::abs(peekBand-30.0f)>.0001f||std::abs(detailsBand-36.0f)>.0001f||
        detailsBand<=peekBand)return 8;
    if(std::abs(detail::PrimaryBandDips(1024.0f,52.0f,0.0f)-18.0f)>.0001f||
        std::abs(detail::PrimaryBandDips(1024.0f,176.0f,1.0f)-30.0f)>.0001f||
        detail::PrimaryBandDips(0.0f,476.0f,1.0f)!=0.0f)return 9;

    float previous=peekBand;
    for(unsigned step=1;step<=100;++step) {
        const float u=float(step)/100.0f;
        const float width=320.0f+40.0f*u;
        const float height=176.0f+300.0f*u;
        const float progress=std::clamp((height-52.0f)/124.0f,0.0f,1.0f);
        const float band=detail::PrimaryBandDips(width,height,progress);
        if(!std::isfinite(band)||band<previous-0.00001f||band>36.0001f)return 10;
        previous=band;
    }
    if(std::abs(previous-detailsBand)>.0001f)return 11;

    // The DIP recipe is independent of raster scale; both consumers upload
    // the same profile after applying DPI exactly once.
    for(float dpi:{.5f,1.0f,1.25f,1.5f,2.0f}) {
        const unsigned width=unsigned(std::lround(360.0f*dpi));
        const unsigned height=unsigned(std::lround(476.0f*dpi));
        LightProfile light{};
        if(!MakeLightProfile(width,height,28,dpi,0,light))return 12;
        Frame frame{};frame.widthDips=float(width)/dpi;frame.heightDips=float(height)/dpi;
        frame.radiusDips=28.0f;frame.dpiScale=dpi;
        frame.progress=std::clamp((frame.heightDips-52.0f)/124.0f,0.0f,1.0f);
        frame.sourceCanvasWidthPx=1024;frame.sourceCanvasHeightPx=1024;
        SceneMesh mesh{};
        if(BuildSceneMesh(frame,mesh)!=BuildStatus::success||
            std::abs(light.band-mesh.bandPx)>.0001f||!MeshWithinBudget(mesh))return 13;
        constexpr float expectedSupportedNormalBandDips=28.0f;
        constexpr float expectedLegacyZoomBandDips=36.0f;
        if(std::abs(light.band/dpi-expectedSupportedNormalBandDips)>.0001f||
            std::abs(mesh.bandPx/dpi-expectedSupportedNormalBandDips)>.0001f||
            std::abs(mesh.primaryZoomBandPx/dpi-expectedLegacyZoomBandDips)>.0001f)return 14;
    }

    auto first=LightUpdateRegion(300,78,false),next=LightUpdateRegion(600,450,true);
    if(!first.full||first.width!=1024||first.height!=1024||next.full||next.width!=600||next.height!=450)return 7;
    if(MakeLightProfile(1025,100,28,1,0,output)||MakeLightProfile(100,0,28,1,0,output)
        ||MakeLightProfile(100,100,28,std::numeric_limits<float>::quiet_NaN(),0,output)
        ||MakeLightProfile(100,100,28,1,1.2f,output))return 5;
    std::printf("PASS: %u optical-light / actual-mesh profile pairs, strain contours, invalid input rejection; CPU only\n",checked);
}

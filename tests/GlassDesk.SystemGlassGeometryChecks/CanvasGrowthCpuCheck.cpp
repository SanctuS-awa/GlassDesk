#include <SystemGlassGeometry.h>
#include <DesktopSourceViewport.h>
#include <cstdio>
int main(){int failures=0;for(float dpi:{1.0f,1.25f,1.5f,1.75f,2.0f,2.25f})for(int h=52;h<=768;++h){
 int ch=gd::systemglass::PreferredDesktopSourceHeight(h,dpi,1600);int above=std::min(int(40*dpi),ch-h);
 gd::systemglass::Frame f{400/dpi,h/dpi,std::min(24.0f,h/dpi*.5f),dpi,0,0,600.0f,float(ch),40.0f,float(above)};
 if(!gd::systemglass::detail::ValidateFrame(f)){++failures;if(failures<4)std::printf("FAIL h=%d scale=%f\n",h,dpi);}
 }
 int transitions=0;
 for(float dpi:{1.0f,1.25f,1.5f,1.75f,2.0f,2.25f}){
  const int initial=gd::systemglass::PreferredDesktopSourceHeight(int(std::ceil(52*dpi)),dpi,1600);
  for(float dips:{52.0f,176.0f,300.0f,476.0f,512.0f}){
   int body=int(std::ceil(dips*dpi));if(body>1024)continue;
   ++transitions;
   if(gd::systemglass::PreferredDesktopSourceHeight(body,dpi,1600)!=initial)++failures;
  }
 }
 if(gd::systemglass::PreferredDesktopSourceHeight(78,1.5f,720)!=720)++failures;
 if(gd::systemglass::PreferredDesktopSourceHeight(104,2.0f,1600)!=1024)++failures;
 if(gd::systemglass::PreferredDesktopSourceHeight(451,1.5f,450)!=0)++failures;
 if(gd::systemglass::PreferredDesktopSourceHeight(0,1.5f,1600)!=0)++failures;
 if(gd::systemglass::PreferredDesktopSourceHeight(1025,2.0f,1600)!=0)++failures;
 if(gd::systemglass::PreferredDesktopSourceHeight(78,NAN,1600)!=0)++failures;
 std::printf("Stable capsule/Peek/Details source extents: %d accepted transitions, DPI cap and desktop clamp checked\n",transitions);
 std::printf("DPI/canvas growth invariants: %d failures across 4302 frame boundaries\n",failures);return failures?1:0;}



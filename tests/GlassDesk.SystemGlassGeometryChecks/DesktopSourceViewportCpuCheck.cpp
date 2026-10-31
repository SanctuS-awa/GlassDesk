#include <DesktopSourceViewport.h>
#include <cstdio>
int main(){int failures=0;
 auto test=[&](int dx,int dy,int dw,int dh,int w,int h,int x,int y,int expectedX,int expectedY,bool ok){auto result=gd::systemglass::ClampDesktopPlacement(dx,dy,dw,dh,w,h,x,y);if(result!=ok||(result&&(x!=expectedX||y!=expectedY))){++failures;std::printf("FAIL viewport %d,%d\n",x,y);}};
 test(0,0,2560,1600,312,78,1508,306,1508,306,true);
 test(0,0,2560,1600,312,78,1156,1706,1156,1522,true);
 test(0,0,2560,1600,480,264,-100,-50,0,0,true);
 test(-1920,-200,4480,1800,480,264,-2200,-500,-1920,-200,true);
 test(0,0,2560,1600,480,264,2400,1522,2080,1336,true);
 test(0,0,2560,1600,480,264,2147483647,-2147483647,2080,0,true);
 test(0,0,2560,1600,3000,264,0,0,0,0,false);
 struct Region {int dx,dy,dw,dh,sx,sy,sw,sh;bool fits;};
 const Region regions[]{
  {0,0,2560,1600,2000,800,560,800,true},
  // The body can still fit after resolution shrinks while its old cache does not.
  {0,0,1920,1080,1400,500,800,800,false},
  {-1920,0,4480,1600,-1800,40,800,800,true},
  {0,0,2560,1600,-1800,40,800,800,false},
  {-1920,-200,4480,1800,-1920,-200,4480,1800,true},
  {0,0,2560,1600,0,0,0,800,false},
  {0,0,0,1600,0,0,800,800,false},
  {0,0,2560,1600,2147483640,0,100,100,false},
  {2147483600,0,100,100,2147483640,0,60,100,true}
 };
 for(const auto& r:regions){
  if(gd::systemglass::RetainedDesktopSourceFitsBounds(r.dx,r.dy,r.dw,r.dh,r.sx,r.sy,r.sw,r.sh)!=r.fits){
   ++failures;std::printf("FAIL retained source %d,%d\n",r.sx,r.sy);
  }
 }
 // User's 150% display: host stays at physical y=36 while growing from
 // capsule to the 768px Home page. The initial source must already contain
 // that full trajectory, so the DWM rectangle does not slide from y=0 to 36.
 for(int height : {78,264,714,768}){
  const auto stable=gd::systemglass::PreferredStableSourceHeight(height,1.5f,1600,36);
  if(stable.height!=804||stable.above!=36||
     !gd::systemglass::RetainedDesktopSourceFitsBounds(0,0,2560,1600,980,0,600,stable.height)||
     36+height>stable.height){++failures;std::printf("FAIL stable expansion height=%d canvas=%d above=%d\n",height,stable.height,stable.above);}
 }
 const auto capped=gd::systemglass::PreferredStableSourceHeight(104,2.0f,1600,36);
 if(capped.height!=1024||capped.above!=0){++failures;std::printf("FAIL capped source margin\n");}
 const auto negative=gd::systemglass::PreferredStableSourceHeight(78,1.5f,1600,-12);
 if(negative.height!=768||negative.above!=0){++failures;std::printf("FAIL negative margin\n");}
 std::printf("Stable expansion source checks: %s\n",failures?"FAIL":"PASS");
 std::printf("Desktop placement/source checks: %d failures across 16 cases\n",failures);return failures?1:0;}

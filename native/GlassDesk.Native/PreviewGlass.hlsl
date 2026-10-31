// Local lens on an application-owned source; XAML foreground is never sampled.
cbuffer Frame:register(b0) {
 float4 dimensions; float4 shape; float4 material; float4 state;
 float4 lenses[4]; float4 styles[4]; float4 reserved[3];
 float4 extra; // light enabled, unused, optics-only, unused; retain 64-float ABI
};
Texture2D<float4> backdrop:register(t0);
Texture2D<float4> softenedBackdrop:register(t1);
SamplerState imageSampler:register(s0);
struct Varying {float4 position:SV_POSITION;float2 uv:TEXCOORD;};
Varying VS(uint id:SV_VertexID){Varying o;o.uv=float2((id<<1)&2,id&2);o.position=float4(o.uv*float2(2,-2)+float2(-1,1),0,1);return o;}
// Cached separable Gaussian, evaluated only when the still image, radius or
// cover scale changes. Animation samples the finished image once per surface.
// reserved[0] = UV step xy, sigma in intermediate pixels, input mip level.
float4 PSBlur(Varying input):SV_TARGET{
 float4 colour=0;float total=0,sigma=max(reserved[0].z,.001);
 [unroll]for(int i=-10;i<=10;i++){
  float weight=exp(-.5*i*i/(sigma*sigma));
  colour+=backdrop.SampleLevel(imageSampler,input.uv+reserved[0].xy*i,reserved[0].w)*weight;total+=weight;
 }
 return colour/total;
}
float Phase(float p,float a,float b){float t=saturate((p-a)/(b-a));return t*t*(3-2*t);}
float Round(float2 p,float2 c,float2 size,float r){r=min(r,min(size.x,size.y)*.5);float2 q=abs(p-c)-size*.5+r;return length(max(q,0))+min(max(q.x,q.y),0)-r;}
float Mask(float d,float aa){return 1-smoothstep(-aa,aa,d);}
float3 MixMask(float3 base,float3 ink,float d,float aa){return lerp(base,ink,Mask(d,aa));}
float2 MainExtent(){
 float t=saturate(shape.z),growth=Phase(t,.08,1),strain=clamp(shape.w/7,-.6,.6)*material.x,rebound=clamp(shape.z-t,-.06,.08)*material.x;
 return float2(lerp(200,320,Phase(t,0,.95))*(1-.035*strain-rebound*.3),lerp(52,176,growth)*(1+.045*strain+rebound*.55));
}
float MainDistance(float2 screen){
 float2 p=screen-float2(shape.x-160,shape.y),size=MainExtent();float t=saturate(shape.z),y=saturate(p.y/size.y),breath=sin(3.14159265*t)*material.x;
 float widthScale=1+breath*(.075*sin(3.14159265*y)+.055*(2*y-1));p.x=160+(p.x-160)/widthScale;
 return Round(p,float2(160,size.y*.5),size,lerp(26,24,Phase(t,.08,1)));
}
float2 MainNormal(float2 p){float2 n=float2(MainDistance(p+float2(.4,0))-MainDistance(p-float2(.4,0)),MainDistance(p+float2(0,.4))-MainDistance(p-float2(0,.4)));return n/max(length(n),.0001);}
// A shallow convex height derivative. One normal drives refraction AND light.
// Art-directed single-interface approximation, not Apple's private renderer.
float3 SurfaceNormal(float depth,float2 outward,float band,float height){
 float u=1-saturate(depth/band),slope=height/band*u/sqrt(max(1-u*u,.003));
 return normalize(float3(outward*slope,1));
}
float2 LensSample(float2 p,float2 centre,float d,float2 outward,float zoom,float bend,float band,bool primary){
 float depth=max(0,-d),height=band*(primary?.62:.46);
 float3 normal=SurfaceNormal(depth,outward,band,height),ray=refract(float3(0,0,-1),normal,1/1.45);
 float cap=sqrt(saturate(1-pow(1-saturate(depth/band),2)));
 float2 edge=ray.xy/max(-ray.z,.1)*(band*(primary?.62:.48)+height*cap)*bend*Phase(depth,0,primary?1.5:3);
 // Keep the silhouette continuous, but do not flatten the whole outer bezel.
 // Primary glass hands over to its gentle interior scale deeper in the body.
 // The already-reviewed diagnostic magnifier retains its original profile.
 float weight=primary?Phase(depth,band*.6,band+6):Phase(depth,0,band);
 return p-(p-centre)*(1-1/max(zoom,1))*weight+edge;
}
float2 MainSample(float2 p,float d,float2 n){
 float t=saturate(shape.z),energy=saturate(abs(shape.w)/8),zoom=1+state.x*(1+.12*t+.12*energy);
 return LensSample(p,float2(shape.x,shape.y+MainExtent().y*.5),d,n,zoom,material.y*(1+.12*energy),lerp(18,30,Phase(t,.08,1)),true);
}
float3 Scene(float2 p,float softness){
 float aa=max(max(.45/dimensions.z,.25),softness);
 float3 result=float3(1,1,1);
 if(dimensions.w>.5){
  if(dimensions.w<1.5){
   float left=16,right=dimensions.x-16,y=dimensions.y*.5;
   result=MixMask(result,float3(.82,.85,.89),Round(p,float2((left+right)*.5,y),float2(right-left,3),1.5),aa);
   float end=lerp(left,right,state.x);
   if(end>left)result=MixMask(result,float3(.06,.50,.98),Round(p,float2((left+end)*.5,y),float2(end-left,3),1.5),aa);
  }else if(dimensions.w>2.5){
   result=MixMask(result,lerp(float3(.84,.87,.91),float3(.06,.50,.98),state.x),Round(p,dimensions.xy*.5,float2(dimensions.x-8,26),13),aa);
  }
 }else{
 p.x+=styles[3].z;
 if(state.w>.5&&styles[3].w>.5){
  uint w=1,h=1;backdrop.GetDimensions(w,h);float scale=max(dimensions.x/w,dimensions.y/h);
  float2 uv=(p-dimensions.xy*.5)/(float2(w,h)*scale)+.5;
  // Always keep the clear source at full detail. The second texture is a real
  // two-pass Gaussian, not an enlarged coarse mip mistaken for scattering.
  float4 texel=softness>.001?softenedBackdrop.SampleLevel(imageSampler,uv,0):backdrop.SampleLevel(imageSampler,uv,0);
  result=lerp(float3(1,1,1),texel.rgb,texel.a);
 }else{
 float3 bg=lerp(float3(.92,.95,.98),float3(.97,.95,.97),saturate(p.x/dimensions.x));
 float2 grid=abs(frac((p+20)/40)-.5)*40;bg=lerp(bg,bg*.89,Mask(min(grid.x,grid.y)-.45,aa)*.58);
 // Witnesses only in diagnostic grid, never photos.
 float2 a=float2(236,149),b=float2(470,176);
 bg=MixMask(bg,float3(.08,.095,.12),length(p-a)-20,aa);bg=MixMask(bg,float3(.12,.13,.16),length(p-b)-23,aa);
 [unroll]for(int eye=0;eye<2;eye++){
  bg=MixMask(bg,float3(.96,.98,1),Round(p,a+float2(eye==0?-8:8,-1),float2(4,7),2),aa);
  bg=MixMask(bg,float3(.96,.98,1),Round(p,b+float2(eye==0?-6:6,0),float2(3,5),1.5),aa);
 }
 result=bg;
 }
 }
 return result;
}
float3 Scene(float2 p){return Scene(p,0);}
float3 SoftScene(float2 p,float radius){
 return Scene(p,radius);
}
float3 Light(float3 colour,float d,float2 outward,float band,float emphasis,bool primary){
 float depth=max(0,-d);float3 normal=SurfaceNormal(depth,outward,band,band*(primary?.62:.46));
 float highlight=pow(saturate(dot(normal,normalize(float3(-.48,-.64,1)))),primary?10:20),fresnel=pow(1-normal.z,3);
 float rim=exp(-pow((depth-(primary?1.1:.65))/(primary?1.3:.65),2));
 float key=pow(saturate(dot(outward,normalize(float2(-.55,-.83)))),3),counter=pow(saturate(dot(outward,normalize(float2(.65,.76)))),5);
 colour=lerp(colour,1,saturate((highlight*(primary?.09:.13)+rim*(key*(primary?.13:.53)+counter*(primary?.015:.18))+fresnel*(primary?.035:.075))*emphasis));
 // No offset dark ring on the main body: refraction defines its thickness.
 return primary?colour:colour*(1-exp(-pow((depth-2.4)/1.6,2))*(1-key)*.028*emphasis);
}
bool StageLight(){return extra.x>.5&&extra.z<.5;}
float3 LowerGlass(float2 p){
 float2 sample=MainSample(p,MainDistance(p),MainNormal(p));
 float3 colour=SoftScene(sample,material.z);
 return lerp(colour,float3(.985,.99,1),material.w);
}
float3 Main(float2 p){
 float3 original=Scene(p);float d=MainDistance(p),aa=max(.5/dimensions.z,.25);
 float3 result=original;
 if(d>1){float shadow=StageLight()?exp(-max(0,MainDistance(p-float2(0,3)))/4)*.035:0;result=original*(1-shadow);}
 else{
 float2 n=MainNormal(p),sample=MainSample(p,d,n);float3 glass=SoftScene(sample,extra.z>.5?0:material.z);
 if(extra.z<.5)glass=lerp(glass,float3(.985,.99,1),material.w);
 if(StageLight())glass=Light(glass,d,n,lerp(18,30,Phase(saturate(shape.z),.08,1)),1,true);
 result=lerp(original,glass,Mask(d,aa));
 }
 return result;
}
float4 PS(Varying input):SV_TARGET{
 float2 p=input.uv*dimensions.xy;bool stage=dimensions.w<.5;float3 result=stage?Main(p):Scene(p);
 [loop]for(int j=0;j<4;j++){
  float4 lens=lenses[j];if(lens.z<=0||lens.w<=0)continue;
  if(stage&&j<3&&(MainDistance(p)>0||extra.z>.5))continue;
  float d=Round(p,lens.xy,lens.zw,styles[j].x);if(d>8)continue;
  float a=styles[j].y;bool lit=!stage||StageLight();
  if(d>0&&lit)result*=1-exp(-d/2)*exp(-d/3)*.085*(stage&&j<3?(.6+.4*a)*state.y:1);if(d>1)continue;
  float2 q=abs(p-lens.xy)-lens.zw*.5+styles[j].x;
  float2 n=length(max(q,0))>.001?normalize(max(q,0))*sign(p-lens.xy):q.x>q.y?float2(sign(p.x-lens.x),0):float2(0,sign(p.y-lens.y));
  float zoom=stage?(j==3?1.85:1.14+.055*a):1.18+.12*a,bend=stage?(j==3?1.15:.85+.25*a):.85+.5*a;
  float band=max(3,min(lens.z,lens.w)*(stage&&j<3?.42:stage&&j==3?.34:.28));
  float2 sample=LensSample(p,lens.xy,d,n,zoom,bend,band,false);
  // An upper glass tile sees the LOWER material (including its scattering and
  // tint), never a crisp cut-out of the original picture. Foreground is XAML.
  // The diagnostic magnifier continues to read only the original background.
  float3 glass=stage&&j<3?LowerGlass(sample):Scene(sample);
  if(stage&&j<3&&lit){
   glass=lerp(glass,styles[j].z>.5?float3(1,1,1):float3(.03,.04,.06),styles[j].z>.5?.13:.07);
   float crown=saturate(.5-(p.y-lens.y)/lens.w);
   glass=lerp(glass,1,crown*crown*(.055+.025*a));
  }
  if(lit)glass=Light(glass,d,n,band,stage&&j<3?1.05+.3*a:.8,false);
  result=lerp(result,glass,Mask(d,max(.45/dimensions.z,.25))*(stage&&j<3?state.y:1));
 }
 if(dimensions.w>2.5){
  float2 centre=lenses[0].xy;float aa=.55/dimensions.z;result=MixMask(result,float3(.11,.13,.16),length(p-centre)-10,aa);
  [unroll]for(int e=0;e<2;e++){
   float2 ep=p-centre-float2(e==0?-3.5:3.5,0);
   float eye=state.x>.5?abs(length(ep-float2(0,1.6))-2.1)-.65:Round(ep,0,float2(2.2,3.6),1.1);
   if(state.x>.5)eye=max(eye,ep.y-.3);result=MixMask(result,float3(1,1,1),eye,aa);
  }
 }
 return float4(saturate(lerp(float3(.95,.96,.97),result,state.z)),1);
}

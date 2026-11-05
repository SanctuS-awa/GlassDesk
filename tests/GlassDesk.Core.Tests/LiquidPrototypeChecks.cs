using GlassDesk.Core;
using System.Numerics;

static class LiquidPrototypeChecks
{
    public static void Run(Action<bool,string> check)
    {
        var model=new LiquidTransitionPrototype();
        check(model.Distance(new(61,26))<0&&model.Distance(new(59,26))>0&&model.Distance(new(160,53))>0,
            "liquid lab starts at the real 200x52 capsule silhouette");
        model.Scrub(.5);
        check(model.Extent.Y>100&&model.Distance(new(40,70))<0&&model.Distance(new(160,70))<0,
            "mid expansion is a continuous body with room below the shoulder instead of a T-shaped shelf");
        model.Scrub(0);
        check(model.HeaderOffset(true).Length()<.001f&&model.HeaderOffset(false).Length()<.001f,
            "header movement starts at its real capsule placement");
        model.Scrub(.5);
        check(model.HeaderOffset(true).X < -10 && model.HeaderOffset(false).X > 7,
            "companion and time make space with distinct continuous paths");
        model.Scrub(1);
        check(model.HeaderOffset(true).Length()<.001f&&model.HeaderOffset(false).Length()<.001f,
            "header motion settles without a residual offset");
        check(Vector2.Distance(model.CompanionSize,new(58,28))<.001f,
            "the companion retains its identity while becoming the peek pill");
        model.Scrub(.5);
        check(model.ContentProgress==0&&model.TransportProgress==0,"music waits for physical space instead of crossing the time and face");
        model.Scrub(.7);
        check(model.ContentProgress>0&&model.TransportProgress==0,"transport enters after the music information has room");
        bool valid=true;
        foreach(double p in new[]{0,.1,.25,.5,.75,.9,1})
        {
            model.Scrub(p);
            valid &= model.Contour().All(v=>float.IsFinite(v.X)&&float.IsFinite(v.Y)&&v.X>=-20&&v.X<=340&&v.Y>=-20&&v.Y<=208);
        }
        check(valid,"all scrub stages have finite connected contours inside the preview envelope");
        model.Scrub(1);
        check(model.Distance(new(1,88))<0&&model.Distance(new(160,175))<0&&model.Distance(new(160,177))>0,
            "expanded lab settles at the exact 320x176 peek silhouette");
        model.Scrub(0);model.SetTarget(true,0);model.Sample(100);
        double position=model.Progress,velocity=model.Velocity;
        model.SetTarget(false,100);model.Sample(100);
        check(Math.Abs(model.Progress-position)<1e-9&&Math.Abs(model.Velocity-velocity)<1e-9,
            "reversing the lab keeps both visible position and velocity continuous");
        model.Configure(1.2f,.8f,600,100);
        check(Math.Abs(model.Progress-position)<1e-9&&Math.Abs(model.Velocity-velocity)<1e-9,
            "changing lab parameters does not reset the current animation");
        model.Sample(4000);
        check(!model.Active&&model.Progress==0&&model.Velocity==0,"liquid lab settles and needs no idle animation ticks");
        model.SetTarget(true,4100,true);
        check(!model.Active&&model.Progress==1&&model.Velocity==0,"reduced motion preserves endpoints with no moving strain");
        model.Scrub(1);
        var centre=new Vector2(160,88);
        var materialSample=model.Refract(centre+new Vector2(40,0),LiquidTransitionPrototype.MaterialMagnification,0);
        float materialScale=40/(materialSample.X-centre.X);
        check(materialScale>1.04f&&materialScale<1.1f,"built-in material uses subtle magnification rather than the former user-adjustable strong zoom");
        var a=model.Refract(centre+new Vector2(10,0),.18f,1);
        var b=model.Refract(centre+new Vector2(30,0),.18f,1);
        check(Math.Abs((b.X-a.X)-20/1.2016f)<.01,"interior scale matches the baseline magnification without global attenuation");
        var innerA=model.Refract(centre+new Vector2(70,0),.18f,0);
        var innerB=model.Refract(centre+new Vector2(110,0),.18f,0);
        check(Math.Abs((innerB.X-innerA.X)-40/1.2016f)<.01,"interior magnification remains stable away from the optical axis");
        check(Vector2.Distance(model.Refract(centre,.18f,1),centre)<.001,"lens optical axis remains fixed");
        var off=centre+new Vector2(27,16);
        check(Vector2.Distance(model.Refract(off,0,0),off)<.001,"zero magnification and refraction preserve background coordinates");
        check(Vector2.Distance(model.Refract(off,.18f,0),off)>3,"centre magnification is independent from edge refraction");
        var edge=new Vector2(6,88);
        check(Vector2.Distance(model.Refract(edge,0,1),edge)>2,"edge curvature remains independently adjustable");
        bool mapFinite=true;
        foreach(double progress in new[]{0,.2,.5,.8,1}) {
            model.Scrub(progress);
            foreach(var point in model.Contour(24)) {
                var sample=model.Refract(point,.35f,1.5f);
                mapFinite &= float.IsFinite(sample.X)&&float.IsFinite(sample.Y);
            }
        }
        check(mapFinite,"inverse optical map remains finite across the full shape transition");
        model.Scrub(1);
        var refractedEdge=model.Refract(new(2,88),0,1);
        check(refractedEdge.X>2&&refractedEdge.X<30&&Math.Abs(refractedEdge.Y-88)<.01,
            "convex edge bends toward the interior without pinning source coordinates to the rim");
        var defaultEdge=model.Refract(new(2,88),LiquidTransitionPrototype.MaterialMagnification,LiquidTransitionPrototype.MaterialRefraction);
        check(defaultEdge.X-2>9&&defaultEdge.X-2<15,"default bezel has visible bounded optical displacement, independently of a drawn rim");
        var boundary=model.Refract(new(0,88),LiquidTransitionPrototype.MaterialMagnification,LiquidTransitionPrototype.MaterialRefraction);
        var nearBoundary=model.Refract(new(.01f,88),LiquidTransitionPrototype.MaterialMagnification,LiquidTransitionPrototype.MaterialRefraction);
        check(Vector2.Distance(boundary,new(0,88))<.001&&Vector2.Distance(nearBoundary,boundary)<.05,
            "stronger bezel still joins the outside source continuously at the silhouette");
        float previous=float.NegativeInfinity;bool ordered=true;
        for(int x=28;x<=292;x+=2){float mapped=model.Refract(new(x,88),.18f,1).X;ordered&=mapped>previous;previous=mapped;}
        check(ordered,"horizontal optical interior does not fold over itself at default settings");
        var moved=LiquidTransitionPrototype.ClampPlacement(new(-999,999),new(48,48),true);
        check(moved.X>=8&&moved.Y+48<=170,"dragged preview content remains within the expanded layout");
        var capsule=LiquidTransitionPrototype.ClampPlacement(new(-999,999),new(124,32),false);
        check(capsule.X>=68&&capsule.X+124<=252&&capsule.Y+32<=47,"capsule layout edits stay within the capsule instead of the full preview canvas");
    }
}

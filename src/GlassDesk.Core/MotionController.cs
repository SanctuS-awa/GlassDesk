namespace GlassDesk.Core;

public readonly record struct WidgetPose(float X, float Y, float Width, float Height, float Radius, float Opacity=1)
{
    public float SurfaceTension {get;init;}
    internal double[] Values => [X,Y,Width,Height,Radius,Opacity,SurfaceTension];
    internal static WidgetPose From(double[] v)=>new((float)v[0],(float)v[1],Math.Max(1,(float)v[2]),Math.Max(1,(float)v[3]),Math.Max(0,(float)v[4]),Math.Clamp((float)v[5],0,1)){SurfaceTension=Math.Clamp((float)v[6],-1.15f,1.15f)};
}

/// <summary>Interruptible critically damped motion, evaluated with a monotonic clock.</summary>
public sealed class MotionController(WidgetPose initial)
{
    private WidgetPose start=initial;
    private double[] velocity=new double[7];
    private double started, ends, omega, tensionKick;
    public WidgetPose Target {get;private set;}=initial;
    public WidgetPose Current {get;private set;}=initial;
    public bool Active {get;private set;}

    private (WidgetPose Pose,double[] Velocity) Evaluate(double now)
    {
        if (!Active || now>=ends) return (Target,new double[7]);
        var a=start.Values; var b=Target.Values; var result=new double[7]; var speed=new double[7];
        double t=Math.Max(0,(now-started)/1000), decay=Math.Exp(-omega*t);
        double duration=(ends-started)/1000, u=Math.Clamp(t/duration,0,1), v=1-u;
        double endDecay=Math.Exp(-omega*duration);
        double endPositionBlend=u*u*(3-2*u), endVelocityBlend=u*u*(u-1);
        double endPositionSlope=6*u*(1-u)/duration, endVelocitySlope=u*(3*u-2);
        for(int i=0;i<6;i++)
        {
            double offset=a[i]-b[i], c=velocity[i]+omega*offset;
            // A truncated spring still has residual displacement and speed.
            // Remove just that residual with a C1 Hermite correction: the
            // start pose/velocity survive, and the deadline reaches exact rest.
            double endOffset=(offset+c*duration)*endDecay;
            double endSpeed=(velocity[i]-omega*c*duration)*endDecay;
            result[i]=b[i]+(offset+c*t)*decay-endOffset*endPositionBlend-duration*endSpeed*endVelocityBlend;
            speed[i]=(velocity[i]-omega*c*t)*decay-endOffset*endPositionSlope-endSpeed*endVelocitySlope;
        }
        // A finite strain impulse, separate from content/size. C1 continuity
        // survives reversals; both displacement and velocity end exactly at zero.
        double envelope=55*u*u*v*v*v*Math.Cos(Math.PI*u);
        double derivative=55*((2*u*v*v*v-3*u*u*v*v)*Math.Cos(Math.PI*u)
            -Math.PI*u*u*v*v*v*Math.Sin(Math.PI*u));
        result[6]=(2*u*u*u-3*u*u+1)*a[6]+(u*u*u-2*u*u+u)*duration*velocity[6]+tensionKick*envelope;
        speed[6]=((6*u*u-6*u)*a[6]+(3*u*u-4*u+1)*duration*velocity[6]+tensionKick*derivative)/duration;
        return (WidgetPose.From(result),speed);
    }
    public WidgetPose Sample(double now)
    {
        Current=Evaluate(now).Pose;
        if(now>=ends) Active=false;
        return Current;
    }
    public void Retarget(WidgetPose target,double now,int durationMs,bool reducedMotion=false)
    {
        if (reducedMotion || durationMs<=0) { Jump(target); return; }
        if (target==Target && Active) return;
        var current=Evaluate(now);
        start=current.Pose; Current=start; velocity=current.Velocity;
        tensionKick=TensionKickFor(start,target);
        Target=target with {SurfaceTension=0}; started=now; ends=now+durationMs; omega=8.0/(durationMs/1000.0);
        Active=start!=target || velocity.Any(v=>Math.Abs(v)>.001);
    }
    public void Jump(WidgetPose pose)
    {
        start=Current=Target=pose; velocity=new double[7]; Active=false; started=ends=0; tensionKick=0;
    }
    public void Translate(float dx,float dy,double now)
    {
        var evaluated=Evaluate(now);
        var target=Target with {X=Target.X+dx,Y=Target.Y+dy};
        double remaining=Math.Max(0,ends-now);
        if (!Active || remaining<=0) { Jump(target); return; }

        // A drag changes only the component's anchor. Shift the active
        // segment's X/Y endpoints and presented pose together so every future
        // sample receives the same offset while all shape/strain motion,
        // velocity, kick, and the original deadline remain intact.
        start=start with {X=start.X+dx,Y=start.Y+dy};
        Target=target;
        Current=evaluated.Pose with {X=evaluated.Pose.X+dx,Y=evaluated.Pose.Y+dy};
    }

    private static double TensionKickFor(WidgetPose from,WidgetPose to)
    {
        double widthChange=(to.Width-from.Width)/140.0;
        double heightChange=(to.Height-from.Height)/220.0;
        double dominant=Math.Abs(widthChange)>Math.Abs(heightChange)?widthChange:heightChange;
        return Math.Clamp(Math.Max(Math.Abs(widthChange),Math.Abs(heightChange)),0,1)*Math.Sign(dominant);
    }
}

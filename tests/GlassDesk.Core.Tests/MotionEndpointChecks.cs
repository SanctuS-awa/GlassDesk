using GlassDesk.Core;

internal static class MotionEndpointChecks
{
    public static void Run(Action<bool,string> check)
    {
        foreach(int duration in new[]{160,220,360})
        {
            var target=new WidgetPose(100,20,360,512,24);
            var motion=new MotionController(new(100,20,184,32,16));
            motion.Retarget(target,0,duration);
            var justBefore=motion.Sample(duration-.1);
            check(Math.Abs(target.Height-justBefore.Height)<.01f,
                $"{duration}ms expansion has no terminal size snap");
            check(Math.Abs((target.Height-justBefore.Height)/.0001)<2,
                $"{duration}ms expansion approaches rest before the deadline");
            check(motion.Sample(duration)==target&&!motion.Active,
                $"{duration}ms motion finishes at the exact pose without an idle loop");
        }
        var reversal=new MotionController(new(0,0,184,32,16));
        reversal.Retarget(new(0,0,360,512,24),0,360);
        // A shorter finite-difference interval separates inherited velocity
        // from the deliberately large acceleration after changing the target.
        const double epsilonMs=.02;
        var before=reversal.Sample(100-epsilonMs);
        var turn=reversal.Sample(100);
        reversal.Retarget(new(0,0,184,32,16),100,240);
        check(reversal.Sample(100)==turn,"finite-end correction preserves the presented pose at reversal");
        var after=reversal.Sample(100+epsilonMs);
        double incoming=(turn.Height-before.Height)/(epsilonMs/1000);
        double outgoing=(after.Height-turn.Height)/(epsilonMs/1000);
        check(Math.Abs(incoming-outgoing)<Math.Max(8,Math.Abs(incoming)*.02),
            $"finite-end correction carries size velocity through a mid-flight reversal ({incoming:F2}/{outgoing:F2} DIP/s)");

        // Width-only changes must produce the same signed tension impulse as
        // height changes. This guards the former height-sign-only calculation.
        var widthExpand=new MotionController(new(0,0,200,52,24));
        widthExpand.Retarget(new(0,0,280,52,24),0,280);
        var expandedWidthSample=widthExpand.Sample(70);
        check(expandedWidthSample.SurfaceTension>.2f,
            $"width-only expansion produces positive tension ({expandedWidthSample.SurfaceTension:F3})");

        var widthRetract=new MotionController(new(0,0,280,52,24));
        widthRetract.Retarget(new(0,0,200,52,24),0,280);
        var retractedWidthSample=widthRetract.Sample(70);
        check(retractedWidthSample.SurfaceTension<-.2f,
            $"width-only retraction produces negative tension ({retractedWidthSample.SurfaceTension:F3})");

        // Translation is a pure XY offset and must not restart the active
        // width spring or its tension kick.
        var translatedWidth=new MotionController(new(0,0,200,52,24));
        translatedWidth.Retarget(new(0,0,280,52,24),0,280);
        translatedWidth.Translate(10,0,0);
        var translatedWidthSample=translatedWidth.Sample(70);
        check(translatedWidthSample.SurfaceTension>.2f,
            $"Translate preserves positive width-only tension ({translatedWidthSample.SurfaceTension:F3})");

        // A width reversal inherits both size and strain velocity at the
        // interruption point, and remains finite/bounded throughout.
        var widthReversal=new MotionController(new(0,0,200,52,24));
        widthReversal.Retarget(new(0,0,280,52,24),0,280);
        var widthBefore=widthReversal.Sample(70-epsilonMs);
        var widthTurn=widthReversal.Sample(70);
        widthReversal.Retarget(new(0,0,180,52,24),70,240);
        check(widthReversal.Sample(70)==widthTurn,
            "width reversal preserves the presented pose at interruption");
        var widthAfter=widthReversal.Sample(70+epsilonMs);
        double incomingWidth=(widthTurn.Width-widthBefore.Width)/(epsilonMs/1000);
        double outgoingWidth=(widthAfter.Width-widthTurn.Width)/(epsilonMs/1000);
        check(Math.Abs(incomingWidth-outgoingWidth)<Math.Max(8,Math.Abs(incomingWidth)*.02),
            $"width velocity remains continuous through reversal ({incomingWidth:F2}/{outgoingWidth:F2} DIP/s)");
        double incomingTension=(widthTurn.SurfaceTension-widthBefore.SurfaceTension)/(epsilonMs/1000);
        double outgoingTension=(widthAfter.SurfaceTension-widthTurn.SurfaceTension)/(epsilonMs/1000);
        check(Math.Abs(incomingTension-outgoingTension)<Math.Max(.2,Math.Abs(incomingTension)*.05),
            $"tension velocity remains continuous through width reversal ({incomingTension:F2}/{outgoingTension:F2} /s)");
        foreach(double sampleTime in new[]{70d,90,130,190,250,310})
        {
            var pose=widthReversal.Sample(sampleTime);
            check(float.IsFinite(pose.Width)&&float.IsFinite(pose.SurfaceTension)
                &&Math.Abs(pose.SurfaceTension)<=1.15f,
                $"width reversal stays finite and strain-bounded at {sampleTime:F0}ms");
        }

        // Keep the existing height-dominant kick magnitude and envelope.
        var heightOnly=new MotionController(new(0,0,200,52,24));
        heightOnly.Retarget(new(0,0,200,172,24),0,280);
        double u=70d/280d, v=1-u;
        double expectedHeightTension=(120d/220d)*55*u*u*v*v*v*Math.Cos(Math.PI*u);
        check(Math.Abs(heightOnly.Sample(70).SurfaceTension-expectedHeightTension)<.0001,
            "height-only tension magnitude and envelope remain unchanged");
    }
}

using GlassDesk.Core;

internal static class MotionTranslationChecks
{
    public static void Run(Action<bool,string> check)
    {
        var initial=new WidgetPose(12,18,184,32,16);
        var peek=new WidgetPose(112,38,320,176,28);

        // A mid-flight translation must move only the anchor. Compare against
        // an untouched segment at fixed times on both sides of its deadline.
        var reference=new MotionController(initial);
        var moved=new MotionController(initial);
        reference.Retarget(peek,0,280);
        moved.Retarget(peek,0,280);
        const float dx=13, dy=-7;
        const double translateAt=90;
        reference.Sample(translateAt);
        moved.Translate(dx,dy,translateAt);
        CheckOffset(reference.Sample(translateAt),moved.Sample(translateAt),dx,dy,
            "mid-flight translation preserves the presented pose",check);
        foreach(double time in new[]{120d,190,279.5,280,310})
            CheckOffset(reference.Sample(time),moved.Sample(time),dx,dy,
                $"mid-flight translation preserves non-position motion at {time:F1}ms",check);
        check(!reference.Active&&!moved.Active,
            "translation retains the original completion deadline");

        // Repeated moves accumulate offsets without moving the size/strain
        // curve or extending its original deadline.
        reference=new MotionController(initial);
        moved=new MotionController(initial);
        reference.Retarget(peek,0,320);
        moved.Retarget(peek,0,320);
        reference.Sample(55);
        moved.Translate(4,2,55);
        CheckOffset(reference.Sample(55),moved.Sample(55),4,2,
            "first successive translation applies its offset",check);
        reference.Sample(125);
        moved.Translate(-3,8,125);
        foreach(double time in new[]{125d,180,319.5,320,350})
            CheckOffset(reference.Sample(time),moved.Sample(time),1,10,
                $"successive translations preserve the original segment at {time:F1}ms",check);

        // Translate, then reverse at a later point. The translated path must
        // still match the reference before reversal, and both branches must
        // carry size velocity continuously into the reverse segment.
        reference=new MotionController(initial);
        moved=new MotionController(initial);
        reference.Retarget(peek,0,320);
        moved.Retarget(peek,0,320);
        const float reverseDx=9, reverseDy=6;
        const double reverseAt=140;
        reference.Sample(90);
        moved.Translate(reverseDx,reverseDy,90);
        const double epsilonMs=.02;
        var beforeReference=reference.Sample(reverseAt-epsilonMs);
        var beforeMoved=moved.Sample(reverseAt-epsilonMs);
        CheckOffset(beforeReference,beforeMoved,reverseDx,reverseDy,
            "translated path remains aligned before reversal",check);
        var turnReference=reference.Sample(reverseAt);
        var turnMoved=moved.Sample(reverseAt);
        CheckOffset(turnReference,turnMoved,reverseDx,reverseDy,
            "translated path preserves the presented pose at reversal",check);

        reference.Retarget(initial,reverseAt,240);
        moved.Retarget(initial with {X=initial.X+reverseDx,Y=initial.Y+reverseDy},reverseAt,240);
        check(reference.Sample(reverseAt)==turnReference&&moved.Sample(reverseAt)==turnMoved,
            "reverse retarget keeps each translated pose at the interruption point");
        var afterReference=reference.Sample(reverseAt+epsilonMs);
        var afterMoved=moved.Sample(reverseAt+epsilonMs);
        CheckOffset(afterReference,afterMoved,reverseDx,reverseDy,
            "reverse retarget carries the translated pose forward",check);
        CheckVelocity(turnMoved.Height,beforeMoved.Height,afterMoved.Height,epsilonMs,8,.02,
            "translated height velocity remains continuous through reversal",check);
        CheckVelocity(turnMoved.SurfaceTension,beforeMoved.SurfaceTension,afterMoved.SurfaceTension,
            epsilonMs,.2,.05,"translated strain velocity remains continuous through reversal",check);
        foreach(double time in new[]{reverseAt+epsilonMs,180d,260,379.5,380})
            CheckOffset(reference.Sample(time),moved.Sample(time),reverseDx,reverseDy,
                $"translated reverse reaches its matching pose at {time:F1}ms",check);
        check(!reference.Active&&!moved.Active,
            "translated reverse settles at its requested deadline");

        // Reduced motion and an already-settled controller stay inactive and
        // apply translation as an exact target jump.
        var reduced=new MotionController(initial);
        reduced.Retarget(peek,0,280,true);
        reduced.Translate(dx,dy,0);
        check(!reduced.Active&&reduced.Current==reduced.Target
            &&reduced.Target.X==peek.X+dx&&reduced.Target.Y==peek.Y+dy
            &&reduced.Target.Width==peek.Width&&reduced.Target.Height==peek.Height,
            "reduced-motion translation remains an exact inactive target");

        var settled=new MotionController(initial);
        settled.Retarget(peek,0,280);
        settled.Sample(280);
        settled.Translate(-5,11,281);
        check(!settled.Active&&settled.Current==settled.Target
            &&settled.Target.X==peek.X-5&&settled.Target.Y==peek.Y+11
            &&settled.Target.Width==peek.Width&&settled.Target.Height==peek.Height,
            "settled translation jumps to the exact offset target without restarting motion");
    }

    private static void CheckOffset(WidgetPose reference,WidgetPose moved,float dx,float dy,
        string name,Action<bool,string> check)
    {
        check(Math.Abs((moved.X-reference.X)-dx)<.001f
            &&Math.Abs((moved.Y-reference.Y)-dy)<.001f,
            $"{name}: position offset is preserved");
        check(SameBits(moved.Width,reference.Width)
            &&SameBits(moved.Height,reference.Height)
            &&SameBits(moved.Radius,reference.Radius)
            &&SameBits(moved.Opacity,reference.Opacity)
            &&SameBits(moved.SurfaceTension,reference.SurfaceTension),
            $"{name}: width, height, radius, opacity, and strain are bitwise unchanged");
    }

    private static void CheckVelocity(float turn,float before,float after,double epsilonMs,
        double absoluteTolerance,double relativeTolerance,string name,Action<bool,string> check)
    {
        double incoming=(turn-before)/(epsilonMs/1000);
        double outgoing=(after-turn)/(epsilonMs/1000);
        check(Math.Abs(incoming-outgoing)<Math.Max(absoluteTolerance,Math.Abs(incoming)*relativeTolerance),
            $"{name} ({incoming:F2}/{outgoing:F2} units/s)");
    }

    private static bool SameBits(float left,float right)=>
        BitConverter.SingleToInt32Bits(left)==BitConverter.SingleToInt32Bits(right);
}

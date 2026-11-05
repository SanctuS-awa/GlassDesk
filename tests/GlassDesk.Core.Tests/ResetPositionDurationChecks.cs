using GlassDesk.Core;
using System;
using System.Collections.Generic;
using System.Linq;

// Fixed reset-position duration and event-order regression checks. No frame sweep.
internal static class ResetPositionDurationChecks
{
    public static void Run(Action<bool, string> check)
    {
        var monitor = new MonitorDescriptor(
            "fixed", new(0, 0, 1920, 1080), new(0, 0, 1920, 1040), 1.0f, true);

        // Capsule starts at a different valid location. Reset must still start
        // the position animation even though ChangeForm(Capsule) is a no-op.
        var capsule = new WidgetController(new WidgetPreferences(), monitor, new(400, 100));
        var capsuleStart = capsule.Motion.Current;
        var capsuleEvents = new List<string>();
        capsule.FormChanged += _ => capsuleEvents.Add("FormChanged");
        capsule.PresentationChanged += _ => capsuleEvents.Add("PresentationChanged");
        capsule.PlacementCommitted += (_, _) => capsuleEvents.Add("PlacementCommitted");
        capsule.ResetPosition(monitor, 1000);
        var capsuleTarget = capsule.Motion.Target;
        check(capsule.Motion.Active && Math.Abs(capsuleTarget.X - capsuleStart.X) > 1,
            "Capsule reset starts a position animation despite already being Capsule");
        check(IsValidResetCapsuleTarget(capsuleTarget, monitor),
            "Capsule reset target is finite, normalized, and inside the work area");
        check(capsuleEvents.SequenceEqual(new[] { "PlacementCommitted", "PresentationChanged" }),
            "Capsule reset preserves placement/presentation event order");
        capsule.Motion.Sample(1119.9);
        check(capsule.Motion.Active, "Capsule reset remains active just before 120 ms");
        capsule.Motion.Sample(1120);
        check(!capsule.Motion.Active, "Capsule reset settles at 120 ms");

        // Peek baseline is settled before reset; old source remains active at
        // +120 ms because its 240 ms ChangeForm target wins the same-target retarget.
        var peek = new WidgetController(new WidgetPreferences(), monitor, new(400, 100));
        peek.ChangeForm(WidgetForm.Peek, 0);
        peek.Motion.Sample(300);
        var peekEvents = new List<string>();
        peek.FormChanged += _ => peekEvents.Add("FormChanged");
        peek.PresentationChanged += _ => peekEvents.Add("PresentationChanged");
        peek.PlacementCommitted += (_, _) => peekEvents.Add("PlacementCommitted");
        peek.ResetPosition(monitor, 1000);
        var peekTarget = peek.Motion.Target;
        check(IsValidResetCapsuleTarget(peekTarget, monitor),
            "Peek reset target is finite, normalized, and inside the work area");
        check(peekEvents.SequenceEqual(new[] { "FormChanged", "PresentationChanged", "PlacementCommitted", "PresentationChanged" }),
            "Peek reset preserves form/presentation/placement event order");
        peek.Motion.Sample(1119.9);
        check(peek.Motion.Active, "Peek reset remains active just before 120 ms");
        peek.Motion.Sample(1120);
        check(!peek.Motion.Active, "Peek reset settles at 120 ms");

        // Details is likewise settled before the same fixed reset deadline.
        var details = new WidgetController(new WidgetPreferences(), monitor, new(400, 100));
        details.ChangeForm(WidgetForm.Details, 0);
        details.Motion.Sample(400);
        var detailsEvents = new List<string>();
        details.FormChanged += _ => detailsEvents.Add("FormChanged");
        details.PresentationChanged += _ => detailsEvents.Add("PresentationChanged");
        details.PlacementCommitted += (_, _) => detailsEvents.Add("PlacementCommitted");
        details.ResetPosition(monitor, 1000);
        var detailsTarget = details.Motion.Target;
        check(IsValidResetCapsuleTarget(detailsTarget, monitor),
            "Details reset target is finite, normalized, and inside the work area");
        check(peekTarget == detailsTarget && capsuleTarget == detailsTarget,
            "all starting forms reset to the same capsule pose");
        check(detailsEvents.SequenceEqual(new[] { "FormChanged", "PresentationChanged", "PlacementCommitted", "PresentationChanged" }),
            "Details reset preserves form/presentation/placement event order");
        details.Motion.Sample(1119.9);
        check(details.Motion.Active, "Details reset remains active just before 120 ms");
        details.Motion.Sample(1120);
        check(!details.Motion.Active, "Details reset settles at 120 ms");
    }

    private static bool IsValidResetCapsuleTarget(WidgetPose pose, MonitorDescriptor monitor)
    {
        var area = monitor.WorkArea;
        float scale = monitor.Scale > 0 ? monitor.Scale : 1;
        return float.IsFinite(pose.X) && float.IsFinite(pose.Y)
            && float.IsFinite(pose.Width) && float.IsFinite(pose.Height) && float.IsFinite(pose.Radius)
            && pose.Width > 0 && pose.Height > 0 && pose.Radius >= 0 && pose.Radius <= pose.Height / 2
            && pose.X >= area.X && pose.Y >= area.Y
            && pose.X + pose.Width * scale <= area.Right && pose.Y + pose.Height * scale <= area.Bottom
            && Math.Abs(pose.X - (area.X + area.Width / 2.0 - pose.Width * scale / 2.0)) < .01
            && Math.Abs(pose.Y - (area.Y + 24 * scale)) < .01;
    }
}

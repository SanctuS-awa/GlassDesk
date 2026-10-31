using GlassDesk.Core;

static class WidgetControllerGeometryChecks
{
    public static void Run(Action<bool, string> check)
    {
        var compactMonitor = new MonitorDescriptor("compact", new(0, 0, 1366, 720),
            new(0, 0, 1366, 720), 2, true);
        var compact = new WidgetController(new(), compactMonitor, new(1200, 650));
        compact.SetDetailsPageHeight(512, 0);
        compact.ChangeForm(WidgetForm.Details, 1);
        var compactPose = compact.Motion.Target;
        bool compactTransitionFits = true;
        for (int now = 1; now <= 361; now += 10)
            compactTransitionFits &= InsideWorkArea(compact.Motion.Sample(now), compactMonitor);
        check(compact.Layout is { Width: 360, Height: 512 } && compactPose.Height == 360 &&
              InsideWorkArea(compactPose, compactMonitor) && compactTransitionFits,
            "512 DIP Home page and its expansion fit the 1366x720 physical work area at 200% while retaining its logical canvas");

        var highDpiMonitor = new MonitorDescriptor("4x", new(0, 0, 3840, 2160),
            new(0, 0, 3840, 2160), 4, true);
        var ordinary4x = new WidgetController(new(), highDpiMonitor, new(3800, 2100));
        ordinary4x.SetDetailsPageHeight(512, 0);
        ordinary4x.ChangeForm(WidgetForm.Details, 1);
        check(ordinary4x.Layout is { Width: 360, Height: 512 } &&
              ordinary4x.Motion.Target is { Width: 360, Height: 512 } &&
              ordinary4x.LogicalLayoutFitsPhysicalBodyLimit is null,
            "ordinary 4x controller preserves its 360x512 DIP layout when no backend limit is enabled");

        var experimental4x = new WidgetController(new(), highDpiMonitor, new(3800, 2100));
        experimental4x.SetPhysicalBodyLimit(1024, 0);
        experimental4x.SetDetailsPageHeight(512, 1);
        experimental4x.ChangeForm(WidgetForm.Details, 2);
        var experimentalPose = experimental4x.Motion.Target;
        bool experimentalTransitionFits = true;
        for (int now = 2; now <= 362; now += 10)
        {
            var pose = experimental4x.Motion.Sample(now);
            experimentalTransitionFits &= FitsNativeBody(pose, highDpiMonitor) && InsideWorkArea(pose, highDpiMonitor);
        }
        check(experimental4x.Layout is { Width: 360, Height: 512 } &&
              experimental4x.LogicalLayoutFitsPhysicalBodyLimit == false &&
              FitsNativeBody(experimentalPose, highDpiMonitor) &&
              experimentalPose.Width * highDpiMonitor.Scale > 1023 &&
              experimentalPose.Height * highDpiMonitor.Scale > 1023 &&
              InsideWorkArea(experimentalPose, highDpiMonitor) && experimentalTransitionFits,
            "opt-in 4x cap bounds Details body and animation while reporting its 360x512 DIP logical layout unsupported");

        var experimentalPeek = new WidgetController(new(), highDpiMonitor, new(1920, 48));
        experimentalPeek.SetPhysicalBodyLimit(1024, 0);
        experimentalPeek.UpdateContent(CapsuleContentMode.Music, true, true, 296, 1);
        experimentalPeek.ChangeForm(WidgetForm.Peek, 2);
        check(experimentalPeek.Layout is { Width: 320, Height: 176 } &&
              experimentalPeek.LogicalLayoutFitsPhysicalBodyLimit == false &&
              experimentalPeek.Motion.Target.Width < experimentalPeek.Layout.Width &&
              FitsNativeBody(experimentalPeek.Motion.Target, highDpiMonitor),
            "4x music Peek remains 320 DIP logical geometry and is reported unsupported when its bounded pose would shrink");

        var normalMonitor = new MonitorDescriptor("normal", new(0, 0, 3840, 2160),
            new(0, 0, 3840, 2160), 2, true);
        var normal = new WidgetController(new(), normalMonitor, new(1920, 48));
        normal.UpdateContent(CapsuleContentMode.Music, true, true, 256, 0);
        normal.ChangeForm(WidgetForm.Details, 1);
        check(normal.Motion.Target.Width == 360 && normal.Motion.Target.Height == 476,
            "ordinary 200% music Details presentation keeps its 360x476 DIP size");
        var short3xMonitor=new MonitorDescriptor("short3x",new(0,0,2560,900),new(0,0,2560,900),2.5f,true);
        var short3x=new WidgetController(new(),short3xMonitor,new(1280,24));
        short3x.SetPhysicalBodyLimit(1024,0);short3x.SetDetailsPageHeight(512,0);short3x.ChangeForm(WidgetForm.Details,1);
        check(short3x.LogicalLayoutFitsPhysicalBodyLimit==true&&short3x.Motion.Target.Height==360,
            "physical cap checks the scrollable work-area viewport, not the offscreen natural Home height");
    }

    private static bool InsideWorkArea(WidgetPose pose, MonitorDescriptor monitor)
    {
        var area = monitor.WorkArea;
        double right = pose.X + pose.Width * monitor.Scale;
        double bottom = pose.Y + pose.Height * monitor.Scale;
        return pose.X >= area.X && pose.Y >= area.Y &&
               right <= area.Right + .001 && bottom <= area.Bottom + .001;
    }

    private static bool FitsNativeBody(WidgetPose pose, MonitorDescriptor monitor) =>
        pose.Width * monitor.Scale < 1024 && pose.Height * monitor.Scale < 1024;
}

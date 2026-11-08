namespace GlassDesk.Core;

public readonly record struct WidgetAnchor(double CenterX,double TopY);

public sealed class WidgetController
{
    private WidgetPreferences preferences;
    private MonitorDescriptor monitor;
    private WidgetAnchor anchor;
    private double? hoverAt,leaveAt;
    private bool inside,down,bodyClickCancelled,manualHidden,fullscreen,fullscreenOverride;
    private int pressedControl;
    private int pressX,pressY;
    private nint lastExternalWindow;
    private CapsuleContentMode capsuleMode=CapsuleContentMode.Clock;
    private bool mediaConnected,companion=true;
    private float musicCapsuleWidth=256;
    private float? detailsPageWidth,detailsPageHeight;
    private float? physicalBodyLimitPixels;
    public WidgetForm Form {get;private set;}=WidgetForm.Capsule;
    public bool Visible {get;private set;}=true;
    public bool IsDragging=>false;
    public WidgetAnchor Anchor=>anchor;
    public MonitorDescriptor Monitor=>monitor;
    /// <summary>Null when no backend physical-body limit is enabled; otherwise reports whether the logical layout fits.</summary>
    public bool? LogicalLayoutFitsPhysicalBodyLimit=>physicalBodyLimitPixels is float limit
        ?Math.Min(Layout.Width,monitor.WorkArea.Width/Scale)*Scale<=limit
            &&Math.Min(Layout.Height,monitor.WorkArea.Height/Scale)*Scale<=limit:null;
    public MotionController Motion {get;}
    public bool NeedsTick=>hoverAt.HasValue || leaveAt.HasValue || Motion.Active;
    public event Action<WidgetForm>? FormChanged;
    public event Action<bool>? VisibilityChanged;
    public event Action<WidgetPose>? PresentationChanged;
    public event Action<int>? ActionRequested;
#pragma warning disable CS0067 // Kept for old callers; fixed placement never emits a placement commit.
    public event Action<WidgetAnchor,MonitorDescriptor>? PlacementCommitted;
#pragma warning restore CS0067
    public WidgetController(WidgetPreferences preferences,MonitorDescriptor monitor,WidgetAnchor anchor)
    {
        if(!monitor.Primary)throw new ArgumentException("Widget placement requires the primary monitor.",nameof(monitor));
        this.preferences=preferences.Validated();this.monitor=monitor;this.anchor=FixedAnchor(monitor);
        ClampAnchor(); Motion=new(TargetPose()); Motion.SetPoseConstraint(ConstrainPresentationPose);
    }
    private float Scale=>monitor.Scale>0?monitor.Scale:1;
    private static WidgetAnchor FixedAnchor(MonitorDescriptor value)
        =>new(value.WorkArea.X+value.WorkArea.Width/2.0,value.WorkArea.Y);
    public WidgetLayoutMetrics Layout
    {
        get
        {
            var metrics=WidgetLayoutMetrics.Resolve(Form,Form==WidgetForm.Capsule?capsuleMode:mediaConnected?CapsuleContentMode.Music:CapsuleContentMode.Clock,companion,musicCapsuleWidth);
            if (Form == WidgetForm.Details && (detailsPageWidth.HasValue || detailsPageHeight.HasValue))
                return metrics with
                {
                    Width=detailsPageWidth ?? metrics.Width,
                    Height=detailsPageHeight ?? metrics.Height,
                    Radius=detailsPageWidth.HasValue ? 32 : metrics.Radius
                };
            // The music Details page is a fixed 476 DIP; only real pages own a separate height.
            return metrics;
        }
    }
    /// <summary>A real non-music page owns its height independently of media connection changes.</summary>
    public void SetDetailsPageHeight(float? height, double now)
        =>SetDetailsPageSize(detailsPageWidth,height,now);
    public void SetDetailsPageSize(float? width,float? height,double now)
    {
        if (width.HasValue && (!float.IsFinite(width.Value) || width.Value < 64 || width.Value > 960))
            throw new ArgumentOutOfRangeException(nameof(width));
        if (height.HasValue && (!float.IsFinite(height.Value) || height.Value < 160 || height.Value > 512))
            throw new ArgumentOutOfRangeException(nameof(height));
        if (detailsPageWidth == width && detailsPageHeight == height) return;
        detailsPageWidth = width;
        detailsPageHeight = height;
        if (Form != WidgetForm.Details) return;
        ClampAnchor(); Motion.Retarget(TargetPose(), now, 160, preferences.ReducedMotion);
        PresentationChanged?.Invoke(Motion.Sample(now));
    }
    /// <summary>Enables a backend-specific physical body limit; null restores the uncapped geometry.</summary>
    public void SetPhysicalBodyLimit(float? pixels,double now)
    {
        if(pixels.HasValue&&(!float.IsFinite(pixels.Value)||pixels.Value<1))
            throw new ArgumentOutOfRangeException(nameof(pixels));
        if(physicalBodyLimitPixels==pixels)return;
        physicalBodyLimitPixels=pixels;
        Motion.Retarget(TargetPose(),now,120,preferences.ReducedMotion);
        PresentationChanged?.Invoke(Motion.Sample(now));
    }
    public void UpdateContent(CapsuleContentMode mode,bool connected,bool hasCompanion,float measuredMusicWidth,double now,bool timeline=false)
    {
        var previous=Layout;
        capsuleMode=mode;mediaConnected=connected;companion=hasCompanion;musicCapsuleWidth=Math.Clamp(measuredMusicWidth,208,296);
        _=timeline; // Timeline availability no longer changes the Details height (fixed 476).
        if(previous==Layout)return;
        ClampAnchor();Motion.Retarget(TargetPose(),now,160,preferences.ReducedMotion);
        PresentationChanged?.Invoke(Motion.Sample(now));
    }
    private WidgetPose TargetPose()
    {
        float width=Layout.Width;
        float height=Layout.Height;
        width=Math.Min(width,monitor.WorkArea.Width/Scale);
        height=Math.Min(height,monitor.WorkArea.Height/Scale);
        if(physicalBodyLimitPixels is float limit)
        {
            // Leave one float ULP below the opt-in backend limit so DPI conversion
            // cannot round an exact physical-pixel boundary up by one pixel.
            float limitDip=MathF.BitDecrement(limit/Scale);
            width=Math.Min(width,limitDip);
            height=Math.Min(height,limitDip);
        }
        float x=(float)Math.Clamp(anchor.CenterX-width*Scale/2,monitor.WorkArea.X,Math.Max(monitor.WorkArea.X,monitor.WorkArea.Right-width*Scale));
        float y=(float)Math.Clamp(anchor.TopY,monitor.WorkArea.Y,Math.Max(monitor.WorkArea.Y,monitor.WorkArea.Bottom-height*Scale));
        return new(x,y,width,height,Math.Min(Layout.Radius,height/2));
    }
    private WidgetPose ConstrainPresentationPose(WidgetPose pose)
    {
        // The spring keeps its raw velocity, while the submitted frame stays
        // inside the monitor and the capture backend's per-side pixel budget.
        // This matters when an underdamped expansion briefly crosses its target.
        float scale=Scale;
        var area=monitor.WorkArea;
        float maxWidth=area.Width/scale,maxHeight=area.Height/scale;
        if(physicalBodyLimitPixels is float limit)
        {
            float limitDip=MathF.BitDecrement(limit/scale);
            maxWidth=Math.Min(maxWidth,limitDip);
            maxHeight=Math.Min(maxHeight,limitDip);
        }
        float width=Math.Clamp(pose.Width,1,Math.Max(1,maxWidth));
        float height=Math.Clamp(pose.Height,1,Math.Max(1,maxHeight));
        double maxX=Math.Max((double)area.X,area.Right-(double)width*scale);
        double maxY=Math.Max((double)area.Y,area.Bottom-(double)height*scale);
        return pose with
        {
            X=(float)Math.Clamp((double)pose.X,area.X,maxX),
            Y=(float)Math.Clamp((double)pose.Y,area.Y,maxY),
            Width=width,
            Height=height,
            Radius=Math.Min(pose.Radius,height/2)
        };
    }
    private void ClampAnchor()
    {
        anchor=FixedAnchor(monitor);
    }
    public void ChangeForm(WidgetForm next,double now)
        => ChangeForm(next,now,null);
    private void ChangeForm(WidgetForm next,double now,int? durationOverrideMs)
    {
        if (Form==next) return;
        var previous=Form; Form=next; hoverAt=leaveAt=null;
        int duration=durationOverrideMs??(next==WidgetForm.Capsule?240:next==WidgetForm.Peek?280:previous==WidgetForm.Capsule?360:320);
        Motion.Retarget(TargetPose(),now,duration,preferences.ReducedMotion);
        FormChanged?.Invoke(next);PresentationChanged?.Invoke(Motion.Sample(now));
    }
    public void UpdatePreferences(WidgetPreferences value,double now)
    {
        preferences=value.Validated();
        if(inside && Form==WidgetForm.Capsule && !down) hoverAt=now+preferences.HoverDelayMs;
        if(!inside && Form==WidgetForm.Peek && !down) leaveAt=now+preferences.LeaveDelayMs;
        Motion.Retarget(TargetPose(),now,120,preferences.ReducedMotion); ApplyVisibility(now);
    }
    public void SetMonitor(MonitorDescriptor next,double now,bool migrate=false)
    {
        if(!next.Primary)return;
        if (next==monitor) return;
        monitor=next;ClampAnchor();
        if(migrate) Motion.Jump(TargetPose());
        else Motion.Retarget(TargetPose(),now,120,preferences.ReducedMotion);
        PresentationChanged?.Invoke(Motion.Sample(now));
    }
    public void ResetPosition(MonitorDescriptor next,double now)
    {
        if(!next.Primary)return;
        monitor=next;ClampAnchor();
        if(Form==WidgetForm.Capsule) Motion.Retarget(TargetPose(),now,120,preferences.ReducedMotion);
        else ChangeForm(WidgetForm.Capsule,now,120);
        PresentationChanged?.Invoke(Motion.Sample(now));
    }
    public void RestoreAnchor(WidgetAnchor value,double now)
    {
        _=value;anchor=FixedAnchor(monitor);Motion.Jump(TargetPose());PresentationChanged?.Invoke(Motion.Current);
    }
    private void ApplyVisibility(double now)
    {
        bool next=fullscreen&&preferences.AvoidFullscreen?fullscreenOverride:!manualHidden;
        if (Visible==next) return;
        Visible=next;hoverAt=leaveAt=null; down=bodyClickCancelled=false;
        if(next) ChangeForm(WidgetForm.Capsule,now);
        VisibilityChanged?.Invoke(next);
    }
    public void CollapseAfterOutsidePress(double now)
    {inside=down=bodyClickCancelled=false;hoverAt=leaveAt=null;ChangeForm(WidgetForm.Capsule,now);}
    public void ToggleVisibility(double now)
    {
        if(fullscreen && preferences.AvoidFullscreen)
        {
            fullscreenOverride=!fullscreenOverride;
        }
        else manualHidden=!manualHidden;
        ApplyVisibility(now);
    }
    public void ApplySystemScene(SystemScene scene,double now)
    {
        bool changedWindow=scene.ForegroundWindow!=lastExternalWindow;
        lastExternalWindow=scene.ForegroundWindow;
        if(fullscreen!=scene.Fullscreen)
        {
            fullscreen=scene.Fullscreen;fullscreenOverride=false;
            if(!fullscreen) ChangeForm(WidgetForm.Capsule,now);
        }
        else if(changedWindow && scene.Maximized && !fullscreen) ChangeForm(WidgetForm.Capsule,now);
        ApplyVisibility(now);
    }
    public void HandlePointer(HostPointerEvent e,int controlId,double now)
    {
        if(!Visible && e.Kind is not HostEventKind.Invoke) return;
        switch(e.Kind)
        {
            case HostEventKind.Enter:
                inside=true;leaveAt=null;
                if(Form==WidgetForm.Capsule && !down) hoverAt=now+preferences.HoverDelayMs;
                break;
            case HostEventKind.Leave:
                inside=false;hoverAt=null;
                if(Form==WidgetForm.Peek && !down) leaveAt=now+preferences.LeaveDelayMs;
                break;
            case HostEventKind.Down:
                down=true;bodyClickCancelled=false;pressedControl=controlId;pressX=e.ScreenX;pressY=e.ScreenY;hoverAt=leaveAt=null;
                break;
            case HostEventKind.Move:
                CancelLargeBodyClick(e.ScreenX,e.ScreenY);
                break;
            case HostEventKind.Up:
                if(!down) break;
                CancelLargeBodyClick(e.ScreenX,e.ScreenY);
                down=false;
                if(controlId==pressedControl&&!(pressedControl==WidgetActions.Body&&bodyClickCancelled))Invoke(controlId,now);
                bodyClickCancelled=false;
                if(!inside && Form==WidgetForm.Peek) leaveAt=now+preferences.LeaveDelayMs;
                break;
            case HostEventKind.CaptureLost:
                down=bodyClickCancelled=false;
                if(!inside && Form==WidgetForm.Peek) leaveAt=now+preferences.LeaveDelayMs;
                break;
            case HostEventKind.Invoke: Invoke(controlId,now);break;
        }
    }
    private void CancelLargeBodyClick(int screenX,int screenY)
    {
        if(!down||pressedControl!=WidgetActions.Body)return;
        double dx=screenX-pressX,dy=screenY-pressY;
        if(dx*dx+dy*dy>36*Scale*Scale)bodyClickCancelled=true;
    }
    private void Invoke(int control,double now)
    {
        if(control==WidgetActions.Body) ChangeForm(WidgetForm.Details,now);
        else if(control==WidgetActions.Close) ChangeForm(WidgetForm.Capsule,now);
        else ActionRequested?.Invoke(control);
    }
    public void Tick(double now)
    {
        if(hoverAt is double hover && now>=hover) {hoverAt=null;if(inside&&!down)ChangeForm(WidgetForm.Peek,now);}
        if(leaveAt is double leave && now>=leave) {leaveAt=null;if(!inside&&!down)ChangeForm(WidgetForm.Capsule,now);}
        if(Motion.Active) PresentationChanged?.Invoke(Motion.Sample(now));
    }
}

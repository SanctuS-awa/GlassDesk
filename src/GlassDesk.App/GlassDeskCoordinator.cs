using System.Diagnostics;
using GlassDesk.Core;
using GlassDesk.NativeBridge;
using GlassDesk.Platform;
using Microsoft.UI.Dispatching;
using Windows.System.Power;
using Windows.UI.ViewManagement;
using GlassDesk.Plugins;
using System.Text.Json;

namespace GlassDesk.App;

internal sealed partial class GlassDeskCoordinator : ISettingsCoordinator,IDisposable
{
    readonly LaunchOptions options;
    readonly Action exit;
    readonly DispatcherQueue queue=DispatcherQueue.GetForCurrentThread();
    readonly UiDispatcher dispatcher;
    readonly PreferenceStore store;
    readonly Diagnostics diagnostics=new();
    readonly SemaphoreSlim preferenceGate=new(1,1);
    readonly NativeWidgetHost host;
    readonly WidgetScene scene;
    readonly WidgetController controller;
    readonly SystemIntegration system;
    readonly ForegroundObserver foreground;
    readonly OutsidePointerObserver outside;
    long openSequence, geometryVersion;
    readonly DispatcherQueueTimer companionIdleTimer;
    WidgetPreferences? appearancePreview;
    readonly IMediaAdapter media;
    readonly ProcessAudioVisualizationSource audio;
    readonly PluginHost plugins;
    readonly CapsuleContentPolicy contentPolicy=new();
    readonly DispatcherQueueTimer motionTimer,clockTimer,timelineTimer,graphicsRetryTimer,pauseTimer,materialSnapshotTimer,cursorTimer,mediaStateTimer;
    WidgetReadabilityDecision readability = new(false,false,true,0,"Manual black text; composed contrast not measured.");
    WidgetReadabilityDecision clockReadability = new(false,false,true,0,"Manual black text; composed contrast not measured.");
    int materialSnapshotAttempts;
    readonly UISettings uiSettings=new();
    readonly Stopwatch uptime=Stopwatch.StartNew();
    readonly MonitorService monitorService=new();
    IReadOnlyList<MonitorDescriptor> monitors;
    SettingsWindow? settings;
    ResidencyProbe? qa;
    QaSnapshotRequests? snapshotRequests;
#if GLASSDESK_UI_PREVIEW && GLASSDESK_SYSTEM_GLASS_PREVIEW
    QaTransportFeedback? qaTransportFeedback;
#endif
    bool disposed,quitting,solid,seeking,applyingHost,nativeRecoveryPending;
    bool effectsSubscribed,animationsSubscribed,powerSubscribed;
    string seekToken="";
    string? notice;
    // Widget persistence failures survive unrelated successes (a confirmed play command must
    // not clear a "configuration was not saved" warning) until the next successful save.
    string? pageSaveNotice;
    uint previousDpi;
    int graphicsRetries;
    bool pointerDown;
    int pressedAction=-1;
    CompanionInteraction companionInteraction;
    long companionInteractionSerial;
    public WidgetPreferences Preferences {get;private set;}
    public MediaSnapshot Media=>media.Current;
    public string Status=>notice??(Media.Connected?(Media.Timeline?.Valid==true?"已连接网易云音乐。":"已连接网易云音乐；当前客户端未提供播放时间轴。") :"未连接网易云音乐，当前显示本地时间。");
    public string? ConnectionIssue=>notice??Media.Notice;
    public bool IsWidgetVisible=>controller.Visible;
    public bool IsUiPreview=>options.UiPreview;
    public bool UsesLiquidGlassCapture=>host.IsLiquidGlassCapture;
    public Windows.Graphics.IGeometrySource2D CreatePreviewGeometry(System.Numerics.Vector2[] points)=>host.CreatePolygonPath(points,.25f);
    public IReadOnlyList<PluginStatus> Plugins=>plugins.Statuses;
    public IReadOnlyList<PluginContentPage> PluginPages=>plugins.Pages;
    public string AudioStatus=>audio.Current.Reason??audio.Current.State.ToString();
    public string LiquidGlassStatus=>host.LiquidGlassStatus;
    public event Action? Changed;
    double Now=>uptime.Elapsed.TotalMilliseconds;
    WidgetPreferences EffectivePreferences=>Preferences with {CompanionColor=appearancePreview?.CompanionColor??Preferences.CompanionColor,CompanionAppearanceId=appearancePreview?.CompanionAppearanceId??Preferences.CompanionAppearanceId,ReducedMotion=Preferences.ReducedMotion||!uiSettings.AnimationsEnabled||solid};

    public GlassDeskCoordinator(LaunchOptions options,Action exit)
    {
        this.options=options;this.exit=exit;dispatcher=new(queue);store=new(options.DataDirectory);
        Preferences=store.Load() with {MonitorId=null,AnchorX=.5,AnchorY=0};notice=store.TakeLoadNotice();monitors=monitorService.GetMonitors();
        pages=new(Preferences);
        var monitor=monitors.First(m=>m.Primary);
        var anchor=new WidgetAnchor(monitor.WorkArea.X+monitor.WorkArea.Width/2.0,monitor.WorkArea.Y);
        controller=new(Preferences,monitor,anchor);
        materialSession=options.DesktopSession??(options.OwnedImageGlass||options.LiquidGlassCaptureGlass||options.LiquidGlassCapturePreview
            ?throw new InvalidOperationException("实验必须在 XAML 启动前完成会话授权。")
            :DesktopMaterialLaunch.CreateSession(options));
        PrepareExperimentalCompositorGuard();
        host=new NativeWidgetHost(materialSession,options.IncidentHostShortTest,options.LiquidGlassCapturePreview);scene=new(host){CaptureTypographyDiagnostics=options.QaReport is not null};previousDpi=host.Context.Dpi;
        if(host.IsLiquidGlassCapture)controller.SetPhysicalBodyLimit(1024,Now);
        host.SetLiquidGlassEnabled(Preferences.LiquidGlassEnabled);
        scene.RenderingFailed+=error=>RecoverGraphics(error,"artwork-transition");
        if(options.UiPreview)
        {
            string version=typeof(GlassDeskCoordinator).Assembly.GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute),false)
                .OfType<System.Reflection.AssemblyInformationalVersionAttribute>().FirstOrDefault()?.InformationalVersion.Split('+')[0]??"preview";
            host.SetTitle($"GlassDesk UI Preview · {version}");
        }
        system=new(host.Handle,dispatcher,diagnostics,options.Identity);foreground=new(dispatcher);
#if GLASSDESK_UI_PREVIEW && GLASSDESK_SYSTEM_GLASS_PREVIEW
        media=options.QaSyntheticMedia?new QaSyntheticMediaAdapter(options.QaSyntheticMediaMode):new NetEaseMediaAdapter(dispatcher,diagnostics);
#else
        media=new NetEaseMediaAdapter(dispatcher,diagnostics);
#endif
        audio=new(dispatcher);plugins=new(dispatcher,store.DirectoryPath);
        outside=new(dispatcher,press=>
        {
            if(disposed||quitting||CandidateGraphicsStopped||!OutsidePointerSnapshot.BelongsToVisibleDetails(press,openSequence,controller.Visible,controller.Form))return;
            if(press.OutsideDismissRegion&&homeNavigation is not null)
            {
                CloseHomeNavigationWindow();
                if(scene.HomeNavExpanded){scene.SetHomeNavExpanded(false);Render();}
            }
            if(!press.OutsideRegisteredRegion)return;
#if GLASSDESK_UI_PREVIEW && GLASSDESK_SYSTEM_GLASS_PREVIEW
            // Explicit visual QA holds one real form without moving the user's
            // mouse or repeatedly reopening it while they use another application.
            // This mode is never evidence of outside-click/hover acceptance.
            if(options.QaVisualState is not null)return;
#endif
            pointerDown=false;pressedAction=-1;seeking=false;
            CommitBoardOnLeave();
            scene.SetPointerFeedback(-1);scene.SetCapture(PointerCaptureOwner.None,Now);
            controller.CollapseAfterOutsidePress(Now);WakeMotion();
        },error=>diagnostics.RecordError("outside-pointer",error));
        companionIdleTimer=queue.CreateTimer();companionIdleTimer.IsRepeating=false;
        companionIdleTimer.Tick+=(_,_)=>{if(!disposed){TryScene(()=>scene.TickCompanion(Now));WakeMotion();}};
        audio.FrameChanged+=frame=>{if(!disposed)TryScene(()=>scene.UpdateAudio(frame));};
        plugins.CompanionChanged+=state=>{if(!disposed)TryScene(()=>{scene.SetMonotonicTime(Now);scene.SetCompanionState(state);WakeMotion();});};
        plugins.VisualsChanged+=visuals=>{if(!disposed)TryScene(()=>scene.SetPluginVisuals(visuals));};
        plugins.Changed+=()=>{if(!disposed){TryScene(()=>scene.SetPluginPages(plugins.Pages));Render();Changed?.Invoke();}};
        motionTimer=queue.CreateTimer();motionTimer.Interval=TimeSpan.FromMilliseconds(16);motionTimer.Tick+=(_,_)=>{if(CandidateGraphicsStopped){motionTimer.Stop();return;}controller.Tick(Now);bool homeAnim=false;TryScene(()=>{scene.TickCompanion(Now);homeAnim=scene.TickHome(Now)|scene.TickOwnedGlass(Now);});if(homeAnim)Render();if(!controller.NeedsTick&&!scene.SceneMotionActive){motionTimer.Stop();ScheduleCompanionIdle();}UpdateOutsideGeometry();};
        clockTimer=queue.CreateTimer();clockTimer.IsRepeating=false;clockTimer.Tick+=(_,_)=>Render();
        cursorTimer=queue.CreateTimer();cursorTimer.Interval=TimeSpan.FromMilliseconds(33);cursorTimer.Tick+=(_,_)=>UpdateCursorGaze();
        pauseTimer=queue.CreateTimer();pauseTimer.IsRepeating=false;pauseTimer.Tick+=(_,_)=>{if(contentPolicy.Tick(Now)){Render();WakeMotion();}UpdateContentTimers();};
        timelineTimer=queue.CreateTimer();timelineTimer.Interval=TimeSpan.FromMilliseconds(250);timelineTimer.Tick+=(_,_)=>TryScene(()=>scene.UpdateTimeline(Media,Stopwatch.GetTimestamp()));
        mediaStateTimer=queue.CreateTimer();mediaStateTimer.Interval=TimeSpan.FromMilliseconds(500);
        mediaStateTimer.Tick+=(_,_)=>{if(!disposed&&!quitting&&controller.Visible&&Media.Connected)RefreshMediaPlaybackState();};
        graphicsRetryTimer=queue.CreateTimer();graphicsRetryTimer.Interval=TimeSpan.FromSeconds(2);graphicsRetryTimer.IsRepeating=false;graphicsRetryTimer.Tick+=(_,_)=>Render();
        materialSnapshotTimer=queue.CreateTimer();materialSnapshotTimer.Interval=TimeSpan.FromSeconds(1);materialSnapshotTimer.IsRepeating=false;
        materialSnapshotTimer.Tick+=(_,_)=>
        {
            if(disposed||CandidateGraphicsStopped||options.QaMaterialFrame is null)return;
            try{host.SaveLiquidGlassDiagnosticFrame(options.QaMaterialFrame);}
            catch(Exception error){if(host.IsExperimentalDesktopMaterialCandidate){StopExperimentalDesktopMaterial(error,"qa-material");return;}diagnostics.RecordError("qa-material",error);if(++materialSnapshotAttempts<5)materialSnapshotTimer.Start();}
        };
        plugins.MaterialChanged+=(id,active)=>{if(!disposed&&!CandidateGraphicsStopped&&id=="liquidglass"){try{host.SetLiquidGlassEnabled(active);if(active&&options.QaMaterialFrame is not null){materialSnapshotAttempts=0;materialSnapshotTimer.Start();}}catch(Exception error){RecoverGraphics(error,"liquidglass");}Changed?.Invoke();}};
        controller.FormChanged+=form=>
        {
            if(CandidateGraphicsStopped)return;
            CloseHomeNavigationWindow();
            homeBackPressed=false;homePressedAction=-1;homeConsumeRelease=false;
            if(form!=WidgetForm.Details){CommitBoardOnLeave();CancelHomeFolderRead();}
            if(form==WidgetForm.Details)
            {
                // Opening a new presentation resets transient UI, not the last real page.
                // Never carry a transient hover/popover animation into a fresh expansion.
                scene.ResetHomeState();
            }
            else if(scene.HomePage)scene.ResetHomeState();
            Render();
            if(CandidateGraphicsStopped)return;
            if(form==WidgetForm.Details)scene.BeginDetails(++openSequence,Now);else scene.EndDetails();
            UpdateOutsideGeometry();UpdatePlugins();WakeMotion();
        };
        controller.VisibilityChanged+=visible=>{if(CandidateGraphicsStopped)return;if(!visible){CloseHomeNavigationWindow();CommitBoardOnLeave();}system.WidgetVisible=visible;host.SetVisible(visible);scene.SetSceneVisible(visible,Now);UpdateOutsideGeometry();contentPolicy.Tick(Now);Render();UpdatePlugins();Changed?.Invoke();};
        controller.PresentationChanged+=ApplyPresentation;
        controller.ActionRequested+=InvokeAction;
        host.PointerEvent+=OnPointer;
        host.WindowContextChanged+=OnWindowContext;
        host.NativeError+=reason=>
        {
            if(host.IsExperimentalDesktopMaterialCandidate){materialSession.MarkFault(reason);}
            if(disposed||quitting||nativeRecoveryPending||experimentalDesktopMaterialStopped)return;
            nativeRecoveryPending=true;
            if(!queue.TryEnqueue(()=>
            {
                nativeRecoveryPending=false;
                if(!disposed&&!quitting)RecoverGraphics(new System.Runtime.InteropServices.COMException(reason),"native");
            }))nativeRecoveryPending=false;
        };
        media.SnapshotChanged+=_=>{if(seeking&&seekToken!=Media.SessionToken){seeking=false;TryScene(()=>scene.SetSeekPreview(null));}contentPolicy.Update(Media,Now);Render();WakeMotion();UpdatePlugins();Changed?.Invoke();};
        system.ShowHideRequested+=ToggleVisibility;system.SettingsRequested+=ShowSettings;system.OpenMusicRequested+=OpenMusic;
        system.CollapseRequested+=()=>{controller.CollapseAfterOutsidePress(Now);WakeMotion();};system.ResetPositionRequested+=ResetPosition;system.QuitRequested+=Quit;
        system.DisplayChanged+=RefreshMonitors;system.Resumed+=OnResume;system.SystemSettingsChanged+=RefreshSystemSettings;
        foreground.SceneChanged+=state=>{
            if(disposed||quitting||CandidateGraphicsStopped)return;
#if GLASSDESK_UI_PREVIEW && GLASSDESK_SYSTEM_GLASS_PREVIEW
            if(options.QaVisualState is not null){if(controller.Visible)host.EnsureTopmost();return;}
#endif
            controller.ApplySystemScene(state,Now);if(controller.Visible)host.EnsureTopmost();WakeMotion();};
        // AccessibilitySettings.HighContrastChanged is unsupported in desktop apps.
        // The owned HWND receives WM_SETTINGCHANGE/WM_SYSCOLORCHANGE instead.
        effectsSubscribed=TrySubscribe(()=>uiSettings.AdvancedEffectsEnabledChanged+=OnAdvancedEffects);
        animationsSubscribed=TrySubscribe(()=>uiSettings.AnimationsEnabledChanged+=OnAnimations);
        powerSubscribed=TrySubscribe(()=>PowerManager.EnergySaverStatusChanged+=OnPower);
        InitializeWidgetBoard();
        RefreshSystemSettings();ApplyPresentation(controller.Motion.Current);Render();
        if(experimentalDesktopMaterialFailure is not null)System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(experimentalDesktopMaterialFailure).Throw();
        host.SetVisible(true);scene.SubmitOwnedGlass();
        if(!options.UiPreview&&Preferences.HotkeyKey!=0 && !system.TrySetHotkey(Preferences.HotkeyModifiers,Preferences.HotkeyKey,out var error))notice=error;
        diagnostics.RecordEvent("app","started");
        StartOwnedWallpaperRefresh();
    }
    public async void Start()
    {
        try
        {
            foreground.Start();foreground.Refresh();
            widgetLoadTask=LoadWidgetBoardAsync();await widgetLoadTask;
            if(disposed||quitting)return;
            await media.StartAsync();
            if(disposed||quitting)return;
            contentPolicy.Update(Media,Now);
            await plugins.ApplyPreferencesAsync(Preferences);
            await plugins.UpdateSceneAsync(PluginScene());
            Render();
            if(disposed||quitting||CandidateGraphicsStopped)return;
#if GLASSDESK_UI_PREVIEW && GLASSDESK_SYSTEM_GLASS_PREVIEW
            if(options.QaVisualState is { } qaVisualState)
            {
                controller.ChangeForm(qaVisualState=="peek"?WidgetForm.Peek:WidgetForm.Details,Now);
                WakeMotion();
                if(disposed||quitting||CandidateGraphicsStopped)return;
                diagnostics.RecordEvent("qa-visual-state",qaVisualState+"; synthetic media; outside click held; not input acceptance");
                qaTransportFeedback=new(options.DataDirectory!,queue,
                    ()=>disposed||quitting||CandidateGraphicsStopped,
                    feedback=>{
                        if(feedback is "next-details" or "previous-peek")
                        {
                            if(disposed||quitting||CandidateGraphicsStopped)return;
                            if(options.QaSyntheticMediaMode!="playlist"||media is not QaSyntheticMediaAdapter synthetic)
                                throw new InvalidOperationException("QA form/track transitions require the playlist synthetic-media adapter.");

                            bool next=feedback=="next-details";
                            scene.SetMonotonicTime(Now);
                            if(disposed||quitting||CandidateGraphicsStopped)return;
                            controller.ChangeForm(next?WidgetForm.Details:WidgetForm.Peek,Now);
                            if(disposed||quitting||CandidateGraphicsStopped)return;

                            Task<CommandResult> command=synthetic.ExecuteAsync(
                                next?MediaCommand.Next:MediaCommand.Previous,synthetic.Current.SessionToken);
                            if(!command.IsCompleted)
                                throw new InvalidOperationException("QA synthetic media commands must complete synchronously.");
                            CommandResult result=command.GetAwaiter().GetResult();
                            if(!result.Accepted||!result.Verified)
                                throw new InvalidOperationException(result.Error??"QA synthetic media command was not verified.");
                            if(disposed||quitting||CandidateGraphicsStopped)return;
                            WakeMotion();
                            return;
                        }
                        scene.SetMonotonicTime(Now);
                        scene.SetPointerFeedback(feedback=="rest"?-1:WidgetActions.Toggle,feedback=="pressed");
                        WakeMotion();
                    },scene.GetTransportVisualDiagnostics,
                    error=>StopExperimentalDesktopMaterial(error,"qa-feedback"));
            }
#endif
            if(options.QaSnapshotRequest is { } snapshotRequest)snapshotRequests=new(host,queue,snapshotRequest);
            if(!options.NoSettings && !Preferences.OnboardingSeen)
            {
                ShowSettings();
            }
        }
        catch(Exception error) {diagnostics.RecordError("start",error);notice="连接初始化失败，可以在设置中查看状态。";Render();Changed?.Invoke();}
        if(!disposed && options.QaReport is not null)
        {
            try { qa=new(queue,controller,()=>Now,WakeMotion,options.QaReport,options.QaCycles,scene.GetTypographyDiagnostics,()=>new {Body=readability,ClockPlate=clockReadability},options.IncidentHostShortTest,()=>new {Backend=materialSession.Backend.ToString(),Status=host.LiquidGlassStatus,Host=host.Context,OwnedImageSourceLoaded=host.OwnedImageAvailable,OwnedImagePresented=host.OwnedImagePresented,SystemLensSelected=host.IsLiquidGlassCapture,RuntimePixelAcquisition=false,Tripped=materialSession.IsExperimentTripped});qa.RecordContent(contentPolicy.Mode,contentPolicy.ForForm(controller.Form),Media.State,controller.Form,controller.Visible); }
            catch(Exception error) { diagnostics.RecordError("qa-start",error); }
        }
    }
    void WakeMotion(){if(disposed||quitting||CandidateGraphicsStopped)return;if(controller.NeedsTick||scene.SceneMotionActive){companionIdleTimer.Stop();if(!motionTimer.IsRunning)motionTimer.Start();}else ScheduleCompanionIdle();}
    void ScheduleCompanionIdle()
    {
        companionIdleTimer.Stop();
        if(disposed||quitting||CandidateGraphicsStopped)return;
        if(controller.Visible&&scene.NextCompanionWake is double wake){companionIdleTimer.Interval=TimeSpan.FromMilliseconds(Math.Max(1,wake-Now));companionIdleTimer.Start();}
    }
    void UpdateOutsideGeometry()
    {
        if(disposed||quitting||CandidateGraphicsStopped||controller.Form!=WidgetForm.Details||!controller.Visible){CloseHomeNavigationWindow();outside.Update(null);return;}
        homeNavigation?.RepositionNearOwner();
        var context=host.Context;float scale=context.Dpi/96f;
        outside.Update(new(openSequence,++geometryVersion,new(context.X,context.Y,context.Width*scale,context.Height*scale),
            controller.Motion.Current.Radius*scale,scene.CaptureOwner,OwnedWidgetRegions(),
            homeNavigation?.ScreenBounds,homeNavigation?.ScreenCornerRadius??0));
    }
    public void PreviewCompanionAppearance(uint color,string appearanceId)
    {
        if(disposed||quitting||CandidateGraphicsStopped)return;
        scene.SetMonotonicTime(Now);
        appearancePreview=Preferences with {CompanionColor=color|0xff000000,CompanionAppearanceId=CompanionAppearanceIds.Normalize(appearanceId)};
        plugins.PreviewBuiltinAppearance(color,appearanceId);
        if(plugins.IsBuiltinCompanionActive)scene.PreviewCompanionAppearance(color,appearanceId);
        Render();
    }
    PluginSceneState PluginScene()=>new(controller.Form,controller.Visible,Media,EffectivePreferences.ReducedMotion,Stopwatch.GetTimestamp(),companionInteraction,companionInteractionSerial);
    void CompanionReact(CompanionInteraction interaction)
    {
        companionInteraction=interaction;companionInteractionSerial++;UpdatePlugins();
    }
    async void UpdatePlugins()
    {
        if(disposed||quitting)return;
        try{await plugins.UpdateSceneAsync(PluginScene());}
        catch(Exception error){if(!disposed)diagnostics.RecordError("plugin-scene",error);}
    }
    void ApplyPresentation(WidgetPose pose)
    {
        if(disposed||applyingHost||CandidateGraphicsStopped||!EnsureExperimentalBodyFits())return;
        applyingHost=true;
        try
        {
            scene.SetSolidMaterial(solid);
            var frame=new HostFrame(pose.Width,pose.Height,pose.Radius,Preferences.Blur,Preferences.TintOpacity,solid){SurfaceTension=EffectivePreferences.ReducedMotion?0:pose.SurfaceTension};
            int screenX=checked((int)Math.Round(pose.X)),screenY=checked((int)Math.Round(pose.Y));
            if(host.IsExperimentalDesktopMaterialCandidate)
                host.ApplyPositionedFrame(frame,screenX,screenY);
            else
            {
                host.ApplyFrame(frame);
                if(CandidateGraphicsStopped)return;
                host.SetPlacement(screenX,screenY);
            }
            if(CandidateGraphicsStopped)return;
            scene.SetMonotonicTime(Now);scene.ApplyPresentation(pose);scene.SubmitOwnedGlass();UpdateOutsideGeometry();
            CheckOwnedWallpaperMonitor();
        }
        catch(Exception error){if(host.IsExperimentalDesktopMaterialCandidate)StopExperimentalDesktopMaterial(error,"presentation");else{diagnostics.RecordError("graphics",error);notice="图形暂时不可用，正在使用简化显示。";solid=true;}}
        finally{applyingHost=false;}
    }
    bool ApplySceneInk()
    {
        bool lightInk=!solid&&Preferences.TextInk==WidgetTextInk.White;
        const string reason="Manual text color; composed contrast not measured.";
        readability=new(lightInk,false,true,0,reason);clockReadability=readability;
        return !CandidateGraphicsStopped&&scene.SetTextTone(lightInk,false,lightInk,false);
    }
    void Render()
    {
        if(disposed||quitting||CandidateGraphicsStopped)return;
        try
        {
            contentPolicy.Tick(Now);
            scene.SetMonotonicTime(Now);
            scene.SetSolidMaterial(solid);
            ApplySceneInk();
            PrepareWidgetBoard();
            // Preview-only acceptance hook: shrink the real Details window so the responsive
            // compact layout is exercised end to end (the controller clamps to the work area).
            if(options.QaDetailsHeight is float previewDetailsHeight
                && controller.Form==WidgetForm.Details
                && pages.CurrentPageId==DetailsPageController.Music)
                controller.SetDetailsPageHeight(previewDetailsHeight,Now);
            controller.UpdateContent(contentPolicy.Mode,Media.Connected,Preferences.CompanionEnabled,scene.MeasureMusicCapsuleWidth(Media,Preferences),Now,Media.Timeline?.Valid==true);
            if(CandidateGraphicsStopped||!EnsureExperimentalBodyFits())return;
            float viewportHeight=Math.Min(controller.Layout.Height,controller.Monitor.WorkArea.Height/controller.Monitor.Scale);
            if(options.QaDetailsHeight is float qaHeight)viewportHeight=Math.Min(viewportHeight,qaHeight);
            scene.SetDetailsViewport(viewportHeight,Math.Min(360,controller.Monitor.WorkArea.Width/controller.Monitor.Scale));
            scene.Render(controller.Form,Media,DateTimeOffset.Now,EffectivePreferences,pageSaveNotice??notice,contentPolicy.Mode);
            scene.SubmitOwnedGlass();
            ReconcileWidgetBoard();
            qa?.RecordContent(contentPolicy.Mode,contentPolicy.ForForm(controller.Form),Media.State,controller.Form,controller.Visible);
            WakeMotion();
            graphicsRetries=0;
        }
        catch(Exception error)
        {
            RecoverGraphics(error,"graphics-render");
            return;
        }
        UpdateContentTimers();
    }
    bool TryScene(Action action)
    {
        if(disposed||CandidateGraphicsStopped)return false;
        try{action();return true;}
        catch(Exception error){RecoverGraphics(error,"graphics-update");return false;}
    }
    void RecoverGraphics(Exception error,string component)
    {
        if(host.IsExperimentalDesktopMaterialCandidate){StopExperimentalDesktopMaterial(error,component);return;}
        diagnostics.RecordError(component,error);notice="图形暂时不可用，已切换简化材质并尝试恢复。";solid=true;
        timelineTimer.Stop();clockTimer.Stop();cursorTimer.Stop();
        ApplyPresentation(controller.Motion.Sample(Now));
        if(graphicsRetries<3&&!graphicsRetryTimer.IsRunning){graphicsRetries++;graphicsRetryTimer.Start();}
        Changed?.Invoke();
    }
    void UpdateContentTimers()
    {
        if(disposed||quitting||CandidateGraphicsStopped)return;
        bool reconcileMedia=!disposed&&!quitting&&controller.Visible&&Media.Connected;
        if(reconcileMedia&&!mediaStateTimer.IsRunning){mediaStateTimer.Start();RefreshMediaPlaybackState();}
        else if(!reconcileMedia)mediaStateTimer.Stop();
        bool clock=controller.Visible&&(controller.Form==WidgetForm.Peek||controller.Form==WidgetForm.Capsule&&contentPolicy.ForForm(controller.Form)==CapsuleContentMode.Clock);
        clockTimer.Stop();
        if(clock){var current=DateTimeOffset.Now;clockTimer.Interval=TimeSpan.FromMilliseconds(Math.Max(10,60000-current.Second*1000-current.Millisecond));clockTimer.Start();}
        pauseTimer.Stop();
        if(controller.Visible&&contentPolicy.NeedsTick){pauseTimer.Interval=TimeSpan.FromMilliseconds(Math.Max(1,contentPolicy.RemainingDelay(Now)));pauseTimer.Start();}
        bool waveform=controller.Visible&&controller.Form!=WidgetForm.Details&&contentPolicy.ForForm(controller.Form)==CapsuleContentMode.Music&&Preferences.AudioVisualizationEnabled;
        audio.SetTarget(Media,waveform);
        bool progress=controller.Visible&&controller.Form==WidgetForm.Details&&pages.CurrentPageId==DetailsPageController.Music&&Media.Timeline?.Valid==true&&Media.State==PlaybackState.Playing;
        if(progress&&!timelineTimer.IsRunning)timelineTimer.Start();else if(!progress)timelineTimer.Stop();
        bool follow=controller.Visible&&controller.Form==WidgetForm.Capsule&&Preferences.CompanionEnabled&&Preferences.CompanionFollowMouse&&!EffectivePreferences.ReducedMotion;
        if(follow)
        {
            if(!cursorTimer.IsRunning){UpdateCursorGaze();cursorTimer.Start();}
        }
        else
        {
            cursorTimer.Stop();
            // Expanded forms receive gaze from native pointer events. A normal render
            // must not reset those eyes just because capsule polling is stopped.
            if(!controller.Visible || !Preferences.CompanionEnabled || !Preferences.CompanionFollowMouse || EffectivePreferences.ReducedMotion)
                TryScene(()=>scene.ClearCompanionPointerGaze());
        }
    }
    void UpdateCursorGaze()
    {
        if(disposed||quitting||CandidateGraphicsStopped)return;
        if(CursorPositionSource.TryGetRelativePosition(host.Context,out var x,out var y))
            TryScene(()=>scene.SetCompanionPointerGaze(x,y));
        else TryScene(()=>scene.ClearCompanionPointerGaze());
    }
    void OnPointer(HostPointerEvent e)
    {
        if(disposed||quitting)return;
        // Lifecycle requests belong to the host, before any page consumes pointer input.
        if(e.Kind==HostEventKind.Quit){Quit();return;}
        if(CandidateGraphicsStopped)return;
        if(e.Kind==HostEventKind.OpenSettings){ShowSettings();return;}
        bool positionedPointer=e.Kind is HostEventKind.Enter or HostEventKind.Move or HostEventKind.Leave
            or HostEventKind.Down or HostEventKind.Up or HostEventKind.Wheel;
        bool outsideSurface=positionedPointer&&e.ControlId==int.MinValue;
        int id=e.Kind is HostEventKind.Invoke or HostEventKind.RangeValue
            ?e.ControlId
            :outsideSurface?-1:scene.HitTest(e.X,e.Y);
        if(e.Kind is HostEventKind.Enter or HostEventKind.Move or HostEventKind.Leave or HostEventKind.CaptureLost)
            TryScene(()=>scene.UpdateCompanionGaze(e));
        if(e.Kind==HostEventKind.Enter)CompanionReact(CompanionInteraction.Attention);
        else if(e.Kind==HostEventKind.Down)CompanionReact(CompanionInteraction.Press);
        else if(e.Kind==HostEventKind.Up)CompanionReact(CompanionInteraction.Release);
        else if(e.Kind==HostEventKind.CaptureLost)CompanionReact(CompanionInteraction.None);
        qa?.RecordPointer(e,id,host.Context);
        // A wheel can be delivered while the HWND retains mouse capture. It
        // remains observable to QA, but must not scroll through a clipped edge.
        if(outsideSurface&&e.Kind==HostEventKind.Wheel)return;
        if(controller.Form==WidgetForm.Details&&(id==WidgetActions.HomeBack||homeBackPressed))
        {
            if(e.Kind is HostEventKind.Enter or HostEventKind.Move)
            {scene.SetPointerFeedback(id==WidgetActions.HomeBack?id:-1,homeBackPressed&&id==WidgetActions.HomeBack);return;}
            if(e.Kind==HostEventKind.Leave){scene.SetPointerFeedback(-1);return;}
            if(e.Kind==HostEventKind.Invoke){SelectDetailsPage(DetailsPageController.Home);return;}
            if(e.Kind==HostEventKind.Down){homeBackPressed=true;scene.SetPointerFeedback(id,true);return;}
            if(e.Kind is HostEventKind.Up or HostEventKind.CaptureLost)
            {
                bool activate=homeBackPressed&&id==WidgetActions.HomeBack&&e.Kind==HostEventKind.Up;
                homeBackPressed=false;scene.SetPointerFeedback(-1);
                if(activate)SelectDetailsPage(DetailsPageController.Home);
            }
            return;
        }
        bool homePageVisible=scene.HomePage&&controller.Form==WidgetForm.Details;
        bool homeHoverChanged=false;
        if(homePageVisible)
        {
            if(e.Kind==HostEventKind.Wheel&&!scene.HomePopoverOpen)
            {
                scene.ScrollHome(-(float)e.Value*40);pages.SetScrollOffset(scene.CurrentPageScroll);return;
            }
            if(e.Kind is HostEventKind.Enter or HostEventKind.Move)homeHoverChanged=scene.UpdateHomePointer(e.X,e.Y);
            else if(e.Kind is HostEventKind.Leave or HostEventKind.CaptureLost)homeHoverChanged=scene.ClearHomeHover();
        }
        if(scene.RouteSpecialPointer(e,id,Now)){UpdateOutsideGeometry();WakeMotion();return;}
        // The chrome handle is still the only way back to the capsule on Home; it is not a
        // dock action, so the controller must receive it before the home route consumes events.
        if(homePageVisible&&id==WidgetActions.Close)
        {
            controller.HandlePointer(e,id,Now);UpdateOutsideGeometry();WakeMotion();return;
        }
        if(RouteHomePointer(e,id))
        {
            if(homeHoverChanged)Render();
            UpdateOutsideGeometry();WakeMotion();return;
        }
        if(homePageVisible){if(homeHoverChanged)Render();UpdateOutsideGeometry();WakeMotion();return;}
        if(RouteBoardPointer(e,id))return;
        if(e.Kind==HostEventKind.Down)scene.SetCapture(id==WidgetActions.Seek?PointerCaptureOwner.Timeline:PointerCaptureOwner.WidgetDrag,Now);
        if(e.Kind is HostEventKind.Up or HostEventKind.CaptureLost)scene.SetCapture(PointerCaptureOwner.None,Now);
        UpdateOutsideGeometry();
        if(e.Kind==HostEventKind.Wheel)
        {
            if(scene.HomePage)return;
            if(scene.BoardHiddenPanelOpen){ScrollHiddenPanel(-(float)e.Value*40);return;}
            TryScene(()=>scene.ScrollDetails(-(float)e.Value*40));pages.SetScrollOffset(scene.CurrentPageScroll);return;
        }
        if(e.Kind==HostEventKind.RangeValue)
        {
            var control=scene.GetAccessibleControls().FirstOrDefault(c=>c.Id==WidgetActions.Seek);
            if(control is {Enabled:true}) SubmitSeek(scene.SeekPositionFor(control.X+control.Width*(float)Math.Clamp(e.Value/100,0,1)),Media.SessionToken);
            return;
        }
        if(e.Kind==HostEventKind.Down)
        {
            pointerDown=true;pressedAction=id;
            TryScene(()=>scene.SetPointerFeedback(id,true));
            if(id==WidgetActions.Seek && Media.Capabilities.Seek && Media.Timeline?.Valid==true)
            {seeking=true;seekToken=Media.SessionToken;TryScene(()=>scene.SetSeekPreview(scene.SeekPositionFor(e.X)));}
        }
        else if(e.Kind==HostEventKind.Move)
        {
            if(seeking)TryScene(()=>scene.SetSeekPreview(scene.SeekPositionFor(e.X)));else TryScene(()=>scene.SetPointerFeedback(id,pointerDown&&pressedAction==id));
        }
        else if(e.Kind==HostEventKind.Up)
        {
            pointerDown=false;pressedAction=-1;
            TryScene(()=>scene.SetPointerFeedback(id,false));
            if(seeking){seeking=false;var position=scene.SeekPositionFor(e.X);TryScene(()=>scene.SetSeekPreview(null));SubmitSeek(position,seekToken);}
        }
        else if(e.Kind is HostEventKind.Leave or HostEventKind.CaptureLost)
        {TryScene(()=>scene.SetPointerFeedback(-1,false));if(e.Kind==HostEventKind.CaptureLost){pointerDown=false;pressedAction=-1;seeking=false;TryScene(()=>scene.SetSeekPreview(null));}}
        controller.HandlePointer(e,id,Now);
        WakeMotion();
    }
    async void SubmitSeek(TimeSpan? position,string token)
    {
        if(position is null||disposed)return;
        try{var result=await media.ExecuteAsync(MediaCommand.Seek,token,position);if(!result.Accepted||!result.Verified){notice=result.Error??"播放进度未得到客户端确认。";Render();Changed?.Invoke();}}
        catch(Exception error){diagnostics.RecordError("seek",error);}
    }
    async void InvokeAction(int id)
    {
        if(disposed)return;
        if(scene.BoardRender?.Actions.TryGetValue(id,out var boardAction)==true)
        {
            // UIA Invoke follows the same enabled gate as pointer input: a disabled
            // control is announced but can never execute its action.
            bool enabled=scene.BoardRender.Controls.FirstOrDefault(c=>c.Id==id) is {Enabled:true};
            if(enabled)ExecuteBoardAction(boardAction);
            return;
        }
        if(scene.HomeActions.TryGetValue(id,out var homeAction))
        {
            if(controller.Form!=WidgetForm.Details)return;
            bool enabled=scene.GetAccessibleControls().FirstOrDefault(c=>c.Id==id) is not {Enabled:false};
            if(enabled)ExecuteHomeAction(homeAction);
            WakeMotion();
            return;
        }
        if(id==WidgetActions.OpenMusic){OpenMusic();return;}
        if(id==WidgetActions.Settings||id==WidgetActions.PluginTools){ShowSettings();if(id==WidgetActions.PluginTools)settings?.ShowPlugins();return;}
        MediaCommand? command=id switch {WidgetActions.Previous=>MediaCommand.Previous,WidgetActions.Toggle=>MediaCommand.TogglePlayPause,WidgetActions.Next=>MediaCommand.Next,_=>null};
        if(command is null)return;
        TryScene(()=>{scene.SetMonotonicTime(Now);scene.AnimateTransportActivation(id);});
        WakeMotion();
        if(id is WidgetActions.Previous or WidgetActions.Next)TryScene(()=>scene.PrepareTrackTransition(id));
        try
        {
            var result=await media.ExecuteAsync(command.Value,Media.SessionToken);
            notice=result.Accepted&&result.Verified?null:result.Error??"客户端未确认此次操作。";Render();Changed?.Invoke();
        }
        catch(Exception error){diagnostics.RecordError("command",error);notice="音乐操作暂时不可用。";Render();Changed?.Invoke();}
    }
    void OnWindowContext(HostContext context)
    {
        if(disposed||context.Dpi==previousDpi)return;
        previousDpi=context.Dpi;
        queue.TryEnqueue(()=>{if(!disposed)RefreshMonitors();});
    }
    void RefreshMonitors()
    {
        if(disposed||CandidateGraphicsStopped)return;
        monitors=monitorService.GetMonitors();
        controller.SetMonitor(monitors.First(m=>m.Primary),Now,true);
        Render();WakeMotion();foreground.Refresh();
    }
    void RefreshMediaPlaybackState(){if(media is NetEaseMediaAdapter actual)actual.RefreshPlaybackState();}
    void RefreshMedia(){if(media is NetEaseMediaAdapter actual)actual.Refresh();}
    void OnResume(){if(disposed||CandidateGraphicsStopped)return;widgetScheduler.Invalidate(DateTimeOffset.UtcNow);Render();RefreshMonitors();if(controller.Visible)host.EnsureTopmost();RefreshMedia();}
    bool TrySubscribe(Action subscribe)
    {
        try{subscribe();return true;}
        catch(System.Runtime.InteropServices.COMException error){diagnostics.RecordError("system-notification",error);return false;}
    }
    void OnAdvancedEffects(UISettings sender,object args)=>dispatcher.TryEnqueue(RefreshSystemSettings);
    void OnAnimations(UISettings sender,object args)=>dispatcher.TryEnqueue(RefreshSystemSettings);
    void OnPower(object? sender,object args)=>dispatcher.TryEnqueue(RefreshSystemSettings);
    void RefreshSystemSettings()
    {
        if(CandidateGraphicsStopped)return;
        if(disposed)return;
        var environment=SystemIntegration.ReadMaterialEnvironment();
        widgetScheduler.Invalidate(DateTimeOffset.UtcNow);
        solid=environment.UseSolid||!uiSettings.AdvancedEffectsEnabled;
        controller.UpdatePreferences(EffectivePreferences,Now);ApplyPresentation(controller.Motion.Sample(Now));Render();WakeMotion();
    }
    public async Task<SettingsUpdateResult> UpdatePreferencesAsync(WidgetPreferences value)
    {
        await preferenceGate.WaitAsync();
        try
        {
            if(disposed)return new(false,"程序正在退出。");
            var next=pages.ApplyTo(PreferenceStore.Normalize(value));var previous=Preferences;
            bool hotkeyChanged=!options.UiPreview&&(next.HotkeyKey!=previous.HotkeyKey||next.HotkeyModifiers!=previous.HotkeyModifiers);
            if(hotkeyChanged&&!system.TrySetHotkey(next.HotkeyModifiers,next.HotkeyKey,out var hotkeyError))return new(false,hotkeyError);
            if(!options.UiPreview&&next.RunAtStartup!=previous.RunAtStartup&&!system.SetStartupEnabled(next.RunAtStartup,out var startupError))
            {if(hotkeyChanged)system.TrySetHotkey(previous.HotkeyModifiers,previous.HotkeyKey,out _);return new(false,startupError);}
            try{await store.SaveAsync(next);}
            catch(Exception error)
            {
                diagnostics.RecordError("preferences-save",error);
                if(hotkeyChanged)system.TrySetHotkey(previous.HotkeyModifiers,previous.HotkeyKey,out _);
                if(!options.UiPreview&&next.RunAtStartup!=previous.RunAtStartup)system.SetStartupEnabled(previous.RunAtStartup,out _);
                return new(false,"设置未能保存，请检查数据目录是否可写。");
            }
            Preferences=pages.ApplyTo(next);
            if(appearancePreview?.CompanionColor==next.CompanionColor&&appearancePreview.CompanionAppearanceId==next.CompanionAppearanceId)appearancePreview=null;
            notice=null;controller.UpdatePreferences(EffectivePreferences,Now);await plugins.ApplyPreferencesAsync(Preferences);await plugins.UpdateSceneAsync(PluginScene());ApplyPresentation(controller.Motion.Sample(Now));Render();WakeMotion();Changed?.Invoke();return new(true);
        }
        finally{preferenceGate.Release();}
    }
    public void ShowSettings()
    {
        if(disposed)return;
        try
        {
            settings??=options.DataDirectory is null ? new(this) : new(this,false,true,
                message=>File.AppendAllText(Path.Combine(store.DirectoryPath,"settings-construction.log"),message+Environment.NewLine));
            settings.Show();
        }
        catch(Exception error)
        {
            diagnostics.RecordError("settings-open",error);
            string? failurePath=FailureReporter.Write(store.DirectoryPath,"settings-open-error.txt",error,options.UiPreview);
            notice="设置暂时无法打开，请稍后重试。";
            if(options.QaReport is null)FailureReporter.NotifyOnce(notice,failurePath);
            Render();Changed?.Invoke();
        }
    }
    public void ResetPosition(){controller.ResetPosition(monitors.First(m=>m.Primary),Now);WakeMotion();}
    public void ResetLiquidGlassCapture(){try{host.ResetLiquidGlassCapture();}catch{/* Fuse reset is best-effort; frosted fallback stays active. */}}
    public void ToggleVisibility(){controller.ToggleVisibility(Now);WakeMotion();}
    public void OpenMusic(){if(!system.OpenNetEase(out var error)){notice=error;Render();Changed?.Invoke();}}
    public async Task RetryMediaAsync()
    {
        if(disposed||quitting)return;
        notice=null;
        await media.StartAsync();
        if(disposed||quitting)return;
        RefreshMedia();Render();Changed?.Invoke();
    }
    public async Task<string> ExportDiagnosticsAsync()
    {
        Directory.CreateDirectory(store.DirectoryPath);string path=Path.Combine(store.DirectoryPath,$"diagnostics-{DateTimeOffset.Now:yyyyMMdd-HHmmss}.json");await diagnostics.ExportAsync(path);return path;
    }
    public async Task<SettingsUpdateResult> ImportPluginAsync(string packagePath)
    {try{await plugins.ImportAsync(packagePath);Changed?.Invoke();return new(true,"插件已安装，可独立开启。");}catch(Exception error){diagnostics.RecordError("plugin-import",error);return new(false,error.Message);}}
    public async Task<SettingsUpdateResult> RemovePluginAsync(string id)
    {try{await plugins.RemoveAsync(id);var enabled=new PluginEnableMap(Preferences.PluginEnabled);enabled.Remove(id);return await UpdatePreferencesAsync(Preferences with {PluginEnabled=enabled});}catch(Exception error){diagnostics.RecordError("plugin-remove",error);return new(false,error.Message);}}
    public Task<SettingsUpdateResult> SetPluginEnabledAsync(string id,bool enabled)
    {
        if(id=="glassdesk.companion")return UpdatePreferencesAsync(Preferences with {CompanionEnabled=enabled});
        if(id=="glassdesk.liquidglass")return UpdatePreferencesAsync(Preferences with {LiquidGlassEnabled=enabled});
        var values=new PluginEnableMap(Preferences.PluginEnabled){[id]=enabled};
        return UpdatePreferencesAsync(Preferences with {PluginEnabled=values});
    }
    public async Task<SettingsUpdateResult> ExecutePluginCommandAsync(string pluginId,string commandId)
    {try{await plugins.ExecuteAsync(pluginId,commandId);return new(true);}catch(Exception error){diagnostics.RecordError("plugin-command",error);return new(false,error.Message);}}
    public JsonElement? GetPluginConfiguration(string id)=>plugins.GetConfiguration(id);
    public IReadOnlyList<PluginSetting> GetPluginSettings(string id)=>plugins.Settings(id);
    public async Task<SettingsUpdateResult> SavePluginConfigurationAsync(string id,JsonElement value)
    {try{await plugins.SaveConfigurationAsync(id,value);UpdatePlugins();return new(true);}catch(Exception error){diagnostics.RecordError("plugin-config",error);return new(false,error.Message);}}
    public async void Quit()
    {
        if(quitting||disposed)return;quitting=true;
        try
        {
            CloseHomeNavigationWindow();CommitBoardOnLeave();widgetTimer.Stop();pageSaveTimer.Stop();StopRuntimeTimers();outside.Update(null);
            bool saved=true;
            if(settings is not null){settings.Hide();await settings.FlushPendingAsync();saved=!settings.LastSaveFailed;}
            await preferenceGate.WaitAsync();
            try{if(store.CanSave)await store.SaveAsync(pages.ApplyTo(Preferences));}
            catch(Exception error){diagnostics.RecordError("quit-save",error);saved=false;}
            finally{preferenceGate.Release();}
            await widgetSaveTail;
            await widgetLoadTask;
            if(boardReady&&widgetStore.CanSave)
                try{await widgetStore.SaveAsync(boardEditor.State);pageSaveNotice=null;}
                catch(Exception error){diagnostics.RecordError("widget-quit-save",error);saved=false;}
            if(!saved&&options.QaReport is null&&!FailureReporter.ConfirmExitWithoutSaving())
            {
                quitting=false;notice="配置尚未保存，请检查数据目录后重试。";
                ShowSettings();Render();UpdateOutsideGeometry();WakeMotion();
                if(pages.SavePending){pageSaveTimer.Stop();pageSaveTimer.Start();}
                UpdatePlugins();Changed?.Invoke();return;
            }
            var pluginShutdown=plugins.DisposeAsync().AsTask();
            _=pluginShutdown.ContinueWith(task=>{_=task.Exception;},CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted|TaskContinuationOptions.ExecuteSynchronously,TaskScheduler.Default);
            try{await pluginShutdown.WaitAsync(TimeSpan.FromSeconds(8));}
            catch(Exception error){diagnostics.RecordError("plugin-quit",error);}
        }
        catch(Exception error)
        {
            diagnostics.RecordError("quit",error);
            string? path=FailureReporter.Write(store.DirectoryPath,"quit-error.txt",error,options.UiPreview);
            if(options.QaReport is null)FailureReporter.NotifyOnce("退出时发生错误，部分更改可能尚未保存。",path);
        }
        finally
        {
            if(quitting)
            {
                try{Dispose();}
                finally{exit();}
            }
        }
    }
    void StopRuntimeTimers()
    {
        motionTimer.Stop();clockTimer.Stop();timelineTimer.Stop();graphicsRetryTimer.Stop();pauseTimer.Stop();
        materialSnapshotTimer.Stop();cursorTimer.Stop();companionIdleTimer.Stop();mediaStateTimer.Stop();
#if GLASSDESK_UI_PREVIEW && GLASSDESK_SYSTEM_GLASS_PREVIEW
        qaTransportFeedback?.Dispose();
#endif
    }
    public void Dispose()
    {
        if(disposed)return;disposed=true;StopRuntimeTimers();outside.Update(null);
        StopOwnedWallpaperRefresh();
        void Clean(Action cleanup){try{cleanup();}catch(Exception error){diagnostics.RecordError("dispose",error);materialSession.MarkFault($"dispose failed: 0x{error.HResult:X8}");}}
        Clean(DisposeHomeNavigationWindow);
        Clean(DisposeWidgetBoard);
        Clean(()=>qa?.Dispose());Clean(()=>snapshotRequests?.Dispose());
        if(effectsSubscribed)uiSettings.AdvancedEffectsEnabledChanged-=OnAdvancedEffects;
        if(animationsSubscribed)uiSettings.AnimationsEnabledChanged-=OnAnimations;
        if(powerSubscribed)PowerManager.EnergySaverStatusChanged-=OnPower;
        Clean(outside.Dispose);Clean(audio.Dispose);Clean(()=>settings?.Dispose());Clean(foreground.Dispose);
        Clean(media.Dispose);Clean(system.Dispose);Clean(scene.Dispose);Clean(host.Dispose);Clean(store.Dispose);Clean(preferenceGate.Dispose);
        if(!CandidateGraphicsStopped)materialSession.TryCompleteCleanExit();
    }
}

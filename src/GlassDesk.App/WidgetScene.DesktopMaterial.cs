using System.Numerics;
using GlassDesk.Core;
using GlassDesk.NativeBridge;
using Windows.UI.Composition;

namespace GlassDesk.App;

public sealed partial class WidgetScene
{
    readonly float[] _ownedControlActivity=new float[3];
    readonly double[] _ownedControlPulseUntil=new double[3];
    readonly bool[] _ownedControlPulseActive=new bool[3];
    readonly TransportButtonMotion[] _ownedControlMotion=[new(),new(),new()];
    readonly TransportLensHandoff _transportLiquidGlassHandoff=new();
    TransportLiquidGlassHandoffFrame? _submittedLiquidGlassTransportFrame;
    bool _liquidGlassTransportSubmissionPending;
    double _ownedControlTick;
    bool _ownedControlMoving;
    bool _solidMaterial;
    // Lens support follows the actual transport glyph, not the independent
    // metadata/clock ink policy. High contrast bypasses the optical path.
    float TransportLensLightInk => ((.2126f * (_text >> 16 & 255)
        + .7152f * (_text >> 8 & 255) + .0722f * (_text & 255)) > 127.5f) ? 1 : 0;
    SpriteVisual? _ownedClock;
    CompositionSurfaceBrush? _ownedClockBrush;
    CompositionDrawingSurface? _ownedClockSurface;
    SurfaceOp[]? _ownedClockOperations;
    LiquidGlassInkVisual? _ownedClockInk,_capsuleClockInk;
    (string,string,float,bool,bool,string)? _ownedClockKey;
    bool OwnedGlassEnabled=>(_host.IsOwnedImageCandidate&&_host.OwnedImageAvailable||_host.IsLiquidGlassCapture)
        &&_host.MaterialSession?.IsExperimentTripped==false&&_preferences.LiquidGlassEnabled
        &&!_solidMaterial&&!_accessibility.HighContrast&&(_host.IsLiquidGlassCapture||_contentWeights.Z<.001f);

    void BeginSystemTransportPresentation()
    {
        if(_host.IsLiquidGlassCapture)_liquidGlassTransportSubmissionPending=true;
    }

    public void SetSolidMaterial(bool solid)
    {
        if (_solidMaterial == solid) return;
        _solidMaterial = solid;
        // Invalidate requested foreground ink when a translucent role becomes
        // opaque. Do not keep hidden optical keycaps after effects are disabled.
        _drawingInvalidated = true;
        _ownedClockKey = null;
    }

#if GLASSDESK_UI_PREVIEW && GLASSDESK_SYSTEM_GLASS_PREVIEW
    public object GetTransportVisualDiagnostics()
    {
        var form=OwnedGlassEnabled?_transportLiquidGlassHandoff.Owner
            :_detailsButtons.Opacity>_peekButtons.Opacity?WidgetForm.Details:WidgetForm.Peek;
        return new {
            Form=form.ToString(),FeedbackId=_feedbackId,Pressed=_feedbackPressed,
            Moving=_ownedControlMoving,GeometryOnly=true,
            DisconnectedReading=_disconnectedLineBounds.Select((bounds,index)=>new {
                Bounds=bounds, InkGeometry=index<_disconnectedInkGeometry.Length&&_disconnectedInkGeometry[index],
                Role=_disconnectedMeasuredText is { } text&&index<text.Length?text[index].TextRole.ToString():"unknown"
            }).ToArray(),
            MetadataReading=_metadataOperations.Select(pair=>new {
                Form=pair.Key.ToString(),
                Transitioning=_metadata[pair.Key].IsTransitioning,
                TextLengths=pair.Value.Select(op=>op.Text?.Length??0).ToArray(),
                Bounds=_readingPlatforms.TryGetValue(pair.Key,out var rows)
                    ?new[]{rows.First,rows.Second}:Array.Empty<FloatRect>(),
                SeparateFieldSizeDip=_readingSupport.TryGetValue(pair.Key,out var field)
                    ?new[]{field.Visual.Size.X,field.Visual.Size.Y}:Array.Empty<float>(),
                ContentWeight=pair.Key==WidgetForm.Capsule?_contentWeights.X:pair.Key==WidgetForm.Peek?_contentWeights.Y:_contentWeights.Z
            }).ToArray(),
            CombinedPeekReading = new {
                Active = _combinedPeekReading?.Visual.IsVisible == true,
                LineCount = _combinedPeekReading?.Visual.IsVisible == true ? _combinedPeekReadingBounds.Length : 0,
                LineBounds = _combinedPeekReading?.Visual.IsVisible == true
                    ? _combinedPeekReadingBounds.ToArray() : Array.Empty<FloatRect>(),
                CanvasDip = _combinedPeekReading?.Visual.IsVisible == true
                    ? new[]{_combinedPeekReading.Visual.Size.X,_combinedPeekReading.Visual.Size.Y} : Array.Empty<float>()
            },
            Controls=Enumerable.Range(0,3).Select(i=>{
                float original=_buttons.TryGetValue((form,WidgetActions.Previous+i),out var button)?button.Visual.Size.X:0;
                float diameter=Math.Min(62,Math.Max(0,original-2))*_ownedControlMotion[i].Scale;
                return new {Action=WidgetActions.Previous+i,_ownedControlMotion[i].Scale,
                    Diameter=diameter,Radius=diameter/2,Activity=_ownedControlActivity[i]};
            }).ToArray()
        };
    }
#endif

    /// <summary>Settles owned transport scaling when a settings change disables animation.</summary>
    public void ResetOwnedTransportMotion()
    {
        if(!_host.IsExperimentalDesktopMaterialCandidate||_disposed
            ||!_preferences.ReducedMotion&&_uiSettings.AnimationsEnabled)return;
        SettleOwnedTransportMotion(_motionNow);
        SubmitOwnedGlass();
    }

    void SettleOwnedTransportMotion(double now)
    {
        bool resetScale=false;
        for(int i=0;i<3;i++){
            int action=i+WidgetActions.Previous;
            var motion=_ownedControlMotion[i];
            resetScale|=motion.Active||motion.Scale!=TransportButtonMotion.RestScale;
            bool hovered=_feedbackId==action;
            motion.Tick(now,hovered,hovered&&_feedbackPressed,reducedMotion:true);
            _ownedControlActivity[i]=hovered?(_feedbackPressed?1:.22f):0;
            _ownedControlPulseUntil[i]=0;
            _ownedControlPulseActive[i]=false;
        }
        _ownedControlTick=now;
        _ownedControlMoving=false;
        if(OwnedGlassEnabled&&resetScale)foreach(var button in _buttons.Values){
            button.Visual.StopAnimation("Scale");button.Visual.Scale=Vector3.One;
        }
    }

    public bool TickOwnedGlass(double now)
    {
        if(!_host.IsExperimentalDesktopMaterialCandidate||_disposed)return false;
        double dt=Math.Clamp((now-_ownedControlTick)/1000,0,.05);_ownedControlTick=now;
        bool reducedMotion=_preferences.ReducedMotion||!_uiSettings.AnimationsEnabled;
        bool changed=TickReadingSupportMotion();_ownedControlMoving=false;
        for(int i=0;i<3;i++){
            int action=i+WidgetActions.Previous;
            float target=_feedbackId==action?(_feedbackPressed?1:.22f):0;
            bool pulseActive=!reducedMotion&&now<_ownedControlPulseUntil[i];
            if(pulseActive)target=Math.Max(target,.8f);
            float before=_ownedControlActivity[i];
            float value=reducedMotion?target:float.Lerp(before,target,(float)(1-Math.Exp(-dt/.055)));
            if(Math.Abs(value-target)<.002f)value=target;
            _ownedControlActivity[i]=value;changed|=Math.Abs(value-before)>.0001f;
            bool hovered=_feedbackId==action;
            bool pressed=hovered&&_feedbackPressed;
            var motion=_ownedControlMotion[i];
            float beforeScale=motion.Scale;
            motion.Tick(now,hovered,pressed,reducedMotion);
            if(pulseActive&&!_ownedControlPulseActive[i])motion.TriggerClick(now,reducedMotion);
            _ownedControlPulseActive[i]=pulseActive;
            changed|=Math.Abs(motion.Scale-beforeScale)>.0001f||motion.Active;
            _ownedControlMoving|=value!=target||pulseActive||motion.Active;
        }
        if(_host.IsLiquidGlassCapture&&OwnedGlassEnabled){
            var handoff=AdvanceLiquidGlassTransportHandoff(now,reducedMotion);
            changed|=handoff.Changed;
            _ownedControlMoving|=handoff.Active;
        }
        return changed;
    }
    Vector2 OwnedHeaderParallax(bool companion)
    {
        if(!OwnedGlassEnabled||_preferences.ReducedMotion)return Vector2.Zero;
        float p=Math.Clamp((_presentedSize.Y-52)/124,0,1),arc=MathF.Sin(MathF.PI*p);
        return companion?new(-14*arc,3*arc):new(10*arc,7*arc);
    }
    public void SubmitOwnedGlass()
    {
        if(_disposed)return;
        if(!_host.IsExperimentalDesktopMaterialCandidate||_host.MaterialSession?.IsExperimentTripped!=false){
            if(_liquidGlassTransportSubmissionPending){
                _transportLiquidGlassHandoff.Reset();
                _submittedLiquidGlassTransportFrame=null;_liquidGlassTransportSubmissionPending=false;
                BuildControls();
            }
            return;
        }
        if(_preferences.ReducedMotion||!_uiSettings.AnimationsEnabled)SettleOwnedTransportMotion(_motionNow);
        PresentOwnedClock();
        UpdateReadingSupport();
        if(_host.IsLiquidGlassCapture){_host.StartLiquidGlassCapture();return;}
        bool eligible=OwnedGlassEnabled&&_presentedSize.Y>=170&&_presentedSize.Y<=190;
        Span<OwnedGlassLens> lenses=stackalloc OwnedGlassLens[3];
        float opacity=_media.Connected?_contentWeights.Y:0;
        for(int i=0;i<3;i++){
            float activity=_ownedControlActivity[i];float scale=_ownedControlMotion[i].Scale;
            lenses[i]=new((_presentedSize.X-320)/2+108+i*52,PeekTransportY+22,
                42*scale,42*scale,21*scale,opacity,activity,TransportLensLightInk);
            if(_buttons.TryGetValue((WidgetForm.Peek,WidgetActions.Previous+i),out var visual)){
                visual.SetOwnedGlass(eligible);
                if(eligible)visual.Visual.Scale=new(scale,scale,1);
            }
        }
        if(_host.IsLiquidGlassCapture)_host.UpdateLiquidGlassCapture();
        else _host.PresentOwnedMaterial(eligible,lenses);
        if(!_host.IsLiquidGlassCapture&&!_host.OwnedImagePresented)foreach(var item in _buttons)
            if(item.Key.Form==WidgetForm.Peek)item.Value.SetOwnedGlass(false);
    }
    void SubmitLiquidGlassInk()
    {
        // The native GPU metrics pass supplies a smoothed ink-color surface;
        // each retained text/icon surface uses its alpha as the mask.
        bool enabled=_host.IsLiquidGlassCapture&&_host.LiquidGlassInkAvailable&&OwnedGlassEnabled;
        foreach(var pair in _metadata){
            var form=pair.Key;
            Vector3 parent=form==WidgetForm.Capsule?_capsuleContent.Offset:form==WidgetForm.Peek?_peekContent.Offset:_detailsViewport.Offset+_detailsBody.Offset;
            float weight=form==WidgetForm.Capsule?_contentWeights.X:form==WidgetForm.Peek?_contentWeights.Y:_contentWeights.Z;
            pair.Value.ApplyLiquidGlassInk(_host,new(parent.X,parent.Y),enabled&&weight>.001f&&pair.Value.Visual.Opacity>.001f);
        }
        _capsuleClockInk??=new(_host,_capsuleContent,_capsuleBrush);
        _capsuleClockInk.Update(_capsuleSurface,new(_capsuleContent.Offset.X,_capsuleContent.Offset.Y),_capsuleContent.Size,
            enabled&&_contentWeights.X>.001f&&!MusicFor(WidgetForm.Capsule));
        if(_ownedClock is not null&&_ownedClockBrush is not null){
            _ownedClockInk??=new(_host,_ownedClock,_ownedClockBrush);
            _ownedClockInk.Update(_ownedClockSurface,new(_ownedClock.Offset.X,_ownedClock.Offset.Y),_ownedClock.Size,enabled&&_ownedClock.Opacity>.001f);
        }
        _chrome.ApplyLiquidGlassInk(enabled&&_contentWeights.Z>.001f);
    }
    void SubmitLiquidGlassTransportGlass()
    {
        float peekParentOpacity=LiquidGlassTransportParentOpacity(WidgetForm.Peek);
        float detailsParentOpacity=LiquidGlassTransportParentOpacity(WidgetForm.Details);
        if(HomePage && _form==WidgetForm.Details && OwnedGlassEnabled){
            var dock=HomeGeometry.DockPill;
            var circle=HomeGeometry.NavTrigger;
            bool fits=dock.Width>0&&dock.Width<=320&&dock.Height>0&&dock.Height<=64
                &&dock.X>=0&&dock.Y>=0&&dock.Right<=_presentedSize.X&&dock.Bottom<=_presentedSize.Y
                &&circle.X>=0&&circle.Y>=0&&circle.Right<=_presentedSize.X&&circle.Bottom<=_presentedSize.Y;
            float opacity=Math.Clamp(_contentWeights.Z,0,1);
            _homeVisual?.SetDockOpticalGlass(fits&&opacity>.001f);
            _transportLiquidGlassHandoff.Reset();
            _submittedLiquidGlassTransportFrame=null;_liquidGlassTransportSubmissionPending=false;
            if(!fits||opacity<=.001f){_host.UpdateLiquidGlassCapture();return;}
            Span<OwnedGlassLens> homeLenses=stackalloc OwnedGlassLens[3];
            homeLenses[0]=new(dock.X+dock.Width/2,dock.Y+dock.Height/2,
                dock.Width,dock.Height,dock.Height/2,opacity,0,TransportLensLightInk);
            homeLenses[1]=new(circle.X+circle.Width/2,circle.Y+circle.Height/2,
                circle.Width,circle.Height,circle.Width/2,opacity,0,TransportLensLightInk);
            int homeCount=2;
            var rail=HomeGeometry.NavRail;
            if(HomeNavExpanded&&rail.Width>0&&rail.Height>0&&rail.Height<=320
                &&rail.X>=0&&rail.Y>=0&&rail.Right<=_presentedSize.X&&rail.Bottom<=_presentedSize.Y)
                homeLenses[homeCount++]=new(rail.X+rail.Width/2,rail.Y+rail.Height/2,
                    rail.Width,rail.Height,Math.Min(rail.Width,rail.Height)/2,opacity,0,TransportLensLightInk);
            _host.UpdateLiquidGlassCapture();
            return;
        }
        _homeVisual?.SetDockOpticalGlass(false);
        if(!OwnedGlassEnabled){
            bool refreshControls=_liquidGlassTransportSubmissionPending||_submittedLiquidGlassTransportFrame is not null;
            _transportLiquidGlassHandoff.Reset();
            _peekButtons.Opacity=peekParentOpacity;_detailsButtons.Opacity=detailsParentOpacity;
            foreach(var item in _buttons){item.Value.SetOwnedGlass(false);item.Value.Visual.Opacity=1;}
            _host.UpdateLiquidGlassCapture();
            _submittedLiquidGlassTransportFrame=null;_liquidGlassTransportSubmissionPending=false;
            if(refreshControls)BuildControls();
            return;
        }

        foreach(var item in _buttons)item.Value.SetOwnedGlass(true);
        var handoff=AdvanceLiquidGlassTransportHandoff(_motionNow,_preferences.ReducedMotion||!_uiSettings.AnimationsEnabled);
        _peekButtons.Opacity=handoff.PeekOpacity;
        _detailsButtons.Opacity=handoff.DetailsOpacity;
        _ownedControlMoving|=handoff.Active;
        Span<OwnedGlassLens> regions=stackalloc OwnedGlassLens[3];int count=0;
        var container=handoff.Owner==WidgetForm.Details?_detailsButtons:_peekButtons;
        float lensOpacity=handoff.Owner==WidgetForm.Details?handoff.DetailsOpacity:handoff.PeekOpacity;
        for(int i=0;i<3;i++){
            if(!_buttons.TryGetValue((handoff.Owner,WidgetActions.Previous+i),out var button))continue;
            float original=button.Visual.Size.X,size=Math.Min(62,Math.Max(0,original-2));
            float activity=_ownedControlActivity[i],scale=_ownedControlMotion[i].Scale;
            float x=container.Offset.X+button.Visual.Offset.X+original/2;
            float y=container.Offset.Y+button.Visual.Offset.Y+original/2;
            if(lensOpacity<=.001f||!TransportLensGeometry.IsFullyContained(
                x,y,size,scale,_presentedSize.X,_presentedSize.Y))continue;
            float emphasis=i==1?.18f+.82f*activity:activity;
            float radius=size*scale/2;
            regions[count++]=new(x,y,size*scale,size*scale,radius,lensOpacity,emphasis,TransportLensLightInk);
        }
        foreach(var pair in _buttons){
            pair.Value.Visual.Scale=new(_ownedControlMotion[pair.Key.Action-WidgetActions.Previous].Scale,
                _ownedControlMotion[pair.Key.Action-WidgetActions.Previous].Scale,1);
            pair.Value.Visual.Opacity=pair.Key.Form==handoff.Owner&&count==3&&handoff.HasVisibleRow(handoff.Owner)?1:0;
        }
        _host.UpdateLiquidGlassCapture();
        var previousFrame=_submittedLiquidGlassTransportFrame;
        var submittedFrame=count==3&&handoff.HasVisibleRow(handoff.Owner)?handoff:(TransportLiquidGlassHandoffFrame?)null;
        bool controlsChanged=_liquidGlassTransportSubmissionPending
            ||VisibleSystemTransportForm(previousFrame)!=VisibleSystemTransportForm(submittedFrame);
        _submittedLiquidGlassTransportFrame=submittedFrame;_liquidGlassTransportSubmissionPending=false;
        if(controlsChanged)BuildControls();
    }

    static WidgetForm? VisibleSystemTransportForm(TransportLiquidGlassHandoffFrame? frame)
    {
        if(frame is not { } submitted)return null;
        if(submitted.HasVisibleRow(WidgetForm.Peek))return WidgetForm.Peek;
        if(submitted.HasVisibleRow(WidgetForm.Details))return WidgetForm.Details;
        return null;
    }

    TransportLiquidGlassHandoffFrame AdvanceLiquidGlassTransportHandoff(double now,bool reducedMotion)
    {
        float peekOpacity=LiquidGlassTransportParentOpacity(WidgetForm.Peek);
        float detailsOpacity=LiquidGlassTransportParentOpacity(WidgetForm.Details);
        bool peekFits=SystemTransportRowFits(WidgetForm.Peek);
        bool detailsFits=SystemTransportRowFits(WidgetForm.Details);
        return _transportLiquidGlassHandoff.Tick(now,peekOpacity,detailsOpacity,peekFits,detailsFits,reducedMotion);
    }

    float LiquidGlassTransportParentOpacity(WidgetForm form)
        =>form==WidgetForm.Details
            ?_media.Connected&&!HomePage&&!WidgetsPage?Math.Clamp(_contentWeights.Z,0,1):0
            :_media.Connected?Math.Clamp(_contentWeights.Y,0,1):0;

    bool SystemTransportRowFits(WidgetForm form)
    {
        var container=form==WidgetForm.Details?_detailsButtons:_peekButtons;
        for(int i=0;i<3;i++){
            if(!_buttons.TryGetValue((form,WidgetActions.Previous+i),out var button))return false;
            float original=button.Visual.Size.X;
            float size=Math.Min(62,Math.Max(0,original-2));
            float scale=_ownedControlMotion[i].Scale;
            float x=container.Offset.X+button.Visual.Offset.X+original/2;
            float y=container.Offset.Y+button.Visual.Offset.Y+original/2;
            if(!TransportLensGeometry.IsFullyContained(x,y,size,scale,_presentedSize.X,_presentedSize.Y))return false;
        }
        return true;
    }
    void PresentOwnedClock()
    {
        if(_ownedClock is null){
            _ownedClock=_host.Compositor.CreateSpriteVisual();_ownedClockBrush=_host.Compositor.CreateSurfaceBrush();
            _ownedClockBrush.Stretch=CompositionStretch.Fill;_ownedClock.Brush=_ownedClockBrush;
            _bodyRoot.Children.InsertAtTop(_ownedClock);
        }
        _ownedClock.Opacity=_media.Connected?_contentWeights.Y:0;
        if(!_media.Connected||_contentWeights.Y<=.001f)return;
        float width=PeekClockWidth;
        var key=(ClockPrimary,ClockSecondary,width,_clockPlateLightText,_clockPlatePromoteSecondary,_preferences.ClockFontId);
        if(_ownedClockKey!=key){
            var operations=new List<SurfaceOp>();bool secondary=ClockSecondary.Length>0;
            Text(operations,ClockPrimary,0,secondary?10:15,width,26,TextRole.ClockWhiteplate,InkRole.Primary,center:true,
                flags:_preferences.ClockShowTime?TextFlags.TabularNumbers:TextFlags.TabularDigitsOnly,clockPlate:true);
            if(secondary)Text(operations,ClockSecondary,0,36,width,12,TextRole.ClockLegacyAux,InkRole.Secondary,center:true,clockPlate:true);
            _ownedClockInk?.Dispose();
            ReplaceSurface(ref _ownedClockSurface,_ownedClockBrush!,_host.RenderSurface(width,50,operations));
            // Keep geometry tied to the exact operations now bound to the visible clock surface.
            _ownedClockOperations=operations.ToArray();
            _ownedClock.Size=new(width,50);_ownedClockKey=key;
        }
        var parallax=OwnedHeaderParallax(false);
        _ownedClock.Offset=new((_presentedSize.X-320)/2+PeekClockX+parallax.X,parallax.Y,0);
    }
    void DisposeDesktopMaterialVisuals()
    {
        DisposeReadingSupport();
        _ownedClockInk?.Dispose();_capsuleClockInk?.Dispose();_ownedClockInk=null;_capsuleClockInk=null;
        if(_ownedClockBrush is not null)_ownedClockBrush.Surface=null;
        _ownedClockSurface?.Dispose();_ownedClock?.Dispose();_ownedClockBrush?.Dispose();
        _ownedClockSurface=null;_ownedClock=null;_ownedClockBrush=null;_ownedClockOperations=null;
    }
}

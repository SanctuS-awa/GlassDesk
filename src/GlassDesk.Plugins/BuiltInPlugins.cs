using System.Text.Json;
using GlassDesk.Core;

namespace GlassDesk.Plugins;

public sealed class OrbAppearance : ICompanionAppearance
{
    public string Id=>"glassdesk.orb.original";
    public CompanionVisualState Present(CompanionExpression expression,uint color)=>expression switch
    {
        // Eye offsets use the same 24-DIP logical coordinates as the artwork;
        // the host scales the entire appearance down for its smaller slots.
        CompanionExpression.Curious=>new(expression,color,.55f,-.35f,1.015f,.99f,Tilt:-1.8f),
        CompanionExpression.Happy=>new(expression,color,0,-.45f,1.025f,.975f,Tilt:.8f),
        CompanionExpression.Sleepy=>new(expression,color,0,.45f,1.015f,.985f),
        CompanionExpression.Surprised=>new(expression,color,0,-.3f,.965f,1.035f),
        _=>new(expression,color)
    };
}

internal sealed class CompanionPlugin : IGlassDeskPlugin
{
    readonly OrbAppearance appearance=new();
    readonly object stateGate=new();
    readonly SemaphoreSlim wake=new(0,1);
    IPluginContext? context;
    CancellationTokenSource? timerCancellation;
    Task? timer;
    PluginSceneState scene=new(WidgetForm.Capsule,true,MediaSnapshot.Disconnected,false,0);
    CompanionVisualState? lastPublished;
    CompanionExpression reaction=CompanionExpression.Calm;
    uint color=0xff202329;
    bool running,subscribed,startedOnce,pressed,dragging,blinking;
    double reactionUntil,nextBlink,blinkUntil,secondBlinkAt=double.PositiveInfinity;
    double nextGaze,gazeUntil,nextRest,lastMeaningful;
    float gazeX,gazeY,reactionX,reactionY,reactionTilt;
    long lastInteractionSerial;
    int transitionMs=220;
    static double Now=>Environment.TickCount64;
    static double Between(double minimum,double maximum)=>minimum+Random.Shared.NextDouble()*(maximum-minimum);

    public void Initialize(IPluginContext value)=>context=value;
    public IEnumerable<IPluginContribution> RegisterContributions()=>[new PluginCompanion("orb","陪伴球",appearance)];
    public ValueTask StartAsync(CancellationToken cancellationToken)
    {
        var owner=context??throw new InvalidOperationException("陪伴插件尚未初始化。");
        CompanionVisualState? initial;
        CancellationToken runToken;
        lock(stateGate)
        {
            if(running)return ValueTask.CompletedTask;
            scene=owner.Scene;color=ReadColor(owner);
            lastInteractionSerial=scene.InteractionSerial;
            running=true;pressed=dragging=blinking=false;
            reactionX=reactionY=reactionTilt=gazeX=gazeY=0;gazeUntil=0;
            double now=Now;lastMeaningful=now;transitionMs=220;
            reaction=startedOnce?CompanionExpression.Calm:
                scene.Media.State==PlaybackState.Playing?CompanionExpression.Happy:
                scene.Form!=WidgetForm.Capsule?CompanionExpression.Curious:CompanionExpression.Calm;
            reactionUntil=reaction==CompanionExpression.Calm?0:now+900;
            // Resuming starts a fresh idle schedule; hidden time never produces
            // accumulated blinks or a replay of songs/gestures missed while hidden.
            ResetIdleSchedule(now);startedOnce=true;lastPublished=null;
            timerCancellation=CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            runToken=timerCancellation.Token;
            initial=TakeVisual(now);
        }
        if(!subscribed){owner.SceneChanged+=OnScene;subscribed=true;}
        if(initial is not null)owner.SubmitCompanion(initial);
        owner.ReportResourceUsage("对歌曲、展开和触碰作出回应；不规则短眨眼与轻微注视，无固定表情轮播；隐藏或减少动画时停止闲置动作。");
        timer=AnimateAsync(runToken);
        return ValueTask.CompletedTask;
    }

    static uint ReadColor(IPluginContext owner)=>owner.ReadConfiguration() is JsonElement configuration&&
        configuration.ValueKind==JsonValueKind.Object&&configuration.TryGetProperty("color",out var value)&&value.TryGetUInt32(out uint result)
            ?result:0xff202329;

    void ResetIdleSchedule(double now)
    {
        nextBlink=now+Between(2800,7500);nextGaze=now+Between(4000,12000);
        nextRest=now+Between(50000,120000);secondBlinkAt=double.PositiveInfinity;
    }

    void Respond(CompanionExpression expression,double duration,double now,float x=0,float y=0,float tilt=0,int transition=220)
    {
        reaction=expression;reactionUntil=now+duration;
        reactionX=x;reactionY=y;reactionTilt=tilt;transitionMs=transition;
        lastMeaningful=now;blinking=false;gazeX=gazeY=0;gazeUntil=0;
        // Only meaningful changes call Respond. Metadata refreshes and scene
        // timestamps do not push any action deadline forward.
        ResetIdleSchedule(now);
    }

    void OnScene(PluginSceneState value)
    {
        var owner=context;if(owner is null)return;
        uint configuredColor=ReadColor(owner);
        CompanionVisualState? visual;
        bool changed=false;
        lock(stateGate)
        {
            if(!running)return;
            var previous=scene;scene=value;double now=Now;
            if(color!=configuredColor){color=configuredColor;changed=true;}
            if(previous.ReducedMotion!=value.ReducedMotion)
            {
                blinking=false;gazeX=gazeY=0;gazeUntil=0;
                ResetIdleSchedule(now);
                if(!value.ReducedMotion&&!pressed&&!dragging)reactionUntil=now+400;
                transitionMs=value.ReducedMotion?0:220;changed=true;
            }
            bool paused=previous.Media.State==PlaybackState.Playing&&value.Media.State is PlaybackState.Paused or PlaybackState.Stopped or PlaybackState.Closed;
            bool playing=previous.Media.State!=PlaybackState.Playing&&value.Media.State==PlaybackState.Playing;
            bool songChanged=value.Media.Connected&&!string.IsNullOrEmpty(value.Media.Title)&&
                (previous.Media.SessionToken!=value.Media.SessionToken||previous.Media.Title!=value.Media.Title||previous.Media.Artist!=value.Media.Artist);
            if(paused||previous.Media.Connected&&!value.Media.Connected)
            {Respond(CompanionExpression.Calm,500,now);changed=true;}
            else if(playing||songChanged)
            {Respond(CompanionExpression.Happy,Between(1100,1700),now,0,-.15f);changed=true;}
            if(previous.Form!=value.Form)
            {
                var expression=value.Form switch
                {WidgetForm.Details=>CompanionExpression.Happy,WidgetForm.Peek=>CompanionExpression.Curious,_=>CompanionExpression.Calm};
                Respond(expression,Between(750,1100),now,value.Form==WidgetForm.Peek ? .3f : 0,0,0,190);changed=true;
            }
            if(value.InteractionSerial>lastInteractionSerial)
            {
                lastInteractionSerial=value.InteractionSerial;
                switch(value.Interaction)
                {
                    case CompanionInteraction.Attention:
                        Respond(CompanionExpression.Curious,1100,now,.65f,-.1f,-.7f,180);changed=true;break;
                    case CompanionInteraction.Press:
                        pressed=true;dragging=false;
                        Respond(CompanionExpression.Surprised,double.PositiveInfinity,now,0,-.1f,0,90);changed=true;break;
                    case CompanionInteraction.Drag:
                        if(!dragging)reactionTilt=Random.Shared.Next(2)==0?-3.5f:3.5f;
                        float lean=reactionTilt;pressed=false;dragging=true;
                        Respond(CompanionExpression.Curious,double.PositiveInfinity,now,lean<0?-.8f:.8f,-.15f,lean,150);changed=true;break;
                    case CompanionInteraction.Release:
                        pressed=dragging=false;
                        Respond(CompanionExpression.Happy,750,now,0,-.1f,0,200);changed=true;break;
                    case CompanionInteraction.None:
                        pressed=dragging=false;
                        Respond(CompanionExpression.Calm,350,now,0,0,0,220);changed=true;break;
                }
            }
            if(!changed)return;
            visual=TakeVisual(now);
        }
        if(visual is not null)owner.SubmitCompanion(visual);
        Wake();
    }

    bool IdleAllowed=>scene.Visible&&!scene.ReducedMotion&&!pressed&&!dragging;

    void Advance(double now)
    {
        if(scene.ReducedMotion||!scene.Visible)return;
        if(reactionUntil>0&&now>=reactionUntil&&!pressed&&!dragging)
        {reaction=CompanionExpression.Calm;reactionUntil=0;reactionX=reactionY=reactionTilt=0;transitionMs=240;}
        if(gazeUntil>0&&now>=gazeUntil)
        {gazeX=gazeY=0;gazeUntil=0;transitionMs=300;}
        if(blinking&&now>=blinkUntil){blinking=false;transitionMs=90;}
        if(!IdleAllowed)return;
        if(!blinking&&now>=secondBlinkAt)
        {blinking=true;blinkUntil=now+110;secondBlinkAt=double.PositiveInfinity;transitionMs=45;}
        if(!blinking&&now>=nextBlink)
        {
            blinking=true;blinkUntil=now+110;transitionMs=45;
            if(Random.Shared.NextDouble()<.14)secondBlinkAt=blinkUntil+Between(90,180);
            nextBlink=now+Between(2800,7500);
        }
        if(now>=nextGaze)
        {
            gazeX=(float)Between(-1.2,1.2);gazeY=(float)Between(-.6,.3);
            gazeUntil=now+Between(650,1900);nextGaze=now+Between(4000,12000);
            if(!blinking)transitionMs=280;
        }
        if(now>=nextRest)
        {
            nextRest=now+Between(50000,120000);
            // Rare quiet moments reflect the current music/quiet context; there
            // is no arbitrary rotation through the six expression enums.
            if(now-lastMeaningful>=18000&&reactionUntil==0&&Random.Shared.NextDouble()<.28)
            {
                reaction=scene.Media.State==PlaybackState.Playing?CompanionExpression.Happy:CompanionExpression.Sleepy;
                reactionUntil=now+Between(900,1500);reactionX=reactionY=reactionTilt=0;
                if(!blinking)transitionMs=260;
            }
        }
    }

    CompanionVisualState? TakeVisual(double now)
    {
        var expression=reactionUntil>now||scene.ReducedMotion?reaction:CompanionExpression.Calm;
        var visual=appearance.Present(expression,color);
        visual=visual with
        {
            Expression=blinking&&!scene.ReducedMotion?CompanionExpression.Blink:expression,
            EyeOffsetX=Math.Clamp(visual.EyeOffsetX+reactionX+gazeX,-2,2),
            EyeOffsetY=Math.Clamp(visual.EyeOffsetY+reactionY+gazeY,-2,2),
            Tilt=Math.Clamp(visual.Tilt+reactionTilt,-4,4),TransitionMs=transitionMs
        };
        if(pressed)visual=visual with{ScaleX=1.04f,ScaleY=.94f};
        if(dragging)visual=visual with{ScaleX=1.035f,ScaleY=.96f};
        if(scene.ReducedMotion)visual=visual with{EyeOffsetX=0,EyeOffsetY=0,ScaleX=1,ScaleY=1,Tilt=0,TransitionMs=0};
        if(lastPublished==visual)return null;
        lastPublished=visual;return visual;
    }

    int WaitMilliseconds(double now)
    {
        if(scene.ReducedMotion||!scene.Visible)return Timeout.Infinite;
        double due=double.PositiveInfinity;
        if(!pressed&&!dragging&&reactionUntil>0)due=Math.Min(due,reactionUntil);
        if(gazeUntil>0)due=Math.Min(due,gazeUntil);
        if(blinking)due=Math.Min(due,blinkUntil);
        if(IdleAllowed)due=Math.Min(due,Math.Min(secondBlinkAt,Math.Min(nextBlink,Math.Min(nextGaze,nextRest))));
        return double.IsPositiveInfinity(due)?Timeout.Infinite:(int)Math.Clamp(Math.Ceiling(due-now),1,int.MaxValue);
    }

    void Wake()
    {try{wake.Release();}catch(SemaphoreFullException){ }catch(ObjectDisposedException){ }}

    async Task AnimateAsync(CancellationToken cancellationToken)
    {
        try
        {
            while(!cancellationToken.IsCancellationRequested)
            {
                CompanionVisualState? visual;IPluginContext? owner;int delay;
                lock(stateGate)
                {
                    if(!running)return;
                    double now=Now;Advance(now);visual=TakeVisual(now);owner=context;delay=WaitMilliseconds(now);
                }
                if(visual is not null)owner?.SubmitCompanion(visual);
                await wake.WaitAsync(delay,cancellationToken).ConfigureAwait(false);
            }
        }
        catch(OperationCanceledException) when(cancellationToken.IsCancellationRequested) { }
    }
    public async ValueTask PauseAsync(CancellationToken cancellationToken)
    {
        if(subscribed&&context is not null){context.SceneChanged-=OnScene;subscribed=false;}
        lock(stateGate){running=false;pressed=dragging=blinking=false;gazeX=gazeY=0;lastPublished=null;}
        timerCancellation?.Cancel();
        if(timer is not null)await timer.ConfigureAwait(false);
        timer=null;timerCancellation?.Dispose();timerCancellation=null;
    }
    public async ValueTask StopAsync(CancellationToken cancellationToken)
    {await PauseAsync(cancellationToken);context?.SubmitCompanion(null);}
    public async ValueTask DisposeAsync(){await StopAsync(CancellationToken.None);context=null;wake.Dispose();}
}

internal sealed class LiquidGlassPlugin : IGlassDeskPlugin
{
    IPluginContext? context;
    public void Initialize(IPluginContext value)=>context=value;
    public IEnumerable<IPluginContribution> RegisterContributions()=>[new PluginMaterial("liquidglass","液态玻璃","启用时通过 GPU 桌面捕获渲染折射与色散；关闭时释放捕获资源并恢复系统磨砂。")];
    public ValueTask StartAsync(CancellationToken cancellationToken){context?.SetMaterial("liquidglass",true);return ValueTask.CompletedTask;}
    public ValueTask PauseAsync(CancellationToken cancellationToken){context?.SetMaterial("liquidglass",false);return ValueTask.CompletedTask;}
    public ValueTask StopAsync(CancellationToken cancellationToken)=>PauseAsync(cancellationToken);
    public async ValueTask DisposeAsync(){await StopAsync(CancellationToken.None);context=null;}
}

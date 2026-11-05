using GlassDesk.Core;
using System.Diagnostics;

int checks=0;
void Check(bool result,string name) { if(!result){Console.Error.WriteLine("FAIL "+name);Environment.Exit(1);}checks++;Console.WriteLine("PASS "+name); }
MonitorDescriptor Monitor(float scale=1)=>new("test",new(0,0,1920,1080),new(0,0,1920,1040),scale,true);
WidgetController Controller()=>new(new(),Monitor(),new(960,24));
HostPointerEvent Point(HostEventKind kind,int x=960,int y=24)=>new(kind,0,0,x,y);

if(args.Contains("--fixed-position-only"))
{
    var primary=new MonitorDescriptor("primary",new(-1920,0,1920,1080),new(-1900,40,1880,1000),1.5f,true);
    var normalized=new WidgetPreferences {MonitorId="old-secondary",AnchorX=.93,AnchorY=720}.Validated();
    Check(normalized.MonitorId is null&&normalized.AnchorX==.5&&normalized.AnchorY==0,
        "legacy monitor and saved anchors normalize to fixed placement");
    var cFixed=new WidgetController(new(){MonitorId="old-secondary",AnchorX=.93,AnchorY=720},primary,new(-1800,900));
    var anchor=new WidgetAnchor(-960,40);
    Check(cFixed.Anchor==anchor,"initial placement uses primary work-area center and top edge");
    foreach(float scale in new[]{1f,1.5f,2f})
    {
        var monitor=new MonitorDescriptor("dpi",new(0,0,2560,1600),new(0,36,2560,1500),scale,true);
        var placement=new WidgetController(new(),monitor,new(0,0));
        Check(placement.Motion.Target.Y==36&&placement.Anchor.CenterX==1280,
            $"fixed placement touches the primary work-area top at {scale:P0} DPI");
    }
    cFixed.ChangeForm(WidgetForm.Peek,0);var peek=cFixed.Motion.Target;
    cFixed.ChangeForm(WidgetForm.Details,1);var details=cFixed.Motion.Target;
    Check(Math.Abs(peek.X+peek.Width*primary.Scale/2-anchor.CenterX)<.001&&Math.Abs(peek.Y-anchor.TopY)<.001&&
          Math.Abs(details.X+details.Width*primary.Scale/2-anchor.CenterX)<.001&&Math.Abs(details.Y-anchor.TopY)<.001,
        "Peek and Details keep the same top-center anchor");
    cFixed.SetMonitor(new("secondary",new(-1280,0,1280,720),new(-1280,0,1280,680),1.5f,false),2,true);
    Check(cFixed.Monitor.Id=="primary"&&cFixed.Anchor==anchor,"secondary monitor and saved monitor choice are ignored");
    cFixed.SetMonitor(new("new-primary",new(0,0,1800,1050),new(100,50,1600,900),1.25f,true),3,true);
    Check(cFixed.Monitor.Id=="new-primary"&&cFixed.Anchor==new WidgetAnchor(900,50),
        "primary work-area changes recenter at its top without preserving old offsets");

    var pointer=new WidgetController(new(),primary,new(0,0));var fixedPrimaryAnchor=pointer.Anchor;int action=0,placementCommits=0;
    pointer.ActionRequested+=id=>action=id;
    pointer.PlacementCommitted+=(_,_)=>placementCommits++;
    pointer.HandlePointer(Point(HostEventKind.Down),WidgetActions.Body,0);
    pointer.HandlePointer(Point(HostEventKind.Move,1010,74),WidgetActions.Body,10);
    pointer.HandlePointer(Point(HostEventKind.Up,1010,74),WidgetActions.Body,20);
    Check(pointer.Anchor==fixedPrimaryAnchor&&pointer.Form==WidgetForm.Capsule&&placementCommits==0,
        "body motion beyond 6 DIP cancels click without moving or expanding the widget");
    pointer.HandlePointer(Point(HostEventKind.Down),WidgetActions.Body,30);
    pointer.HandlePointer(Point(HostEventKind.Move,963,26),WidgetActions.Body,35);
    pointer.HandlePointer(Point(HostEventKind.Up,963,26),WidgetActions.Body,40);
    Check(pointer.Anchor==fixedPrimaryAnchor&&pointer.Form==WidgetForm.Details,
        "small body motion still activates the body click without moving the widget");
    pointer.HandlePointer(Point(HostEventKind.Down),WidgetActions.Toggle,50);
    pointer.HandlePointer(Point(HostEventKind.Move,1110,170),WidgetActions.Toggle,60);
    pointer.HandlePointer(Point(HostEventKind.Up,1110,170),WidgetActions.Toggle,70);
    Check(action==WidgetActions.Toggle&&pointer.Anchor==fixedPrimaryAnchor,
        "playback-button movement does not become a body drag or lose its action");
    pointer.HandlePointer(Point(HostEventKind.Down),WidgetActions.Seek,80);
    pointer.HandlePointer(Point(HostEventKind.Move,1300,700),WidgetActions.Seek,90);
    pointer.HandlePointer(Point(HostEventKind.Up,1300,700),WidgetActions.Seek,100);
    Check(pointer.Anchor==fixedPrimaryAnchor,"seek dragging leaves fixed placement unchanged");
    Console.WriteLine($"{checks} fixed-position checks passed.");
    return;
}

var c=Controller();
c.HandlePointer(Point(HostEventKind.Enter),0,0);c.Tick(349);
Check(c.Form==WidgetForm.Capsule,"hover stays capsule before 350 ms");
c.HandlePointer(Point(HostEventKind.Leave),0,349);c.Tick(1000);
Check(c.Form==WidgetForm.Capsule,"quick crossing cancels delayed expansion");
c.HandlePointer(Point(HostEventKind.Enter),0,1100);c.Tick(1450);
Check(c.Form==WidgetForm.Peek,"sustained hover expands once");
c.HandlePointer(Point(HostEventKind.Leave),0,1500);c.Tick(1949);
Check(c.Form==WidgetForm.Peek,"leave delay keeps peek until threshold");
c.HandlePointer(Point(HostEventKind.Enter),0,1949);c.Tick(2200);
Check(c.Form==WidgetForm.Peek,"return cancels pending collapse");
int requested=0;c.ActionRequested+=id=>requested=id;
c.HandlePointer(Point(HostEventKind.Down),WidgetActions.Toggle,2210);c.HandlePointer(Point(HostEventKind.Up),WidgetActions.Toggle,2220);
Check(requested==WidgetActions.Toggle && c.Form==WidgetForm.Peek,"play action does not bubble to details");
c.HandlePointer(Point(HostEventKind.Down),0,2230);c.HandlePointer(Point(HostEventKind.Up),0,2240);
c.HandlePointer(Point(HostEventKind.Leave),0,2241);c.Tick(4000);
Check(c.Form==WidgetForm.Details,"details persists after pointer leaves");
c.HandlePointer(Point(HostEventKind.Invoke),WidgetActions.Close,4001);
Check(c.Form==WidgetForm.Capsule,"explicit close returns to capsule");

c=Controller();var fixedAnchor=c.Anchor;c.HandlePointer(Point(HostEventKind.Down,960,24),0,0);
c.HandlePointer(Point(HostEventKind.Move,1000,55),0,10);c.HandlePointer(Point(HostEventKind.Up,1000,55),0,20);
Check(c.Anchor==fixedAnchor&&!c.IsDragging&&c.Form==WidgetForm.Capsule,
    "body movement beyond the threshold cancels the click without moving the widget");
c.HandlePointer(Point(HostEventKind.Down,960,24),0,30);c.HandlePointer(Point(HostEventKind.Move,963,26),0,35);
c.HandlePointer(Point(HostEventKind.Up,963,26),0,40);
Check(c.Anchor==fixedAnchor&&c.Form==WidgetForm.Details,
    "small body movement keeps click-to-expand while placement remains fixed");
c=Controller();c.ChangeForm(WidgetForm.Details,0);c.Tick(300);
c.HandlePointer(Point(HostEventKind.Down,950,600),WidgetActions.Seek,310);c.HandlePointer(Point(HostEventKind.Move,1400,700),WidgetActions.Seek,320);
Check(!c.IsDragging && c.Anchor==new WidgetAnchor(960,0),"seek drag does not move the fixed component");

c=Controller();c.ChangeForm(WidgetForm.Details,0);c.ApplySystemScene(new(100,true,false),100);
Check(!c.Visible,"fullscreen hides the component");
c.ToggleVisibility(110);Check(c.Visible && c.Form==WidgetForm.Capsule,"fullscreen hotkey reveals capsule only");
c.ToggleVisibility(120);Check(!c.Visible,"second fullscreen hotkey removes override");
c.ApplySystemScene(new(200,false,false),130);Check(c.Visible && c.Form==WidgetForm.Capsule,"fullscreen exit restores normal rules");
c.ToggleVisibility(140);c.ApplySystemScene(new(100,true,false),150);c.ApplySystemScene(new(200,false,false),160);
Check(!c.Visible,"manual hidden state survives fullscreen round trip");
c.ToggleVisibility(170);c.ChangeForm(WidgetForm.Details,180);c.ApplySystemScene(new(200,false,true),190);
Check(c.Form==WidgetForm.Details,"same maximized foreground does not repeatedly collapse");
c.ApplySystemScene(new(300,false,true),200);Check(c.Form==WidgetForm.Capsule,"new maximized application collapses form");

var motion=new MotionController(new(0,0,232,48,24));
motion.Retarget(new(0,0,360,132,24),0,220);var halfway=motion.Sample(85);
motion.Retarget(new(0,0,232,48,24),85,220);var after=motion.Sample(85);
Check(halfway==after,"interrupted motion starts at actual current pose");
motion.Sample(400);Check(!motion.Active && motion.Current.Width==232,"retarget settles at final requested pose");
motion.Retarget(new(20,30,360,464,24),500,280,true);
Check(!motion.Active && motion.Current.Height==464,"reduced motion keeps state change without animation");
var strainMotion=new MotionController(new(0,0,184,32,16));
strainMotion.Retarget(new(0,0,360,512,24),0,360);
var strained=strainMotion.Sample(90);
Check(strained.SurfaceTension>.4f,"large expansion develops a visible transient material strain");
strainMotion.Retarget(new(0,0,184,32,16),90,240);
Check(strainMotion.Sample(90)==strained,"reversing a liquid transition preserves the exact presented contour");
strainMotion.Sample(331);
Check(!strainMotion.Active&&strainMotion.Current.SurfaceTension==0,"liquid contour returns exactly to rest without an idle loop");
strainMotion.Retarget(new(0,0,360,512,24),400,360,true);
Check(strainMotion.Current.SurfaceTension==0&&!strainMotion.Active,"reduced motion has no transient contour strain");
MotionEndpointChecks.Run(Check);
MotionTranslationChecks.Run(Check);
TransportButtonMotionChecks.Run(Check);
TransportLensHandoffChecks.Run(Check);
ReadingMaterialPolicyChecks.Run(Check);
ReadingSupportMaskChecks.Run(Check);
var reducedTarget=new WidgetPose(20,30,360,464,24);
motion.Jump(new(0,0,232,48,24));
motion.Retarget(reducedTarget,600,280);motion.Sample(650);
motion.Retarget(reducedTarget,650,280,true);
Check(!motion.Active && motion.Current==reducedTarget,"enabling reduced motion settles an already active identical target immediately");
if (!args.Contains("--no-stress"))
{
bool stable=true;
for(int i=0;i<1000;i++) {motion.Retarget(new(20,30,i%2==0?360:232,i%2==0?132:48,24),i*11,220);var pose=motion.Sample(i*11+7);stable &= float.IsFinite(pose.Width)&&pose.Width>0;}
Check(stable,"1,000 rapid retargets stay finite and preserve valid sizes");
motion.Sample(12000);Check(!motion.Active,"interrupt stress has no lingering animation");
}
c=Controller();c.RestoreAnchor(new(4000,3000),0);Check(c.Anchor==new WidgetAnchor(960,0),"legacy saved position is ignored");
c.SetMonitor(new("secondary",new(-1280,0,1280,720),new(-1280,0,1280,680),1.5f,false),0,true);
Check(c.Monitor.Primary&&c.Anchor==new WidgetAnchor(960,0),"secondary monitor never changes the fixed primary placement");
WidgetControllerGeometryChecks.Run(Check);

var timestamp=Stopwatch.GetTimestamp();
var media=new MediaSnapshot("test",1,true,"","","",null,PlaybackState.Playing,1,new(TimeSpan.Zero,TimeSpan.FromSeconds(100),TimeSpan.FromSeconds(25),TimeSpan.FromSeconds(5),TimeSpan.FromSeconds(95),timestamp),new(true,true,true,true));
Check(Math.Abs((TimelineProjection.Position(media,timestamp+Stopwatch.Frequency*2)!.Value-TimeSpan.FromSeconds(27)).TotalSeconds)<.01,"playing timeline uses monotonic elapsed time");
Check(TimelineProjection.Position(media with {State=PlaybackState.Paused},timestamp+Stopwatch.Frequency*10)==TimeSpan.FromSeconds(25),"paused progress remains stationary");
Check(TimelineProjection.ClampSeek(media,TimeSpan.FromSeconds(200))==TimeSpan.FromSeconds(95),"seek respects provider range");
Check(TimelineProjection.Position(media with {Timeline=null},timestamp)==null && TimelineProjection.ClampSeek(media with {Capabilities=new()},TimeSpan.FromSeconds(3))==null,"missing timeline or capability cannot synthesize progress");
c=Controller();c.ToggleVisibility(0);c.ApplySystemScene(new(100,true,false),1);c.ToggleVisibility(2);
Check(c.Visible,"fullscreen summon can temporarily reveal manually hidden widget");
c.ToggleVisibility(3);Check(!c.Visible,"second fullscreen toggle only removes temporary summon");
c.ToggleVisibility(4);c.ApplySystemScene(new(200,false,false),5);
Check(!c.Visible,"fullscreen exit after summon preserves prior manual hide");
c.ToggleVisibility(6);Check(c.Visible,"ordinary toggle restores manual visibility independently");
var policy=new CapsuleContentPolicy();var paused=media with {State=PlaybackState.Paused};
policy.Update(paused,100);policy.Tick(5099);
Check(policy.Mode==CapsuleContentMode.Music,"pause 4,999ms keeps capsule music");
policy.Update(paused with {ContentVersion=2,Cover=[1],Title="new metadata"},2000);policy.Tick(5100);
Check(policy.Mode==CapsuleContentMode.Clock,"pause 5,000ms changes capsule without reset by metadata");
Check(policy.ForForm(WidgetForm.Peek)==CapsuleContentMode.Music&&policy.ForForm(WidgetForm.Details)==CapsuleContentMode.Music,"paused expanded forms retain real music");
policy.Update(paused with {SessionToken="replacement"},6000);policy.Tick(10999);
Check(policy.Mode==CapsuleContentMode.Music,"replacement paused session starts its own interval");
policy.Update(media with {SessionToken="replacement"},11000);policy.Tick(16000);
Check(policy.Mode==CapsuleContentMode.Music&&!policy.NeedsTick,"resume cancels pending pause interval");
policy.Update(paused,20000);policy.Tick(30000);
Check(policy.Mode==CapsuleContentMode.Clock,"hidden time counts without restarting pause interval");
policy.Update(MediaSnapshot.Disconnected,30001);
Check(policy.ForForm(WidgetForm.Details)==CapsuleContentMode.Clock,"disconnected expanded form uses clock");
var expected=new[]{(WidgetForm.Capsule,CapsuleContentMode.Clock,200f,52f),(WidgetForm.Peek,CapsuleContentMode.Clock,240f,144f),(WidgetForm.Details,CapsuleContentMode.Clock,360f,300f),(WidgetForm.Capsule,CapsuleContentMode.Music,256f,52f),(WidgetForm.Peek,CapsuleContentMode.Music,320f,176f),(WidgetForm.Details,CapsuleContentMode.Music,360f,476f)};
Check(expected.All(x=>{var metrics=WidgetLayoutMetrics.Resolve(x.Item1,x.Item2);return metrics.Width==x.Item3&&metrics.Height==x.Item4;}),"all six forms use frozen layout metrics");
c=Controller();var center=c.Anchor.CenterX;c.UpdateContent(CapsuleContentMode.Music,true,true,296,0);c.ChangeForm(WidgetForm.Details,10);c.Tick(1000);
Check(Math.Abs(c.Motion.Current.X+c.Motion.Current.Width/2-center)<.01&&c.Motion.Current.Y==0,"content expansion preserves horizontal center and top anchor");
Check(new WidgetPreferences{Version=1,AnchorX=.3,AnchorY=900,MonitorId="old-display",CompanionColor=0x123456}.Validated() is {Version:4,MonitorId:null,AnchorX:.5,AnchorY:0,CompanionEnabled:true,LiquidGlassEnabled:false,CompanionColor:0xff123456},"version4 migration normalizes legacy placement and builtin defaults");


LiquidPrototypeChecks.Run(Check);
UiNavigationChecks.Run(Check);
DetailsChromeChecks.Run(Check);
UiCompanionChecks.Run(Check);
CompanionLiquidChecks.Run(Check);
TypographyPageChecks.Run(Check);
WidgetBoardChecks.Run(Check);
OutsidePointerChecks.Run(Check);
ClockStructuredChecks.Run(Check);
HiddenWidgetChecks.Run(Check);
CompactLayoutChecks.Run(Check);
HomeLayoutChecks.Run(Check);
HomePresentationChecks.Run(Check);
HomePopupChecks.Run(Check);

ManualTextInkPreferenceChecks.Run(Check);
Console.WriteLine($"{checks} checks passed.");

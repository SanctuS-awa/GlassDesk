using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using GlassDesk.Core;
using Microsoft.UI.Dispatching;

namespace GlassDesk.App;

/// <summary>Explicit QA mode records resources, never media content or simulated songs.</summary>
internal sealed class ResidencyProbe : IDisposable
{
    readonly string path;
    readonly Process process=Process.GetCurrentProcess();
    readonly Stopwatch elapsed=Stopwatch.StartNew();
    readonly DispatcherQueueTimer sampleTimer,cycleTimer;
    readonly WidgetController controller;
    readonly Func<double> clock;
    readonly Action wake;
    readonly List<ResourceSample> samples=[];
    readonly List<PointerSample> pointers=[];
    readonly List<ContentSample> contents=[];
    TimeSpan previousCpu;
    double previousTime;
    int remaining,completed;
    readonly bool stressEnabled;
    readonly Func<object>? typography;
    readonly Func<object>? materialReadability;
    readonly Func<object>? desktopMaterial;
    readonly bool capsulePeekSmoke;
    [DllImport("user32.dll")] static extern uint GetGuiResources(nint processHandle,uint flag);
    [DllImport("user32.dll")] static extern nint GetForegroundWindow();
    public ResidencyProbe(DispatcherQueue queue,WidgetController controller,Func<double> clock,Action wake,string path,int cycles,Func<object>? typography=null,Func<object>? materialReadability=null,bool capsulePeekSmoke=false,Func<object>? desktopMaterial=null)
    {
        this.controller=controller;this.clock=clock;this.wake=wake;this.path=path;this.typography=typography;this.materialReadability=materialReadability;remaining=cycles;stressEnabled=cycles>0;this.capsulePeekSmoke=capsulePeekSmoke;this.desktopMaterial=desktopMaterial;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        sampleTimer=queue.CreateTimer();sampleTimer.Interval=TimeSpan.FromSeconds(capsulePeekSmoke?2:30);sampleTimer.Tick+=(_,_)=>Sample();
        cycleTimer=queue.CreateTimer();cycleTimer.Interval=capsulePeekSmoke?TimeSpan.FromSeconds(12):TimeSpan.FromMilliseconds(70);
        cycleTimer.Tick+=(_,_)=>
        {
            if(remaining<=0) {cycleTimer.Stop();controller.ChangeForm(WidgetForm.Capsule,clock());wake();Sample();return;}
            controller.ChangeForm(capsulePeekSmoke?(completed%2==0?WidgetForm.Peek:WidgetForm.Capsule):(WidgetForm)((completed+1)%3),clock());remaining--;completed++;wake();
        };
        Sample();sampleTimer.Start();if(cycles>0)cycleTimer.Start();
    }
    public void RecordPointer(HostPointerEvent pointer,int control,HostContext context)
    {
        if(pointer.Kind is not (HostEventKind.Down or HostEventKind.Up))return;
        if(pointers.Count==256)pointers.RemoveAt(0);
        pointers.Add(new(elapsed.Elapsed.TotalSeconds,pointer.Kind,control,pointer.ScreenX,pointer.ScreenY,(long)GetForegroundWindow(),context,controller.Form));
        Sample();
    }
    public void RecordContent(CapsuleContentMode capsuleMode,CapsuleContentMode presentedMode,PlaybackState mediaState,WidgetForm form,bool visible)
    {
        if(contents.Count>0)
        {
            var previous=contents[^1];
            if(previous.CapsuleMode==capsuleMode&&previous.PresentedMode==presentedMode&&previous.MediaState==mediaState&&previous.Form==form&&previous.Visible==visible)return;
        }
        if(contents.Count==2048)contents.RemoveAt(0);
        contents.Add(new(elapsed.Elapsed.TotalSeconds,capsuleMode,presentedMode,mediaState,form,visible));
    }
    void Sample()
    {
        process.Refresh();double seconds=elapsed.Elapsed.TotalSeconds;TimeSpan cpu=process.TotalProcessorTime;
        double percent=samples.Count>0&&seconds>previousTime?(cpu-previousCpu).TotalSeconds/(seconds-previousTime)/Environment.ProcessorCount*100:0;
        samples.Add(new(DateTimeOffset.UtcNow,seconds,process.PrivateMemorySize64,process.HandleCount,GetGuiResources(process.Handle,0),GetGuiResources(process.Handle,1),percent,completed,controller.Form));
        previousCpu=cpu;previousTime=seconds;
        string temporary=path+".tmp";
        object? textDiagnostics=null;
        try{textDiagnostics=typography?.Invoke();}
        catch(Exception error){textDiagnostics=new {Error=error.GetType().Name,HResult=error.HResult};}
        File.WriteAllText(temporary,JsonSerializer.Serialize(new {SchemaVersion=3,Test=capsulePeekSmoke?"authorized 60-second capsule/peek smoke (4 slow transitions)":stressEnabled?"resident resources and application-driven form stress":"normal runtime observation (no stress)",Pid=process.Id,ElapsedSeconds=seconds,CompletedFormTransitions=completed,SystemSettingsChanged=false,MediaTestSource=false,Samples=samples,PointerEvents=pointers,ContentEvents=contents,Typography=textDiagnostics,MaterialReadability=materialReadability?.Invoke(),DesktopMaterial=desktopMaterial?.Invoke()},new JsonSerializerOptions {WriteIndented=true}));
        File.Move(temporary,path,true);
    }
    public void Dispose() {sampleTimer.Stop();cycleTimer.Stop();Sample();process.Dispose();}
    sealed record ResourceSample(DateTimeOffset At,double ElapsedSeconds,long PrivateBytes,int Handles,uint GdiObjects,uint UserObjects,double NormalizedCpuPercent,int Transitions,WidgetForm Form);
    sealed record PointerSample(double ElapsedSeconds,HostEventKind Kind,int ControlId,int ScreenX,int ScreenY,long Foreground,HostContext Context,WidgetForm Form);
    sealed record ContentSample(double ElapsedSeconds,CapsuleContentMode CapsuleMode,CapsuleContentMode PresentedMode,PlaybackState MediaState,WidgetForm Form,bool Visible);
}

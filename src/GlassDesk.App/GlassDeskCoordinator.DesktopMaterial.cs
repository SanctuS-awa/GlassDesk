using GlassDesk.Core;
using Microsoft.UI.Dispatching;
using System.Diagnostics;

namespace GlassDesk.App;

internal sealed partial class GlassDeskCoordinator
{
    readonly DesktopMaterialSession materialSession;
    DesktopWallpaperSource? ownedWallpaper;
    DispatcherQueueTimer? ownedWallpaperTimer;
    DispatcherQueueTimer? ownedCompositorGuard;
    string? expectedCompositorIdentity;
    ScreenRect? ownedWallpaperMonitor;
    int ownedWallpaperGeneration;
    bool ownedWallpaperLoading,experimentalDesktopMaterialStopped;
    Exception? experimentalDesktopMaterialFailure;
    // Native callbacks persist the fuse synchronously before queued teardown.
    // Observe that latch too; a queued UI callback must not use the gap to draw.
    bool CandidateGraphicsStopped=>experimentalDesktopMaterialStopped
        ||materialSession.IsExperimentalCandidate&&materialSession.IsExperimentTripped;

    bool EnsureExperimentalBodyFits()
    {
        if(!host.IsExperimentalDesktopMaterialCandidate||controller.LogicalLayoutFitsPhysicalBodyLimit!=false)return true;
        // The logical content must not be silently cropped to satisfy the
        // experimental pixel budget. Stop before submitting that presentation.
        StopExperimentalDesktopMaterial(new InvalidOperationException(
            "当前缩放下的组件超过系统透镜每边 1024 像素的实验边界；已停止该预览，未压缩或裁切内容。"),"geometry-limit");
        return false;
    }

    void PrepareExperimentalCompositorGuard()
    {
        if(!materialSession.IsExperimentalCandidate)return;
        expectedCompositorIdentity=ReadCompositorIdentity();
        if(expectedCompositorIdentity is null){
            materialSession.MarkFault("Could not observe the current session compositor before candidate host creation.");
            throw new InvalidOperationException("无法建立独立实验的合成器监测，未创建光学宿主。");
        }
    }
    static string? ReadCompositorIdentity()
    {
        try{
            using var current=Process.GetCurrentProcess();int session=current.SessionId;
            var processes=Process.GetProcessesByName("dwm");var ids=new List<int>();
            try{foreach(var process in processes)if(process.SessionId==session)ids.Add(process.Id);}
            finally{foreach(var process in processes)process.Dispose();}
            return ids.Count==1?ids[0].ToString(System.Globalization.CultureInfo.InvariantCulture):null;
        }catch(Exception){return null;}
    }

    void StartOwnedWallpaperRefresh()
    {
        if(!host.IsExperimentalDesktopMaterialCandidate||CandidateGraphicsStopped)return;
        ownedCompositorGuard=queue.CreateTimer();ownedCompositorGuard.Interval=TimeSpan.FromSeconds(1);
        ownedCompositorGuard.Tick+=(_,_)=>{
            if(!CandidateGraphicsStopped&&ReadCompositorIdentity()!=expectedCompositorIdentity)
                StopExperimentalDesktopMaterial(new InvalidOperationException("Session compositor changed or became unobservable; cause not attributed."),"compositor-observer");
        };
        ownedCompositorGuard.Start();
        if(!host.IsOwnedImageCandidate)return;
        notice="液态玻璃实验：仅折射本地静态壁纸；不读取或折射其他应用窗口。";
        ownedWallpaperTimer=queue.CreateTimer();ownedWallpaperTimer.Interval=TimeSpan.FromSeconds(30);
        ownedWallpaperTimer.Tick+=(_,_)=>{if(controller.Visible)RefreshOwnedWallpaper();};
        ownedWallpaperTimer.Start();CheckOwnedWallpaperMonitor();
    }
    void CheckOwnedWallpaperMonitor()
    {
        if(ownedWallpaperTimer is null||CandidateGraphicsStopped||disposed)return;
        var bounds=controller.Monitor.Bounds;
        if(ownedWallpaperMonitor==bounds)return;
        ownedWallpaperMonitor=bounds;ownedWallpaperGeneration++;ownedWallpaper=null;
        host.InvalidateOwnedWallpaper();
        RefreshOwnedWallpaper();
    }
    async void RefreshOwnedWallpaper()
    {
        if(disposed||quitting||CandidateGraphicsStopped||ownedWallpaperLoading||!host.IsOwnedImageCandidate)return;
        var bounds=controller.Monitor.Bounds;int generation=ownedWallpaperGeneration;
        ownedWallpaperLoading=true;
        try{
            if(ownedWallpaper is not null&&ownedWallpaper.Stamp==DesktopWallpaperSource.ReadStamp(bounds))return;
            var source=await DesktopWallpaperSource.LoadAsync(bounds);
            if(disposed||quitting||CandidateGraphicsStopped||generation!=ownedWallpaperGeneration||bounds!=controller.Monitor.Bounds)return;
            host.SetOwnedWallpaper(source.Pixels,source.Width,source.Height,source.Mapping,source.BackgroundArgb);
            ownedWallpaper=source;Render();
        }
        catch(Exception error){
            if(disposed||quitting||generation!=ownedWallpaperGeneration)return;
            if(materialSession.IsExperimentTripped){StopExperimentalDesktopMaterial(error,"wallpaper-upload");return;}
            ownedWallpaper=null;host.InvalidateOwnedWallpaper();
            diagnostics.RecordError("owned-wallpaper-input",error);
            notice="壁纸光学输入暂不可用，已保留纯色材质："+error.Message;
            // File/unsupported-input failures do not create another graphics backend.
            Render();Changed?.Invoke();
        }
        finally{
            ownedWallpaperLoading=false;
            if(!disposed&&!quitting&&!CandidateGraphicsStopped&&generation!=ownedWallpaperGeneration)RefreshOwnedWallpaper();
        }
    }
    void StopExperimentalDesktopMaterial(Exception error,string component)
    {
        if(experimentalDesktopMaterialStopped)return;
        experimentalDesktopMaterialFailure=error;
        experimentalDesktopMaterialStopped=true;materialSession.MarkFault($"{component}: 0x{error.HResult:X8} ({error.Message})");
        CancelHomeFolderRead();CloseHomeNavigationWindow();outside.Update(null);
        diagnostics.RecordError(component,error);
        notice="光学实验已熔断，已停止显示与所有图形重试。请保留诊断，不要自动重新授权。";
        host.TripDesktopMaterial(component);StopRuntimeTimers();StopOwnedWallpaperRefresh();
        try{host.SetVisible(false);}catch(Exception hideError){diagnostics.RecordError("owned-hide",hideError);}
        Changed?.Invoke();
    }
    void StopOwnedWallpaperRefresh()
    {ownedWallpaperGeneration++;ownedWallpaperTimer?.Stop();ownedWallpaperTimer=null;ownedWallpaper=null;ownedCompositorGuard?.Stop();ownedCompositorGuard=null;}
}

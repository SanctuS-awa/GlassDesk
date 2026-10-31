using GlassDesk.Platform;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;

namespace GlassDesk.App;

internal static class Program
{
    [STAThread]
    static void Main(string[] args)
    {
        GlassDesk.Core.DesktopMaterialSession? startupMaterial=null;
        bool qa=args.Any(a=>a.StartsWith("--qa-report=",StringComparison.Ordinal));
#if GLASSDESK_UI_PREVIEW && GLASSDESK_SYSTEM_GLASS_PREVIEW
        qa|=QaSystemCompositorStartGate.IsQaInvocation(args);
#endif
        string errorDirectory=LaunchOptions.EarlyErrorDirectory(args);
        bool preview=LaunchOptions.PreviewBuild||args.Contains("--ui-preview",StringComparer.Ordinal);
        void WriteLog(string name,string value)
        {
            string directory=!preview&&name=="qa-bootstrap.log"?AppContext.BaseDirectory:errorDirectory;
            try{Directory.CreateDirectory(directory);File.AppendAllText(Path.Combine(directory,name),value);}
            catch(Exception failure) when(failure is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
            {
                if(!preview)return;
                try{Directory.CreateDirectory(LaunchOptions.PreviewErrorFallback);File.AppendAllText(Path.Combine(LaunchOptions.PreviewErrorFallback,name),value);}
                catch(Exception fallback) when(fallback is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException){ }
            }
        }
        void Trace(string stage){if(qa)WriteLog("qa-bootstrap.log",$"{DateTimeOffset.Now:O} {stage}\n");}
        try
        {
            Trace("main-enter");
            var options=LaunchOptions.Parse(args);
            DesktopMaterialLaunch.Validate(options);
#if GLASSDESK_UI_PREVIEW && GLASSDESK_SYSTEM_GLASS_PREVIEW
            QaSystemCompositorStartGate.ValidateLaunchRequest(args,options);
#endif
            if(options.ArmOwnedImageGlass||options.ArmSystemCompositorPreview){
                var result=new GlassDesk.Core.DesktopMaterialExperimentFuse(DesktopMaterialLaunch.FusePath(options)).ArmNextStart();
                if(result!=GlassDesk.Core.DesktopMaterialFuseResult.Armed)
                    throw new InvalidOperationException($"未授权光学实验：{result}。运行中、熔断或损坏状态需先审查，不应自动清除。");
                return; // Deliberately before XAML, native ABI loading or HWND creation.
            }
            errorDirectory=options.ErrorDirectory;
            Trace("options-parsed");
            using var gate=new SingleInstanceGate(options.Identity);
            Trace(gate.IsPrimary?"primary-instance":"secondary-instance");
            if(options.QuitExisting) { if(!gate.IsPrimary&&!gate.RequestQuit())Environment.ExitCode=2;return; }
            if(!gate.IsPrimary)
            {
#if GLASSDESK_UI_PREVIEW && GLASSDESK_SYSTEM_GLASS_PREVIEW
                if(options.SystemCompositorPreview)
                    throw new InvalidOperationException("QA system-compositor preview must be primary; refusing existing-instance IPC.");
#endif
                if(!gate.RequestExistingSettings())
                {
                    Environment.ExitCode=2;
                    if(!qa)FailureReporter.NotifyOnce("已有 GlassDesk 实例暂未响应，请稍后重试或从托盘退出后重新打开。",null,attemptedLog:false);
                }
                return;
            }
            startupMaterial=DesktopMaterialLaunch.CreateSession(options);
            options=options with {DesktopSession=startupMaterial};
#if GLASSDESK_UI_PREVIEW && GLASSDESK_SYSTEM_GLASS_PREVIEW
            QaSystemCompositorStartGate.WaitIfRequired(args,options);
#endif
            WinRT.ComWrappersSupport.InitializeComWrappers();
            Trace("com-wrappers-ready");
            Application.Start(initializationArgs=>
            {
                Trace("xaml-callback");
                SynchronizationContext.SetSynchronizationContext(new DispatcherQueueSynchronizationContext(DispatcherQueue.GetForCurrentThread()));
                _=new ResidentApplication(options);
                Trace("application-created");
            });
            Trace("xaml-exit");
        }
        catch(Exception error)
        {
            startupMaterial?.MarkFault($"bootstrap: 0x{error.HResult:X8}");
            Environment.ExitCode=1;
            string? path=FailureReporter.Write(errorDirectory,"bootstrap-error.txt",error,preview);
            if(!qa&&!args.Contains("--quit",StringComparer.Ordinal))FailureReporter.NotifyOnce("GlassDesk 未能启动，请保留诊断文件以便排查。",path);
            Trace("bootstrap-error");
        }
    }
}

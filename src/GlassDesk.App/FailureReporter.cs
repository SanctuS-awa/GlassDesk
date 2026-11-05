using System.Runtime.InteropServices;

namespace GlassDesk.App;

internal static class FailureReporter
{
    static int _notified;
    public static string? Write(string directory,string name,Exception error,bool preview)
    {
        string value=$"{DateTimeOffset.UtcNow:O}\n{error.GetType().FullName}\nHRESULT=0x{error.HResult:X8}\n{error.StackTrace}\n";
        // Whitelisted numerical diagnostic only. Do not dump arbitrary exception
        // messages/data that could contain private player or file information.
        if(error.Data["LiquidGlassCaptureStage"] is uint inkStage)value+=$"LiquidGlassCaptureStage={inkStage}\n";
        if(error.Data["LiquidGlassCaptureStageHResult"] is uint inkStageHr)value+=$"LiquidGlassCaptureStageHResult=0x{inkStageHr:X8}\n";
        string fallback=preview?LaunchOptions.PreviewErrorFallback:Path.Combine(Path.GetTempPath(),"GlassDesk","errors");
        foreach(string target in new[]{directory,fallback}.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            try{Directory.CreateDirectory(target);string path=Path.Combine(target,name);File.AppendAllText(path,value);return path;}
            catch(Exception failure)when(failure is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException or System.Security.SecurityException){ }
        }
        return null;
    }
    public static void NotifyOnce(string message,string? path,bool attemptedLog=true)
    {
        if(Interlocked.Exchange(ref _notified,1)!=0)return;
        MessageBoxW(0,message+(path is not null?"\n诊断文件："+path:attemptedLog?"\n诊断文件也未能写入，请检查数据目录权限。":""),"GlassDesk",0x10);
    }
    public static bool ConfirmExitWithoutSaving() => MessageBoxW(0,
        "部分配置未能保存。仍然退出吗？\n选择“否”返回设置，检查数据目录后重试。","GlassDesk",0x34|0x100)==6;

    [DllImport("user32.dll",CharSet=CharSet.Unicode)] static extern int MessageBoxW(nint owner,string text,string caption,uint type);
}

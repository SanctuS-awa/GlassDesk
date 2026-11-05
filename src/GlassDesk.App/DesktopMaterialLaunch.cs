using GlassDesk.Core;

namespace GlassDesk.App;

/// <summary>Incident containment, checked before Application.Start or native load.</summary>
internal static class DesktopMaterialLaunch
{
    public static string FusePath(LaunchOptions options)=>Path.Combine(options.ErrorDirectory,"desktop-material-20261024-v1.json");
    public static bool AllowedOnThisMachine(LaunchOptions options)=>AllowedForMachine(options,Environment.MachineName);
    internal static bool AllowedForMachine(LaunchOptions options,string machineName)
    {
        bool previewIntent=options.LiquidGlassCapturePreview||options.ArmLiquidGlassCapturePreview;
        bool common=options.UiPreview&&options.ExplicitDataDirectory&&options.DataDirectory is not null
            &&string.Equals(options.IndependentGraphicsMachine,machineName,StringComparison.OrdinalIgnoreCase);
        if(!common)return false;
        if(previewIntent)
            return LaunchOptions.LiquidGlassCapturePreviewBuild
                &&string.Equals(machineName,"SHENG-AWA",StringComparison.OrdinalIgnoreCase);
        return (!string.Equals(machineName,"SHENG-AWA",StringComparison.OrdinalIgnoreCase)
                ||options.IncidentHostShortTest&&options.QaReport is not null&&options.QaCycles==4)
            &&(!options.LiquidGlassCaptureGlass
                ||options.IncidentHostShortTest&&options.QaReport is not null&&options.QaCycles==4
                    &&string.Equals(machineName,"SHENG-AWA",StringComparison.OrdinalIgnoreCase));
    }

    public static void Validate(LaunchOptions options)
    {
        bool previewIntent=options.LiquidGlassCapturePreview||options.ArmLiquidGlassCapturePreview;
        int selectedModes=(options.OwnedImageGlass?1:0)+(options.LiquidGlassCaptureGlass?1:0)
            +(options.LiquidGlassCapturePreview?1:0)+(options.ArmOwnedImageGlass?1:0)
            +(options.ArmLiquidGlassCapturePreview?1:0);
        if((options.ArmOwnedImageGlass||options.ArmLiquidGlassCapturePreview)
            &&(options.OwnedImageGlass||options.LiquidGlassCaptureGlass||options.LiquidGlassCapturePreview
                ||options.ArmOwnedImageGlass&&options.ArmLiquidGlassCapturePreview))
            throw new InvalidOperationException("授权与运行必须分为两次显式操作，不允许每次启动自动重新授权。");
        if(options.OwnedImageGlass&&options.LiquidGlassCaptureGlass
            ||options.LiquidGlassCapturePreview&&(options.OwnedImageGlass||options.LiquidGlassCaptureGlass)
            ||options.ArmLiquidGlassCapturePreview&&options.ArmOwnedImageGlass)
            throw new InvalidOperationException("一次启动只能选择一个实验桌面材质后端。");
        if(previewIntent&&!LaunchOptions.LiquidGlassCapturePreviewBuild)
            throw new InvalidOperationException("系统合成器持续预览仅在 GlassDeskUiPreview 与 GlassDeskLiquidGlassPreview 同时启用的开发构建中可用。");
        if(previewIntent&&(!options.UiPreview||!options.ExplicitDataDirectory||options.DataDirectory is null
            ||!string.Equals(options.IndependentGraphicsMachine,"SHENG-AWA",StringComparison.OrdinalIgnoreCase)
            ||options.IncidentHostShortTest||options.QaReport is not null||options.QaCycles!=0))
            throw new InvalidOperationException("系统合成器持续预览必须使用 SHENG-AWA、显式独立预览数据目录，并关闭短测与 QA 周期。");
        if(options.LiquidGlassCaptureGlass&&!options.IncidentHostShortTest)
            throw new InvalidOperationException("系统合成器候选仅允许使用现有事故主机短测授权。");
        if(options.IncidentHostShortTest&&(!options.UiPreview||!options.ExplicitDataDirectory
            ||options.DataDirectory is null||options.QaReport is null||options.QaCycles!=4
            ||!string.Equals(Environment.MachineName,"SHENG-AWA",StringComparison.OrdinalIgnoreCase)
            ||!options.OwnedImageGlass&&!options.LiquidGlassCaptureGlass&&!options.ArmOwnedImageGlass))
            throw new InvalidOperationException("授权本机短测必须使用独立预览数据、QA 报告、4 次慢速胶囊/小展开转换与一次性熔断授权。");
        if(selectedModes==0)return;
        if(!AllowedOnThisMachine(options))throw new InvalidOperationException(
            "桌面光学实验尚未通过独立设备验证；已在本次事故主机禁用。仅可在独立测试机使用独立预览数据目录。未创建图形宿主。");
        if(options.QaMaterialFrame is not null||options.QaSnapshotRequest is not null)
            throw new InvalidOperationException("桌面材质实验不支持旧捕获/快照诊断入口。");
    }
    public static DesktopMaterialSession CreateSession(LaunchOptions options)
    {
        Validate(options);
        if(options.ArmOwnedImageGlass||options.ArmLiquidGlassCapturePreview)
            throw new InvalidOperationException("Fuse arm operations must exit before creating a desktop session.");
        var fuse=new DesktopMaterialExperimentFuse(FusePath(options));
        DesktopMaterialBackend requestedBackend=options.LiquidGlassCaptureGlass||options.LiquidGlassCapturePreview
            ?DesktopMaterialBackend.LiquidGlassCapture
            :options.OwnedImageGlass?DesktopMaterialBackend.OwnedImageCandidate:DesktopMaterialBackend.LegacySystemMaterial;
        bool candidateRequested=options.OwnedImageGlass||options.LiquidGlassCaptureGlass||options.LiquidGlassCapturePreview;
        var session=DesktopMaterialSession.CreateBeforeHostCreation(
            requestedBackend,
            fuse,candidateRequested?DesktopMaterialBackend.Solid:DesktopMaterialBackend.LegacySystemMaterial,
            AllowedOnThisMachine(options),options.LiquidGlassCapturePreview);
        if((options.IncidentHostShortTest||options.LiquidGlassCapturePreview)&&candidateRequested&&session.Backend!=requestedBackend)
            throw new InvalidOperationException("本机短测授权缺失或熔断，未创建图形宿主。");
        return session;
    }
}

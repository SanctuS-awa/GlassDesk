using System.Globalization;
using GlassDesk.Core;
namespace GlassDesk.App;

internal sealed record LaunchOptions(string? DataDirectory=null,string? QaReport=null,int QaCycles=0,bool NoSettings=false,bool QuitExisting=false,string? QaMaterialFrame=null,bool UiPreview=false,string? QaSnapshotRequest=null,float? QaDetailsHeight=null,
    bool OwnedImageGlass=false,bool ArmOwnedImageGlass=false,string? IndependentGraphicsMachine=null,
    bool ExplicitDataDirectory=false,DesktopMaterialSession? DesktopSession=null,bool IncidentHostShortTest=false,bool SystemCompositorGlass=false,
    bool SystemCompositorPreview=false,bool ArmSystemCompositorPreview=false,bool QaSyntheticMedia=false,string? QaVisualState=null,string? QaSyntheticMediaMode=null)
{
    internal static bool PreviewBuild
    {
        get
        {
#if GLASSDESK_UI_PREVIEW
            return true;
#else
            return false;
#endif
        }
    }
    internal static bool SystemCompositorPreviewBuild
    {
        get
        {
#if GLASSDESK_SYSTEM_GLASS_PREVIEW
            return true;
#else
            return false;
#endif
        }
    }
    public InstanceIdentity Identity=>UiPreview?InstanceIdentity.UiPreview(DataDirectory??Path.Combine(AppContext.BaseDirectory,"preview-data")):InstanceIdentity.Formal;
    public string ErrorDirectory=>DataDirectory??Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"GlassDesk");
    public static string PreviewErrorFallback=>Path.Combine(Path.GetTempPath(),"GlassDesk.UiPreview","errors");
    static string AbsoluteDataDirectory(string value)
    {
        string path=Path.GetFullPath(value);
        string root=Path.GetPathRoot(path)??string.Empty;
        if(path[root.Length..].IndexOfAny(Path.GetInvalidFileNameChars().Where(c=>c is not ('/' or '\\')).ToArray())>=0)
            throw new ArgumentException("The data directory contains invalid path characters.",nameof(value));
        return path;
    }
    public static string EarlyErrorDirectory(string[] args)
    {
        bool preview=PreviewBuild||args.Contains("--ui-preview",StringComparer.Ordinal);
        if(!preview)return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"GlassDesk");
        try
        {
            string? data=args.LastOrDefault(a=>a.StartsWith("--data-dir=",StringComparison.Ordinal));
            return data is null?Path.Combine(AppContext.BaseDirectory,"preview-data"):AbsoluteDataDirectory(data[11..]);
        }
        catch(ArgumentException){return PreviewErrorFallback;}
        catch(NotSupportedException){return PreviewErrorFallback;}
        catch(System.IO.IOException){return PreviewErrorFallback;}
    }
    public static LaunchOptions Parse(string[] args)
    {
        string? data=null,report=null,material=null,snapshot=null;int cycles=0;bool hidden=false,quit=false,preview=PreviewBuild;
        float? detailsHeight=null;string? visualState=null;
        bool owned=false,systemCompositor=false,armOwned=false,explicitData=false,incidentShortTest=false;
        bool systemCompositorPreview=false,armSystemCompositorPreview=false,syntheticMedia=false;string? syntheticMediaMode=null,independentMachine=null;
        const string syntheticMediaModePrefix="--qa-synthetic-media=";
        foreach(string argument in args)
        {
            if(argument.StartsWith("--data-dir=",StringComparison.Ordinal)){data=AbsoluteDataDirectory(argument[11..]);explicitData=true;}
            else if(argument.StartsWith("--qa-report=",StringComparison.Ordinal))report=Path.GetFullPath(argument[12..]);
            else if(argument.StartsWith("--qa-cycles=",StringComparison.Ordinal) && int.TryParse(argument[12..],out int count))cycles=Math.Clamp(count,0,10000);
            else if(argument=="--no-settings")hidden=true;
            else if(argument=="--quit")quit=true;
            else if(argument=="--ui-preview")preview=true;
            else if(argument=="--live-desktop-glass")throw new InvalidOperationException("已移除屏幕采集后端；用户要求禁止读取屏幕。");
            else if(argument=="--owned-image-glass")owned=true;
            else if(argument=="--system-compositor-glass")systemCompositor=true;
            else if(argument=="--authorized-incident-host-short-test")incidentShortTest=true;
            else if(argument=="--arm-owned-image-glass")armOwned=true;
            else if(argument=="--system-compositor-preview")systemCompositorPreview=true;
            else if(argument=="--arm-system-compositor-preview")armSystemCompositorPreview=true;
            else if(argument=="--qa-synthetic-media")syntheticMedia=true;
            else if(argument.StartsWith(syntheticMediaModePrefix,StringComparison.Ordinal))
            {
                syntheticMedia=true;
                string mode=argument[syntheticMediaModePrefix.Length..];
                if(mode is not ("playlist" or "disconnected"))
                    throw new InvalidOperationException("合成媒体模式仅支持 playlist 或 disconnected。");
                if(syntheticMediaMode is not null&&!StringComparer.Ordinal.Equals(syntheticMediaMode,mode))
                    throw new InvalidOperationException("不能同时选择多个合成媒体模式。");
                syntheticMediaMode=mode;
            }
            else if(argument.StartsWith("--qa-visual-state=",StringComparison.Ordinal))visualState=argument[18..];
            else if(argument.StartsWith("--independent-graphics-machine=",StringComparison.Ordinal))independentMachine=argument[31..];
            else if(argument.StartsWith("--qa-material-frame=",StringComparison.Ordinal))material=Path.GetFullPath(argument[20..]);
            else if(argument.StartsWith("--qa-snapshot-request=",StringComparison.Ordinal))snapshot=Path.GetFullPath(argument[22..]);
            // Preview-only acceptance hook: exercises the short-workarea Details layout without
            // changing the real work area (360/400 DIP compact screenshots).
            else if(argument.StartsWith("--qa-details-height=",StringComparison.Ordinal)
                && float.TryParse(argument[20..],NumberStyles.Float,CultureInfo.InvariantCulture,out float qaHeight)
                && float.IsFinite(qaHeight))detailsHeight=Math.Clamp(qaHeight,300f,512f);
        }
        if(preview&&data is null)data=Path.Combine(AppContext.BaseDirectory,"preview-data");
        if(syntheticMedia&&(!PreviewBuild||!SystemCompositorPreviewBuild||!explicitData||!systemCompositorPreview))
            throw new InvalidOperationException("合成媒体仅可用于带独立数据目录的系统玻璃开发预览。");
        if(visualState is not null && (!syntheticMedia || visualState is not ("peek" or "details")))
            throw new InvalidOperationException("固定视觉状态仅供明确启用合成数据的隔离开发预览，可选peek/details。");
        return new(data,report,cycles,hidden,quit,material,preview,preview?snapshot:null,preview?detailsHeight:null,owned,armOwned,independentMachine,explicitData,null,incidentShortTest,systemCompositor,systemCompositorPreview,armSystemCompositorPreview,syntheticMedia,visualState,syntheticMediaMode);
    }
}

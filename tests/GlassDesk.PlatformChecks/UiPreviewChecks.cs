using System.Text.Json;
using System.Text.Json.Nodes;
using GlassDesk.App;
using GlassDesk.Core;
using GlassDesk.Platform;

internal static class UiPreviewChecks
{
    public static void Run(Action<bool,string> check,string root)
    {
        foreach(int version in new[]{1,2,3})
        {
            using var store=new PreferenceStore(Path.Combine(root,"ui-migration-"+version));
            Directory.CreateDirectory(store.DirectoryPath);
            var legacy=new WidgetPreferences {Version=version,Blur=17,TintOpacity=.43f,AnchorX=.72,AnchorY=45,
                CompanionColor=0x0012abcd,HotkeyModifiers=6,HotkeyKey=75,RunAtStartup=true,CompanionEnabled=false};
            var json=JsonSerializer.SerializeToNode(legacy)!.AsObject();json.Remove(nameof(WidgetPreferences.CompanionAppearanceId));
            File.WriteAllText(store.SettingsPath,json.ToJsonString());
            var migrated=store.Load();
            check(migrated.Version==4&&migrated.LastDetailsPageId==DetailsPageController.Home&&migrated.CompanionAppearanceId==CompanionAppearanceIds.Original&&
                migrated.Blur==17&&migrated.TintOpacity==.43f&&migrated.AnchorX==.5&&migrated.AnchorY==0&&
                migrated.HotkeyKey==75&&migrated.RunAtStartup&&!migrated.CompanionEnabled&&migrated.CompanionColor==0xff12abcd,
                $"v{version} to v4 normalizes the details page and fixed anchor while preserving other choices");
            json[nameof(WidgetPreferences.Version)]=3;json[nameof(WidgetPreferences.CompanionAppearanceId)]="future.appearance";
            File.WriteAllText(store.SettingsPath,json.ToJsonString());
            var unknown=store.Load();
            check(unknown.CompanionAppearanceId==CompanionAppearanceIds.Original&&unknown.AnchorX==.5&&unknown.AnchorY==0&&unknown.HotkeyKey==75&&
                Directory.GetFiles(store.DirectoryPath,"settings.corrupt-*").Length==0,"unknown appearance only falls back that field");
        }
        string directory=Path.Combine(root,"Preview A");
        var a=InstanceIdentity.UiPreview(directory);var b=InstanceIdentity.UiPreview(Path.Combine(root,"Preview B"));
        var equivalent=InstanceIdentity.UiPreview(Path.GetFullPath(directory).ToUpperInvariant()+Path.DirectorySeparatorChar);
        check(a==equivalent,"preview identity normalizes absolute path, Windows case and trailing separators");
        check(a.Namespace!=b.Namespace&&a.MutexName!=b.MutexName&&a.WindowMarker!=b.WindowMarker&&
            a.SettingsMessageName!=b.SettingsMessageName&&a.QuitMessageName!=b.QuitMessageName,"different preview roots isolate every IPC channel");
        check(InstanceIdentity.Formal.MutexName==@"Local\GlassDesk.Resident.v1.643A4E18"&&
            InstanceIdentity.Formal.SettingsMessageName=="GlassDesk.OpenSettings.v1.643A4E18"&&
            InstanceIdentity.Formal.QuitMessageName=="GlassDesk.Quit.v1.643A4E18","formal 0.2 IPC names remain compatible");
        var options=LaunchOptions.Parse(["--ui-preview","--data-dir="+directory,"--quit"]);
        check(options.UiPreview&&options.QuitExisting&&options.Identity==a&&options.ErrorDirectory==Path.GetFullPath(directory),
            "preview quit and errors target only the explicit preview root");
        check(LaunchOptions.Parse(["--ui-preview"]).DataDirectory==Path.Combine(AppContext.BaseDirectory,"preview-data"),"preview default stays beside the application");
        check(LaunchOptions.EarlyErrorDirectory(["--ui-preview","--data-dir=\0"]).StartsWith(Path.Combine(Path.GetTempPath(),"GlassDesk.UiPreview"),StringComparison.OrdinalIgnoreCase),
            "malformed preview parameters never select the formal error directory");
        foreach(string invalid in new[]{" D:\\invalid",Path.Combine(root,"bad:component"),Path.Combine(root,"bad?component")})
        {
            bool rejected=false;
            try{LaunchOptions.Parse(["--ui-preview","--data-dir="+invalid]);}catch(ArgumentException){rejected=true;}
            check(rejected&&LaunchOptions.EarlyErrorDirectory(["--ui-preview","--data-dir="+invalid])==LaunchOptions.PreviewErrorFallback,
                "invalid preview directory is rejected and its error stays in the preview fallback");
        }
        check(new CompanionVisualState(CompanionExpression.Calm,0xff123456,0,0,1,1).AppearanceId==CompanionAppearanceIds.Original&&PluginApi.Version==1,
            "legacy companion constructor and plugin API version remain compatible");
    }
}

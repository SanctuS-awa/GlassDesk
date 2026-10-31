using GlassDesk.Core;

static class TypographyPageChecks
{
    public static void Run(Action<bool,string> check)
    {
        var oldSettings=System.Text.Json.JsonSerializer.Deserialize<WidgetPreferences>("{\"ClockShowTime\":true}")!.Validated();
        check(oldSettings.ClockFontId==ClockFontCatalog.System, "existing settings acquire the system clock face without resetting other values");
        check(new WidgetPreferences { ClockFontId="unknown",Blur=13 }.Validated() is {ClockFontId:"system",Blur:13},
            "invalid clock font IDs fall back without resetting material");
        check(ClockFontCatalog.All.All(face=>System.Text.Json.JsonSerializer.Deserialize<WidgetPreferences>(
            System.Text.Json.JsonSerializer.Serialize(new WidgetPreferences {ClockFontId=face.Id}))!.Validated().ClockFontId==face.Id),
            "all four clock font choices survive save and reload");
        check(ClockFontCatalog.IsClockRole(TextRole.ClockWhiteplate)&&!ClockFontCatalog.IsClockRole(TextRole.MediaPeekTitle)
            &&!ClockFontCatalog.IsClockRole(TextRole.ClockLegacyAux), "clock fonts exclude song titles, dates and auxiliary labels");
        var title=TypographyCatalog.Get(TextRole.MediaDetailsTitle);
        check(title is {FontSizeDip:20,LineHeightDip:28,LatinWeight:600,CjkWeight:500}
            && TypographyCatalog.Get(TextRole.ClockWhiteplate) is {FontSizeDip:16,LineHeightDip:26},
            "preview2 shared roles preserve the whiteplate slot and distinguish Details title hierarchy");
        check(FontResolutionPolicy.Tracking(title,"GlassDesk")<0
            && FontResolutionPolicy.Tracking(title,"天球(そら)のMúsica")==0
            && FontResolutionPolicy.Tracking(TypographyCatalog.Get(TextRole.WidgetClock),"08:08")==0,
            "negative tracking is confined to pure Latin headings; mixed scripts and numeric fields stay natural");
        check(TypographyCatalog.All.Values.All(t=>t.LineHeightDip>=t.FontSizeDip&&t.CjkWeight is 400 or 500)
            && Enum.GetValues<InkRole>().All(r=>TypographyCatalog.Ink(r)>>24==255),
            "typography roles retain finite line boxes and four opaque semantic inks");
        var white=WidgetReadabilityPolicy.Resolve([0x00f9f8f7,0x00f9f8f7,0x00f9f8f7,0x00f9f8f7],.24f,true,true);
        check(!white.LightInk&&!white.ContrastUncertain&&!white.PromoteSecondary,
            "light material keeps dark primary and secondary ink");
        var darkPlate=WidgetReadabilityPolicy.Resolve([0x002c271e,0x002c271e,0x002c271e,0x002c271e],.24f,true,false);
        var darkBody=WidgetReadabilityPolicy.Resolve([0x002c271e,0x002c271e,0x002c271e,0x002c271e],.24f,false,false);
        check(darkPlate.ContrastUncertain&&darkPlate.PromoteSecondary&&darkBody.LightInk&&!darkBody.ContrastUncertain,
            "a dark whiteplate reports uncertain contrast and promotes ink without requesting opaque material");
        var gray=WidgetReadabilityPolicy.Resolve([0x008a8480,0x008a8480,0x008a8480,0x008a8480],.24f,false,true);
        check(!gray.LightInk&&gray.PromoteSecondary&&!gray.ContrastUncertain,
            "gray material promotes required secondary text without changing transparency");
        var complex=WidgetReadabilityPolicy.Resolve([0x00000000,0x00ffffff,0x00000000,0x00ffffff],.24f,false,false);
        check(complex.ContrastUncertain&&complex.PromoteSecondary,
            "a spanning material range reports uncertainty rather than claiming guaranteed contrast");
        var missing=WidgetReadabilityPolicy.Resolve([], .24f,false,true);
        check(missing.ContrastUncertain&&missing.LightInk&&missing.PromoteSecondary
            &&WidgetReadabilityPolicy.Resolve([0u,0u,uint.MaxValue,0u],.24f,false,false).ContrastUncertain
            &&WidgetReadabilityPolicy.Resolve([0u,0u,0u,0u],float.NaN,false,false).ContrastUncertain,
            "missing, failed and nonfinite samples preserve the previous ink family and do not request a material override");
        uint[] separateSurfaces=[0x003c3c3c,0x003c3c3c,0x003c3c3c,0x003c3c3c];
        var bodyInk=WidgetReadabilityPolicy.Resolve(separateSurfaces,.24f,false,false);
        var plateInk=WidgetReadabilityPolicy.Resolve(separateSurfaces,.24f,true,false);
        check(bodyInk.LightInk&&!plateInk.LightInk&&plateInk.ContrastUncertain,
            "body and whiteplate choose independent ink families without claiming uncertain gray contrast passes");
        var largeClock=WidgetReadabilityPolicy.Resolve(separateSurfaces,.24f,true,false,3,false);
        var clockAndDate=WidgetReadabilityPolicy.Resolve(separateSurfaces,.24f,true,false,3,true);
        check(!largeClock.ContrastUncertain&&largeClock.PrimaryContrastEstimate>=3&&clockAndDate.ContrastUncertain,
            "a large clock can pass 3:1 while required small date text still retains the 4.5:1 gate");
        check(WidgetReadabilityPolicy.Resolve(separateSurfaces,.24f,true,false,double.NaN,false).ContrastUncertain,
            "unsupported contrast thresholds cannot accidentally certify text");
        foreach(int version in new[]{1,2,3})
        {
            var old=new WidgetPreferences{Version=version,AnchorX=.31,CompanionColor=0xff123456,LastDetailsPageId="widgets"};
            var migrated=old.Validated();
            check(migrated is {Version:4,LastDetailsPageId:"music",AnchorX:.5,CompanionColor:0xff123456},
                $"v{version} migration retains appearance and normalizes the fixed position");
        }
        var invalid=new WidgetPreferences{LastDetailsPageId="unknown",Blur=13}.Validated();
        check(invalid is {LastDetailsPageId:"music",Blur:13},"an unknown real-page ID falls back without resetting other settings");
        var controller=new DetailsPageController(new());var stale=new WidgetPreferences();
        controller.Select("widgets");long oldGeneration=controller.Generation;controller.SetScrollOffset(70);
        check(!controller.SelectPreviewItem("clock")&&controller.CurrentPageId=="widgets"
            &&controller.ApplyTo(stale).LastDetailsPageId=="widgets",
            "preview selection and stale settings saves cannot reset the real widget page");
        controller.SelectPreviewItem("music");controller.SetScrollOffset(12);controller.Select("widgets");
        controller.MarkSaved(oldGeneration);
        check(controller.ScrollOffset==70&&!controller.IsCurrent(oldGeneration,"widgets")&&controller.SavePending,
            "per-page scroll persists in process and late asynchronous save acknowledgements cannot clear newer state");
        var reopened=new DetailsPageController(controller.ApplyTo(stale));
        check(reopened.CurrentPageId=="widgets"&&reopened.ScrollOffset==0,
            "restart restores only the real page ID and starts its scroll at the top");
        var host = new WidgetController(new(),new("qa",new(0,0,1920,1080),new(0,0,1920,1040),1,true),new(960,24));
        host.SetDetailsPageHeight(304,0);host.ChangeForm(WidgetForm.Details,1);
        host.UpdateContent(CapsuleContentMode.Music,true,true,256,2,true);
        bool independent = host.Layout.Height==304;
        host.UpdateContent(CapsuleContentMode.Clock,false,true,256,3);
        independent &= host.Layout.Height==304;
        host.ChangeForm(WidgetForm.Capsule,4);
        independent &= host.Layout.Height==52;
        host.ChangeForm(WidgetForm.Details,5);host.SetDetailsPageHeight(null,6);
        check(independent && host.Layout.Height==300,
            "widget page height survives music connection changes while capsule and restored music keep their own layouts");
    }
}

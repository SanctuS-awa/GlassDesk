using System.Text.Json;
using GlassDesk.Core;
using GlassDesk.Platform;

// These checks are isolated from the real profile and never start/control music,
// register a hotkey, create a tray icon or change any Windows preference.
var root = Path.GetFullPath(Path.Combine("artifacts", "platform-checks", Guid.NewGuid().ToString("N")));
Directory.CreateDirectory(root);
var checks = new List<string>();
void Check(bool condition, string name)
{
    if (!condition)
    {
        Console.Error.WriteLine("FAIL: " + name);
        Environment.Exit(1);
    }
    checks.Add(name);
}

using (var store = new PreferenceStore(Path.Combine(root, "roundtrip")))
{
    var defaults = store.Load();
    Check(!defaults.RunAtStartup && defaults.HotkeyKey == 0 && defaults.HoverDelayMs == 350, "fresh defaults preserve opt-in startup/hotkey");
    var preferences = defaults with { HoverDelayMs = 640, LeaveDelayMs = 700, AnchorX = .8, AnchorY = 48, MonitorId = "stable-monitor", ReducedMotion = true };
    await store.SaveAsync(preferences);
    var roundTrip = store.Load();
    Check(roundTrip == PreferenceStore.Normalize(preferences), "preferences survive atomic round-trip");
    Check(roundTrip.HoverDelayMs == 640 && roundTrip.LeaveDelayMs == 700 && roundTrip.ReducedMotion,
        "persisted timer and reduced-motion preferences survive normalization");
    await Task.WhenAll(Enumerable.Range(0, 10).Select(index => store.SaveAsync(preferences with { HoverDelayMs = 100 + index })));
    Check(store.Load().HoverDelayMs is >= 100 and <= 109, "concurrent saves produce complete readable JSON");
    Check(Directory.GetFiles(store.DirectoryPath, "*.tmp").Length == 0, "atomic writes leave no staging files");
    using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
    var old = File.ReadAllText(store.SettingsPath);
    try { await store.SaveAsync(defaults, cancelled.Token); throw new InvalidOperationException("Cancellation did not occur."); }
    catch (OperationCanceledException) { }
    Check(File.ReadAllText(store.SettingsPath) == old, "cancelled save preserves the preceding complete config");
    File.WriteAllText(store.SettingsPath, "{broken");
    Check(store.Load() == new WidgetPreferences(), "corrupt config restores defaults");
    var backup = Directory.GetFiles(store.DirectoryPath, "settings.corrupt-*.json");
    Check(backup.Length == 1 && File.ReadAllText(backup[0]) == "{broken", "corrupt evidence is preserved exactly");
    Check(store.TakeLoadNotice() is not null && store.TakeLoadNotice() is null, "corruption notice is consumed once");
    await store.SaveAsync(defaults);
    var unsupported = JsonSerializer.Serialize(defaults with { Version = 9 });
    File.WriteAllText(store.SettingsPath, unsupported);
    Check(store.Load() == defaults && Directory.GetFiles(store.DirectoryPath, "settings.corrupt-*.json").Length == 2,
        "unsupported version is retained before fallback");
}

var normalized = PreferenceStore.Normalize(new() { Blur = float.NaN, TintOpacity = float.PositiveInfinity, HoverDelayMs = -1,
    LeaveDelayMs = 8000, AnchorX = double.NaN, AnchorY = double.NegativeInfinity, HotkeyModifiers = 0xffff, HotkeyKey = 999 });
Check(normalized.Blur == 20 && normalized.TintOpacity == .24f && normalized.HoverDelayMs == 100 && normalized.LeaveDelayMs == 1500,
    "numeric preferences cannot introduce invalid material or timer values");
Check(normalized.AnchorX == .5 && normalized.AnchorY == 0 && normalized.HotkeyModifiers == 15 && normalized.HotkeyKey == 0,
    "invalid position and hotkey values are sanitized");

var primary = new MonitorDescriptor("primary", new(0, 0, 2560, 1600), new(0, 0, 2560, 1528), 1.5f, true);
var secondary = new MonitorDescriptor("secondary", new(-1920, 0, 1920, 1080), new(-1920, 0, 1920, 1040), 1, false);
Check(MonitorService.SelectMonitor([primary, secondary], "secondary") == secondary, "stable monitor identity retains selection");
Check(MonitorService.SelectMonitor([primary], "disconnected") == primary, "disconnected monitor migrates to primary");
Check(MonitorService.RestoreAnchor(primary, new()) == (1130, 0), "default anchor is horizontally centered at the top of the work area");
var edge = MonitorService.RestoreAnchor(secondary, new() { AnchorX = 1, AnchorY = 5000 });
Check(edge == (-200, 988), "restored anchor stays inside a negative-origin work area");
Check(MonitorService.SelectMonitor([], null) is null, "empty monitor set fails safely");

Check(ForegroundObserver.IsFullscreen(new(0, 0, 2560, 1600), primary.Bounds, primary.WorkArea, false, false), "borderless monitor-filling app is full-screen");
Check(!ForegroundObserver.IsFullscreen(primary.WorkArea, primary.Bounds, primary.WorkArea, true, true), "ordinary maximized app remains visible");
Check(!ForegroundObserver.IsFullscreen(primary.Bounds, primary.Bounds, primary.Bounds, true, true), "auto-hidden taskbar does not turn maximized apps into full-screen");
Check(!ForegroundObserver.IsFullscreen(new(0, 0, 1800, 1000), primary.Bounds, primary.WorkArea, false, false), "partial monitor window is not full-screen");

var diagnostics = new Diagnostics();
diagnostics.RecordCapability(true, new(true, true, true, false));
for (var index = 0; index < 200; index++) diagnostics.RecordEvent("system", "resume");
diagnostics.RecordError("media.refresh", new InvalidOperationException("private song/account content must never appear"));
var report = Path.Combine(root, "diagnostics.json");
await diagnostics.ExportAsync(report);
var text = File.ReadAllText(report);
using (var document = JsonDocument.Parse(text))
{
    Check(document.RootElement.GetProperty("Entries").GetArrayLength() == 128, "diagnostic ring has a bounded resident footprint");
    Check(document.RootElement.GetProperty("Entries")[127].GetProperty("ErrorType").GetString() == nameof(InvalidOperationException),
        "diagnostic export retains error type without its private message");
    Check(!text.Contains("private song/account") && !text.Contains("Title") && !text.Contains("Artist"), "diagnostic export contains no song/account fields or exception messages");
}

UiPreviewChecks.Run(Check,root);
using(var protectedStore=new PreferenceStore(Path.Combine(root,"read-failure")))
{
    await protectedStore.SaveAsync(new WidgetPreferences {HoverDelayMs=850});
    byte[] original=await File.ReadAllBytesAsync(protectedStore.SettingsPath);
    using(var locked=new FileStream(protectedStore.SettingsPath,FileMode.Open,FileAccess.Read,FileShare.None))protectedStore.Load();
    bool rejected=false;try{await protectedStore.SaveAsync(new());}catch(IOException){rejected=true;}
    Check(rejected&&!protectedStore.CanSave&&(await File.ReadAllBytesAsync(protectedStore.SettingsPath)).SequenceEqual(original),
        "temporary preferences cannot overwrite unreadable/unbacked original settings after unlock");
    Check(protectedStore.Load().HoverDelayMs==850&&protectedStore.CanSave,"successful reload restores original preferences and writable state");
}
await HomeDirectoryChecks.RunAsync(Check);
await WidgetStoreChecks.RunAsync(Check);
Check(WidgetTextToneProbe.TryChooseLight([0u,0u,0u,0u],false,out var darkBackground)&&darkBackground,
    "solid light text is selected for a dark background without metadata panels");
Check(WidgetTextToneProbe.TryChooseLight([0x00ffffffu,0x00ffffffu,0x00ffffffu,0x00ffffffu],true,out var lightBackground)&&!lightBackground,
    "solid dark text is selected for a light background");
Check(!WidgetTextToneProbe.TryChooseLight([uint.MaxValue],true,out var unavailable)&&unavailable,
    "unavailable screen pixels preserve the previous text tone");
WidgetTextToneProbe.TryChooseLight([0x0000ff00u],false,out var green);
WidgetTextToneProbe.TryChooseLight([0x00ff0000u],false,out var blue);
Check(!green&&blue,"screen COLORREF channels use perceptual luminance instead of ARGB byte order");
WidgetTextToneProbe.TryChooseLight([0x007b7b7bu],false,out var grayFromDark);
WidgetTextToneProbe.TryChooseLight([0x007b7b7bu],true,out var grayFromLight);
Check(!grayFromDark&&grayFromLight,"near-threshold backgrounds preserve either prior ink through hysteresis");
var result = JsonSerializer.Serialize(new { Passed = checks.Count, Checks = checks, ArtifactDirectory = root }, new JsonSerializerOptions { WriteIndented = true });
File.WriteAllText(Path.Combine(root, "checks-result.json"), result);
Console.WriteLine(result);

namespace GlassDesk.Core;

public enum WidgetForm { Capsule, Peek, Details }
public enum PlaybackState { None, Closed, Stopped, Paused, Playing, Changing }
public enum MediaCommand { Previous, TogglePlayPause, Next, Seek }
public sealed record MediaCapabilities(bool Previous=false, bool Toggle=false, bool Next=false, bool Seek=false);
public sealed record MediaTimeline(TimeSpan Start, TimeSpan End, TimeSpan Position, TimeSpan MinSeek, TimeSpan MaxSeek, long ReceivedTimestamp)
{
    public bool Valid => End>Start && Position>=Start && Position<=End;
}
public sealed record MediaSnapshot(string SessionToken, long ContentVersion, bool Connected, string Title, string Artist, string Album,
    byte[]? Cover, PlaybackState State, double? Rate, MediaTimeline? Timeline, MediaCapabilities Capabilities, string? Notice=null)
{
    public static MediaSnapshot Disconnected {get;}=new("",0,false,"","","",null,PlaybackState.None,null,null,new());
}
public sealed record CommandResult(bool Accepted, bool Verified, string? Error=null);
public interface IMediaAdapter : IDisposable
{
    event Action<MediaSnapshot>? SnapshotChanged;
    MediaSnapshot Current {get;}
    Task StartAsync(CancellationToken cancellationToken=default);
    Task<CommandResult> ExecuteAsync(MediaCommand command, string sessionToken, TimeSpan? position=null, CancellationToken cancellationToken=default);
}
public interface IUiDispatcher
{
    bool TryEnqueue(Action action);
    Task<T> InvokeAsync<T>(Func<Task<T>> action);
}
public enum HostEventKind { Enter=1, Leave=2, Move=3, Down=4, Up=5, Wheel=6, Invoke=7, RangeValue=8, DpiChanged=9, DisplayChanged=10, Resume=11, SystemSettingsChanged=12, OpenSettings=13, Quit=14, CaptureLost=15 }
public readonly record struct HostPointerEvent(HostEventKind Kind, float X, float Y, int ScreenX, int ScreenY, int ControlId=0, double Value=0);
public readonly record struct HostContext(int X, int Y, float Width, float Height, uint Dpi);
public readonly record struct HostFrame(float Width, float Height, float Radius, float Blur=20, float TintOpacity=.24f, bool Solid=false)
{
    public float SurfaceTension {get;init;}
}
public sealed record AccessibleControl(int Id, string Name, float X, float Y, float Width, float Height, bool Enabled=true,
    bool IsRange=false, double Value=0, double Minimum=0, double Maximum=100);
public interface IWidgetHost : IDisposable
{
    event Action<HostPointerEvent>? PointerEvent;
    event Action<HostContext>? WindowContextChanged;
    HostContext Context {get;}
    nint Handle {get;}
    void ApplyFrame(HostFrame frame);
    void SetPlacement(int screenX,int screenY);
    void SetVisible(bool visible);
    void SetAccessibleControls(IReadOnlyList<AccessibleControl> controls, string name);
}
public enum WidgetTextInk { Black=0, White=1 }
public sealed record WidgetPreferences
{
    public int Version {get;init;}=4;
    public string LastDetailsPageId {get;init;}=DetailsPageController.Music;
    public float Blur {get;init;}=20;
    public float TintOpacity {get;init;}=.24f;
    public bool ClockShowTime {get;init;}=true;
    public bool ClockShowDate {get;init;}
    public bool ClockShowWeekday {get;init;}
    public string ClockFontId {get;init;}=ClockFontCatalog.System;
    public WidgetTextInk TextInk {get;init;}=WidgetTextInk.Black;
    public bool ReducedMotion {get;init;}
    public int HoverDelayMs {get;init;}=350;
    public int LeaveDelayMs {get;init;}=450;
    public bool AvoidFullscreen {get;init;}=true;
    public bool RunAtStartup {get;init;}
    public uint HotkeyModifiers {get;init;}
    public uint HotkeyKey {get;init;}
    public string? MonitorId {get;init;}
    public double AnchorX {get;init;}=.5;
    public double AnchorY {get;init;}=0;
    public bool OnboardingSeen {get;init;}
    public bool AudioVisualizationEnabled {get;init;}=true;
    public bool CompanionEnabled {get;init;}=true;
    public bool CompanionFollowMouse {get;init;}=true;
    public uint CompanionColor {get;init;}=0xff202329;
    public string CompanionAppearanceId {get;init;}=CompanionAppearanceIds.Original;
    public bool LiquidGlassEnabled {get;init;}
    public PluginEnableMap PluginEnabled {get;init;}=new();
    public WidgetPreferences Validated()=>this with {Version=4,LastDetailsPageId=Version<4?DetailsPageController.Music:DetailsPageController.Normalize(LastDetailsPageId),Blur=Math.Clamp(Blur,0,40),TintOpacity=Math.Clamp(TintOpacity,.10f,.90f),HoverDelayMs=Math.Clamp(HoverDelayMs,100,1500),LeaveDelayMs=Math.Clamp(LeaveDelayMs,100,1500),MonitorId=null,AnchorX=.5,AnchorY=0,CompanionColor=CompanionColor|0xff000000,CompanionAppearanceId=CompanionAppearanceIds.Normalize(CompanionAppearanceId),ClockFontId=ClockFontCatalog.Normalize(ClockFontId),TextInk=TextInk is WidgetTextInk.Black or WidgetTextInk.White?TextInk:WidgetTextInk.Black,PluginEnabled=PluginEnabled is null?new():new(PluginEnabled)};
}
public readonly record struct ScreenRect(int X,int Y,int Width,int Height)
{
    public int Right=>X+Width; public int Bottom=>Y+Height;
}
public sealed record MonitorDescriptor(string Id, ScreenRect Bounds, ScreenRect WorkArea, float Scale, bool Primary);
public readonly record struct SystemScene(nint ForegroundWindow, bool Fullscreen, bool Maximized);
public static class WidgetActions
{
    public const int Body=0,Previous=1,Toggle=2,Next=3,Close=4,Seek=5,OpenMusic=6,Settings=7,PluginTools=8;
    public const int NavigationShell=99,NavigationFirst=100,CompanionReveal=110;
    // Details dock destinations (append only): 120..124 = music/widgets/clock/focus/notes.
    public const int DockFirst=120;
    // Home page actions (append only): external shortcuts, navigation entries, folder dock.
    public const int HomeAppFirst=300, HomeAppCount=2;
    public const int HomeNavToggle=320, HomeNavFirst=321, HomeNavCount=5;
    public const int HomeFolderFirst=330, HomeFolderCount=1;
    public const int HomePreviewFirst=340, HomePreviewCount=3;
    public const int HomeOpenFolder=350, HomeClosePopover=351, HomePopupBody=352, HomeBack=353;
}

namespace GlassDesk.Core;

/// <summary>The real page is independent of the five-slot visual preview selection.</summary>
public sealed class DetailsPageController
{
    public const string Home="home", Music="music", Widgets="widgets";
    readonly Dictionary<string,float> _scroll=new(StringComparer.Ordinal) { [Home]=0,[Music]=0,[Widgets]=0 };
    public string CurrentPageId {get;private set;}
    public long Generation {get;private set;}
    public bool SavePending {get;private set;}
    public event Action<string>? PageChanged;
    public DetailsPageController(WidgetPreferences preferences)
        =>CurrentPageId=preferences.Version<4?Home:Normalize(preferences.LastDetailsPageId);
    public static string Normalize(string? id)=>id is Home or Widgets? id! : Home;
    public bool Select(string? id)
    {
        string next=Normalize(id);
        if(next==CurrentPageId)return false;
        CurrentPageId=next;Generation++;SavePending=true;PageChanged?.Invoke(next);return true;
    }
    public bool SelectPreviewItem(string id)=>id==Home && Select(Home);
    public float ScrollOffset=>_scroll[CurrentPageId];
    public void SetScrollOffset(float offset)=>_scroll[CurrentPageId]=float.IsFinite(offset)?Math.Max(0,offset):0;
    public bool IsCurrent(long generation,string pageId)=>generation==Generation&&pageId==CurrentPageId;
    // Apply before *every* settings save; stale setting snapshots cannot restore an old page.
    public WidgetPreferences ApplyTo(WidgetPreferences snapshot)=>snapshot with {Version=4,LastDetailsPageId=CurrentPageId};
    public void MarkSaved(long generation)
    {
        if(generation==Generation)SavePending=false;
    }
}

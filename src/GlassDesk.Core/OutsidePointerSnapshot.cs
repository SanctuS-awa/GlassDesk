namespace GlassDesk.Core;

public enum OutsidePointerButton { Left, Right, Middle }
public readonly record struct OutsidePointerPress(long OpenSequence,long GeometryVersion,int ScreenX,int ScreenY,
    OutsidePointerButton Button,bool Injected,bool OutsideRegisteredRegion=true,bool OutsideDismissRegion=false);

/// <summary>Immutable event-time geometry in physical pixels; no UI, capture or file work on the hook thread.</summary>
public sealed class OutsidePointerSnapshot
{
    public long OpenSequence {get;}
    public long GeometryVersion {get;}
    public FloatRect PhysicalBounds {get;}
    public float Radius {get;}
    public PointerCaptureOwner CaptureOwner {get;}
    readonly IReadOnlyList<FloatRect> popups;
    readonly FloatRect? dismissBounds;
    readonly float dismissRadius;
    bool Valid=>PhysicalBounds.Width>0&&PhysicalBounds.Height>0&&float.IsFinite(PhysicalBounds.X)&&float.IsFinite(PhysicalBounds.Y)&&
        float.IsFinite(PhysicalBounds.Width)&&float.IsFinite(PhysicalBounds.Height)&&float.IsFinite(Radius);
    public OutsidePointerSnapshot(long sequence,long version,FloatRect physicalBounds,float radius,
        PointerCaptureOwner captureOwner=PointerCaptureOwner.None,IEnumerable<FloatRect>? ownedPopupRegions=null,
        FloatRect? dismissBounds=null,float dismissRadius=0)
    {
        OpenSequence=sequence;GeometryVersion=version;PhysicalBounds=physicalBounds;Radius=radius;CaptureOwner=captureOwner;
        popups=Array.AsReadOnly(ownedPopupRegions?.ToArray()??[]);
        this.dismissBounds=dismissBounds;
        this.dismissRadius=float.IsFinite(dismissRadius)?Math.Max(0,dismissRadius):0;
    }
    public bool Contains(float x,float y)
    {
        if(!Valid||!float.IsFinite(x)||!float.IsFinite(y))return false;
        foreach(var popup in popups)if(popup.Contains(x,y))return true;
        var b=PhysicalBounds;if(!b.Contains(x,y))return false;
        float radius=Math.Clamp(Radius,0,Math.Min(b.Width,b.Height)*.5f);
        float cx=Math.Clamp(x,b.X+radius,b.Right-radius),cy=Math.Clamp(y,b.Y+radius,b.Bottom-radius);
        float dx=x-cx,dy=y-cy;return dx*dx+dy*dy<=radius*radius;
    }
    public bool IsOutsideRegisteredRegion(int x,int y)=>Valid&&!Contains(x,y);
    public bool IsOutsideDismissRegion(int x,int y)
    {
        if(!Valid||dismissBounds is not { } bounds||!bounds.Contains(x,y))return false;
        float radius=Math.Clamp(dismissRadius,0,Math.Min(bounds.Width,bounds.Height)*.5f);
        float cx=Math.Clamp(x,bounds.X+radius,bounds.Right-radius),cy=Math.Clamp(y,bounds.Y+radius,bounds.Bottom-radius);
        float dx=x-cx,dy=y-cy;
        return dx*dx+dy*dy>radius*radius;
    }
    public bool IsOutsidePress(int x,int y)=>Valid&&CaptureOwner==PointerCaptureOwner.None
        &&(IsOutsideRegisteredRegion(x,y)||IsOutsideDismissRegion(x,y));
    public static bool BelongsToVisibleDetails(OutsidePointerPress press,long openSequence,bool visible,WidgetForm form)
        =>visible&&form==WidgetForm.Details&&press.OpenSequence==openSequence;
}

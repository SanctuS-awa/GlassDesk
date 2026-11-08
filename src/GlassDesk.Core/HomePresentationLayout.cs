namespace GlassDesk.Core;

/// <summary>
/// Canonical Home geometry. Width/Height describe the visible host viewport; dashboard
/// content uses a fixed 960x512 or 820x456 canvas and is clipped while it scrolls.
/// </summary>
public sealed record HomePresentationLayout(float Width, float Height, float ScrollX = 0, float ScrollY = 0)
{
    public bool Standard => Width >= 960 && Height >= 512;
    public bool Compact => !Standard && Width >= 820 && Height >= 456;
    public bool Viewported => !Standard && !Compact;
    public float CanvasWidth => Standard ? 960 : 820;
    public float CanvasHeight => Standard ? 512 : 456;
    public float Scroll => Math.Max(ScrollX, ScrollY);
    public bool DashboardCardsVisible => true;
    public bool WeatherVisible => true;
    public bool CompactCalendar => false;
    public bool CompactMedia => !Standard;
    public bool MediaActionsVisible => true;
    public bool CanShowFolderPopover => Width >= 480 && Height >= 320;

    public FloatRect Weather => Standard
        ? new(28, 130, 176, 224)
        : new(20, 102, 148, 204);
    public FloatRect Calendar => Standard
        ? new(220, 116, 256, 292)
        : new(184, 92, 224, 268);
    public FloatRect Media => Standard
        ? new(528, 104, 280, 316)
        : new(448, 80, 248, 292);

    /// <summary>iPhone 式音量细条：只画这一根，贴面板右边缘。</summary>
    public FloatRect VolumeBar
    {
        get
        {
            float height = Standard ? 168 : Compact ? 152 : Math.Clamp(Height - 140, 72, 168);
            float y = Math.Max(56, (Height - height) / 2);
            return new(Math.Max(0, Width - 16), y, 6, height);   // 右内缩 10 DIP，宽 6 DIP
        }
    }

    /// <summary>命中区比视觉宽：40 DIP 宽，上下各多 12 DIP，右边缘到面板边。</summary>
    public FloatRect Volume => new(Math.Max(0, Width - 40), VolumeBar.Y - 12, Math.Min(40, Width), VolumeBar.Height + 24);
    /// <summary>拖动映射区 = 细条本身（顶端 100%，底端 0%）。</summary>
    public FloatRect VolumeTrack => VolumeBar;

    public FloatRect Clock => new(20, 12, 72, 24);
    public FloatRect ClockDate => new(Standard ? 32 : 24, Standard ? 364 : 308, 160, 44);
    public FloatRect StatusBattery => new(Math.Max(0, Width - 47.5f), 18, 27.5f, 12);
    public FloatRect StatusWifi => new(Math.Max(0, Width - 73.5f), 18, 18, 12);
    /// <summary>台式机无电池时 Wi‑Fi 右移到电池位置。</summary>
    public FloatRect StatusWifiNoBattery => new(Math.Max(0, Width - 38), 18, 18, 12);

    public FloatRect Dock
    {
        get
        {
            float x = Standard ? 354 : Compact ? 284
                : Math.Max(0, Math.Min((Width - 252) / 2, Volume.X - 260));
            float y = Standard ? 444 : Compact ? 392 : Math.Max(0, Height - 68);
            return new(x, y, 252, 44);
        }
    }
    public FloatRect DockPill => new(Dock.X, Dock.Y, 196, 44);
    public FloatRect DockNavigation => new(Dock.X + 208, Dock.Y, 44, 44);
    public FloatRect NavTrigger => DockNavigation;
    public FloatRect DockFolder => new(Dock.X + 14, Dock.Y, 44, 44);

    /// <summary>44x44 interactive slot. The application artwork itself is 32x32.</summary>
    public FloatRect DockAppSlot(int index)
    {
        if (index is < 0 or > 1) throw new ArgumentOutOfRangeException(nameof(index));
        return new(Dock.X + 76 + index * 62, Dock.Y, 44, 44);
    }
    public FloatRect DockAppIcon(int index)
    {
        var slot = DockAppSlot(index);
        return new(slot.X + 6, slot.Y + 6, 32, 32);
    }

    public FloatRect ContentViewport
    {
        get
        {
            float right = Math.Max(0, Math.Min(Width, Volume.X - 12));
            float bottom = Math.Max(54, Math.Min(Height, Dock.Y - 12));
            return new(0, 54, right, Math.Max(0, bottom - 54));
        }
    }
    public float MaxScrollX => Viewported ? Math.Max(0, CanvasWidth - ContentViewport.Width) : 0;
    public float MaxScrollY => Viewported
        ? Math.Max(0, Math.Min(CanvasHeight, Standard ? 420 : 372) - (ContentViewport.Y + ContentViewport.Height))
        : 0;

    public static HomePresentationLayout Resolve(float width, float height, float scrollX = 0, float scrollY = 0)
    {
        float visibleWidth = float.IsFinite(width) ? Math.Clamp(width, 64, HomeLayout.DashboardWidth) : HomeLayout.DashboardWidth;
        float visibleHeight = float.IsFinite(height) ? Math.Clamp(height, 160, HomeLayout.Height) : HomeLayout.Height;
        var layout = new HomePresentationLayout(visibleWidth, visibleHeight);
        float x = float.IsFinite(scrollX) ? Math.Clamp(scrollX, 0, layout.MaxScrollX) : 0;
        float y = float.IsFinite(scrollY) ? Math.Clamp(scrollY, 0, layout.MaxScrollY) : 0;
        return layout with { ScrollX = x, ScrollY = y };
    }

    public FloatRect PresentedContent(FloatRect rect) => rect with
    {
        X = rect.X - ScrollX,
        Y = rect.Y - ScrollY
    };

    /// <summary>Only expose a content action when its full visible target is inside the shared clip.</summary>
    public FloatRect? VisibleContentTarget(FloatRect rect)
    {
        var presented = PresentedContent(rect);
        var clip = ContentViewport;
        return presented.X >= clip.X && presented.Y >= clip.Y
            && presented.Right <= clip.Right && presented.Bottom <= clip.Bottom
            ? presented : null;
    }

    /// <summary>Static foreground targets are clipped against the visible host window.</summary>
    public FloatRect? VisibleOverlayTarget(FloatRect rect)
    {
        if (rect.X < 0 || rect.Y < 0 || rect.Right > Width || rect.Bottom > Height) return null;
        return rect;
    }

    public FloatRect MediaCover => Standard
        ? new(Media.X + 76, Media.Y + 36, 128, 128)
        : new(Media.X + 72, Media.Y + 32, 104, 104);
    public FloatRect MediaTitle => Standard
        ? new(Media.X + 16, Media.Y + 172, 248, 22)
        : new(Media.X + 16, Media.Y + 143, 216, 20);
    public FloatRect MediaArtist => Standard
        ? new(Media.X + 16, Media.Y + 198, 248, 18)
        : new(Media.X + 16, Media.Y + 165, 216, 18);
    public FloatRect MediaTrack => Standard
        ? new(Media.X + 24, Media.Y + 226, 232, 3)
        : new(Media.X + 20, Media.Y + 194, 208, 3);
    public FloatRect MediaElapsed => Standard
        ? new(Media.X + 24, Media.Y + 233, 70, 14)
        : new(Media.X + 20, Media.Y + 201, 64, 14);
    public FloatRect MediaDuration => Standard
        ? new(Media.X + 186, Media.Y + 233, 70, 14)
        : new(Media.X + 164, Media.Y + 201, 64, 14);
    public MediaControlsRow MediaControls => MediaControlsRowLayout.Resolve(
        Standard ? Media.X + 140 : Media.X + 124,
        Standard ? Media.Y + 282 : Media.Y + 258,
        44, 26);
    public FloatRect MediaPrevious => MediaControls.Previous;
    public FloatRect MediaToggle => MediaControls.Toggle;
    public FloatRect MediaNext => MediaControls.Next;
    public FloatRect MediaOpenMusic => new(Media.X + (Media.Width - 160) / 2,
        Media.Y + (Standard ? 236 : 212), 160, 36);
    public FloatRect CalendarPrevious => new(Calendar.Right - 84, Calendar.Y + 8, 32, 36);
    public FloatRect CalendarNext => new(Calendar.Right - 48, Calendar.Y + 8, 32, 36);

    // Retained for callers that used the original grid during transition animations.
    public FloatRect NavRail => new(DockNavigation.X, 54, 44, Math.Max(0, Dock.Y - 62));
    public FloatRect NavRailItem(int index)
    {
        if (index is < 0 or > 4) throw new ArgumentOutOfRangeException(nameof(index));
        float step = NavRail.Height / 5;
        float size = Math.Min(40, Math.Max(0, step - 4));
        return new(NavRail.X + (NavRail.Width - size) / 2, NavRail.Y + index * step + (step - size) / 2, size, size);
    }

    public FloatRect Popover => new(Math.Max(0, (Width - Math.Min(312, Width - 48)) / 2),
        Math.Max(54, Dock.Y - 220), Math.Max(16, Math.Min(312, Width - 48)), Math.Max(1, Math.Min(208, Dock.Y - 64)));
    public FloatRect PopoverTitle => new(Popover.X + 12, Popover.Y + 12, Math.Max(1, Popover.Width - 24), 20);
    public FloatRect PopoverOpenButton => new(Popover.X + 12, Popover.Bottom - 60, Math.Max(1, Popover.Width - 24), 44);
    public FloatRect Preview(int index)
    {
        if (index is < 0 or > 2) throw new ArgumentOutOfRangeException(nameof(index));
        float width = Math.Max(1, (Popover.Width - 40) / 3);
        return new(Popover.X + 12 + index * (width + 8), Popover.Y + 40, width,
            Math.Max(1, PopoverOpenButton.Y - Popover.Y - 48));
    }
}

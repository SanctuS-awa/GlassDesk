namespace GlassDesk.Core;

/// <summary>Home geometry for the fixed Dock, folder popover, and navigation trigger.</summary>
public sealed record HomePresentationLayout(float Width, float Height, float Scroll)
{
    public bool Compact => Height < 460;
    public float RowHeight => Compact ? 84 : HomeLayout.CellHeight;
    public float RowGap => Compact ? 8 : HomeLayout.RowGap;
    public float CellWidth => (Width - HomeLayout.Inset * 2 - HomeLayout.ColumnGap * 3) / 4;
    public FloatRect Dock => new(20, Height - 72, Width - 40, 56);
    /// <summary>The three-item shortcut pill. The navigation trigger occupies a separate circle at its right.</summary>
    public FloatRect DockPill => new(Dock.X, Dock.Y, Math.Max(0, Dock.Width - 64), Dock.Height);
    public FloatRect DockNavigation => new(Dock.Right - 56, Dock.Y, 56, 56);
    public FloatRect DockItem(int index)
    {
        if (index is < 0 or > 2) throw new ArgumentOutOfRangeException(nameof(index));
        var pill = DockPill;
        float center = pill.X + pill.Width * ((index + 1) / 4f);
        return new(center - 24, Dock.Y + 4, 48, 48);
    }
    public FloatRect DockFolder => new(DockItem(0).X + 2, Dock.Y + 6, 44, 44);
    public FloatRect DockAppSlot(int index)
    {
        if (index is < 0 or > 1) throw new ArgumentOutOfRangeException(nameof(index));
        return DockItem(index + 1);
    }
    public FloatRect DockAppIcon(int index)
    {
        var slot = DockAppSlot(index);
        return new(slot.X + (slot.Width - 44) / 2, slot.Y + (slot.Height - 44) / 2, 44, 44);
    }
    public FloatRect NavTrigger => DockNavigation;
    // Keep the upright capsule inside the Home surface, concentric with its trigger.
    public FloatRect NavRail
    {
        get
        {
            float bottom = Dock.Y - 8;
            float top = Math.Max(54, bottom - 264);
            return new(NavTrigger.X + 4, top, 48, Math.Max(0, bottom - top));
        }
    }
    public FloatRect NavRailItem(int index)
    {
        if (index is < 0 or > 4) throw new ArgumentOutOfRangeException(nameof(index));
        var rail = NavRail;
        float step = rail.Height / 5;
        float diameter = Math.Min(40, Math.Max(0, step - 4));
        return new(rail.X + (rail.Width - diameter) / 2,
            rail.Y + index * step + (step - diameter) / 2, diameter, diameter);
    }
    public FloatRect GridViewport => new(0, 54, Width, Math.Max(44, Dock.Y - 12 - 54));
    public float ContentBottom => HomeLayout.GridTop + RowHeight * 3 + RowGap * 2;
    public float MaxScroll => Math.Max(0, ContentBottom - GridViewport.Bottom);
    public FloatRect NavCard => new(HomeLayout.Inset + 2 * (CellWidth + HomeLayout.ColumnGap),
        HomeLayout.GridTop + RowHeight + RowGap, CellWidth * 2 + HomeLayout.ColumnGap, RowHeight * 2 + RowGap);
    public static HomePresentationLayout Resolve(float width, float height, float scroll = 0)
    {
        var layout = new HomePresentationLayout(Math.Clamp(width, 320, 360), Math.Clamp(height, 300, 512), 0);
        return layout with { Scroll = float.IsFinite(scroll) ? Math.Clamp(scroll, 0, layout.MaxScroll) : 0 };
    }
    public FloatRect AppSlot(int index)
    {
        var (col, row) = HomeLayout.AppCells[index];
        return new(HomeLayout.Inset + col * (CellWidth + HomeLayout.ColumnGap),
            HomeLayout.GridTop + row * (RowHeight + RowGap), CellWidth, RowHeight);
    }
    public FloatRect AppIcon(int index)
    {
        var slot = AppSlot(index);
        return new(slot.X + (slot.Width - 52) / 2, slot.Y + (slot.Height - 72) / 2, 52, 52);
    }
    public FloatRect AppLabel(int index)
    {
        var icon = AppIcon(index);
        return new(AppSlot(index).X - 2, icon.Bottom + 4, CellWidth + 4, 16);
    }
    public FloatRect NavEntry(int index)
    {
        if (index is < 0 or > 2) throw new ArgumentOutOfRangeException(nameof(index));
        var nav = NavCard;
        float centerY = Math.Min(nav.Height * .3f + 14, nav.Height - 22 - 92) + index * 46;
        float centerX = nav.Width * HomeLayout.NavBoxScales[index] - 18;
        return new(nav.X + centerX - 22, nav.Y + centerY - 22, 44, 44);
    }
    public FloatRect FolderSlot(int index) => new(Width / 2 + (index - 1) * 56 - 22, Dock.Y + 6, 44, 44);
    public FloatRect Popover
    {
        get
        {
            float width = Math.Min(312, Width - 48), height = Math.Min(208, Dock.Y - 64);
            return new((Width - width) / 2, Dock.Y - 12 - height, width, height);
        }
    }
    public FloatRect PopoverTitle => new(Popover.X + 12, Popover.Y + 12, Popover.Width - 24, 20);
    public FloatRect PopoverOpenButton => new(Popover.X + 12, Popover.Bottom - 60, Popover.Width - 24, 44);
    public FloatRect Preview(int index)
    {
        float width = (Popover.Width - 40) / 3;
        return new(Popover.X + 12 + index * (width + 8), Popover.Y + 40, width,
            PopoverOpenButton.Y - Popover.Y - 48);
    }
    public FloatRect Presented(FloatRect rect) => rect with { Y = rect.Y - Scroll };
    public FloatRect? VisibleTarget(FloatRect rect)
    {
        rect = Presented(rect);
        float top = Math.Max(rect.Y, GridViewport.Y), bottom = Math.Min(rect.Bottom, GridViewport.Bottom);
        // DIP arithmetic can lose a few ulps when the bottom edge coincides
        // with the viewport; do not drop a fully visible 44-DIP destination.
        return bottom - top >= 44 - .001f ? new(rect.X, top, rect.Width, bottom - top) : null;
    }
}


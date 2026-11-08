using System.Numerics;

namespace GlassDesk.Core;

/// <summary>Legacy 360 DIP grid geometry. The horizontal Home dashboard uses DashboardWidth.</summary>
public static class HomeLayout
{
    public const float Width = 360, Height = 512;
    public const float DashboardWidth = 960;
    public const float Inset = 18;
    public const float GridTop = 64;

    public const int Columns = 4, Rows = 3;
    public const float CellWidth = 72, CellHeight = 96, ColumnGap = 12, RowGap = 14;
    public const float IconSize = 52, IconLabelHeight = 16, IconLabelGap = 7;

    /// <summary>Bottom-right 2×2 cells of the grid (massive-insect-5 navigation card).</summary>
    public const float NavColumn = 2, NavRow = 1, NavColumnSpan = 2, NavRowSpan = 2;
    public const float NavWidth = CellWidth * NavColumnSpan + ColumnGap;
    public const float NavHeight = CellHeight * NavRowSpan + RowGap;

    public const float DockX = 354, DockY = 444, DockWidth = 252, DockHeight = 44;
    public const float FolderSize = 44;
    /// <summary>Centres of the three 44 DIP dock slots, relative to the dock left edge.</summary>
    public static readonly IReadOnlyList<float> FolderCenters = new[] { 36f, 98f, 160f };

    public const float PopoverX = 24, PopoverY = 220, PopoverWidth = 312, PopoverHeight = 208;
    public const float PopoverInset = 12;
    public const float PreviewTop = 40, PreviewHeight = 100, PreviewGap = 8;
    public const float OpenButtonHeight = 44, OpenButtonBottom = 16;

    /// <summary>Four nested glass boxes slide out of the bottom-left corner on hover/pin,
    /// replicating the massive-insect-5 reference (70/50/30/10% with staggered delays).</summary>
    public static readonly float[] NavBoxScales = { .70f, .50f, .30f, .10f };

    public static FloatRect NavBox(int index)
    {
        if (index is < 0 or > 3) throw new ArgumentOutOfRangeException(nameof(index));
        var card = NavCard;
        float width = card.Width * NavBoxScales[index];
        float height = card.Height * NavBoxScales[index];
        return new(card.X - 1, card.Bottom - height + 1, width, height);
    }

    /// <summary>The first three boxes are the interactive destinations (music/widgets/settings).</summary>
    public static FloatRect NavEntry(int index)
    {
        if (index is < 0 or > 2) throw new ArgumentOutOfRangeException(nameof(index));
        return NavBox(index);
    }
    public static FloatRect AppSlot(int column, int row)
    {
        CheckCell(column, row);
        return new(Inset + column * (CellWidth + ColumnGap), GridTop + row * (CellHeight + RowGap),
            CellWidth, CellHeight);
    }

    public static FloatRect AppIcon(int column, int row)
    {
        var cell = AppSlot(column, row);
        float top = cell.Y + (cell.Height - IconSize - IconLabelGap - IconLabelHeight) / 2;
        return new(cell.X + (cell.Width - IconSize) / 2, top, IconSize, IconSize);
    }

    public static FloatRect AppLabel(int column, int row)
    {
        var icon = AppIcon(column, row);
        return new(icon.X - 12, icon.Bottom + IconLabelGap, IconSize + 24, IconLabelHeight);
    }

    /// <summary>Eight application cells; the bottom-right 2×2 cells belong to the navigation card.</summary>
    public static readonly IReadOnlyList<(int Column, int Row)> AppCells = new[]
    {
        (0, 0), (1, 0), (2, 0), (3, 0),
        (0, 1), (1, 1),
        (0, 2), (1, 2),
    };

    public static FloatRect NavCard => new(
        Inset + (int)NavColumn * (CellWidth + ColumnGap),
        GridTop + (int)NavRow * (CellHeight + RowGap),
        NavWidth, NavHeight);

    /// <summary>Expand/collapse affordance; the eyes sit at the card centre in rest state.</summary>
    public static FloatRect NavCardHit => NavCard;

    public static FloatRect Dock => new(DockX, DockY, DockWidth, DockHeight);

    public static FloatRect FolderSlot(int index)
    {
        if (index < 0 || index >= FolderCenters.Count) throw new ArgumentOutOfRangeException(nameof(index));
        float center = FolderCenters[index];
        return new(DockX + center - FolderSize / 2, DockY + (DockHeight - FolderSize) / 2, FolderSize, FolderSize);
    }

    public static FloatRect Popover => new(PopoverX, PopoverY, PopoverWidth, PopoverHeight);

    public static FloatRect PopoverTitle
    {
        get
        {
            var pop = Popover;
            return new(pop.X + PopoverInset, pop.Y + 12, pop.Width - PopoverInset * 2, 20);
        }
    }

    /// <summary>Three preview columns between the title and the bottom open button.</summary>
    public static FloatRect PopoverPreview(int index)
    {
        if (index is < 0 or > 2) throw new ArgumentOutOfRangeException(nameof(index));
        var pop = Popover;
        float inner = pop.Width - PopoverInset * 2;
        float width = (inner - PreviewGap * 2) / 3;
        return new(pop.X + PopoverInset + index * (width + PreviewGap), pop.Y + PreviewTop, width, PreviewHeight);
    }

    public static FloatRect PopoverOpenButton
    {
        get
        {
            var pop = Popover;
            return new(pop.X + PopoverInset, pop.Bottom - OpenButtonBottom - OpenButtonHeight,
                pop.Width - PopoverInset * 2, OpenButtonHeight);
        }
    }

    /// <summary>Arrow centre in popover-local coordinates, clamped to the rounded safe area.</summary>
    public static float PopoverArrowX(int folderIndex)
    {
        var folder = FolderSlot(folderIndex);
        var pop = Popover;
        return Math.Clamp(folder.X + folder.Width / 2 - pop.X, 22, pop.Width - 22);
    }

    public static bool ContainsColumnRow(int column, int row) => column is >= 0 and < Columns && row is >= 0 and < Rows;

    static void CheckCell(int column, int row)
    {
        if (!ContainsColumnRow(column, row)) throw new ArgumentOutOfRangeException(nameof(column));
    }
}

using GlassDesk.Core;

/// <summary>Home page geometry: sparse 4×3 grid, bottom-right 2×2 nav card, folder dock and
/// the popover that anchors to a folder. Values come from REVIEW §6.3 and must stay 1:1.</summary>
static class HomeLayoutChecks
{
    public static void Run(Action<bool, string> check)
    {
        static bool Near(float a, float b) => Math.Abs(a - b) < .001f;

        check(HomeLayout.Width == 360 && HomeLayout.Height == 512
            && HomeLayout.Columns == 4 && HomeLayout.Rows == 3,
            "home page is the reviewed 360x512 canvas with a 4x3 grid");

        var nav = HomeLayout.NavCard;
        check(nav == new FloatRect(186, 174, 156, 206),
            "navigation card occupies the bottom-right 2x2 cells at (186,174,156,206)");

        var dock = HomeLayout.Dock;
        check(dock == new FloatRect(HomeLayout.DockX, HomeLayout.DockY, HomeLayout.DockWidth, HomeLayout.DockHeight),
            "folder dock follows its current dashboard geometry");

        bool folders = true;
        for (int index = 0; index < HomeLayout.FolderCenters.Count; index++)
        {
            var slot = HomeLayout.FolderSlot(index);
            folders &= Near(slot.X + slot.Width / 2, dock.X + HomeLayout.FolderCenters[index])
                && Near(slot.Y + slot.Height / 2, dock.Y + dock.Height / 2)
                && slot.Width >= 44 && slot.Height >= 44 && slot.Y >= dock.Y && slot.Bottom <= dock.Bottom;
        }
        check(folders, "folder buttons stay centred on their dock-relative slots with >=44x44 targets");

        var pop = HomeLayout.Popover;
        check(pop == new FloatRect(24, 220, 312, 208)
            && HomeLayout.PopoverOpenButton == new FloatRect(36, 368, 288, 44)
            && HomeLayout.PopoverTitle == new FloatRect(36, 232, 288, 20),
            "folder popover uses the reviewed 312x208 recipe with a full-width 44 DIP open button");

        bool previews = true;
        for (int index = 0; index < 3; index++)
        {
            var preview = HomeLayout.PopoverPreview(index);
            previews &= Near(preview.Y, 260) && preview.Height == 100
                && preview.Width > 80 && preview.Right <= pop.Right - 12
                && (index == 0 || preview.X > HomeLayout.PopoverPreview(index - 1).Right);
            check(previews, "the three preview columns sit side by side above the open button");
        }

        bool arrows = true;
        for (int index = 0; index < 3; index++)
        {
            float arrow = HomeLayout.PopoverArrowX(index);
            arrows &= arrow >= 22 && arrow <= pop.Width - 22
                && Near(arrow, Math.Clamp(HomeLayout.FolderSlot(index).X + HomeLayout.FolderSize / 2 - pop.X,
                    22, pop.Width - 22));
        }
        check(arrows, "the popover arrow follows each folder where possible and stays inside the rounded safe area");

        check(HomeLayout.AppCells.Count == 8 && HomeLayout.AppCells.All(cell => HomeLayout.ContainsColumnRow(cell.Column, cell.Row))
            && !HomeLayout.AppCells.Any(cell => cell.Column >= 2 && cell.Row >= 1),
            "eight application cells are used; the bottom-right 2x2 cells stay reserved for navigation");

        var firstCell = HomeLayout.AppSlot(0, 0);
        var firstIcon = HomeLayout.AppIcon(0, 0);
        var firstLabel = HomeLayout.AppLabel(0, 0);
        check(firstCell == new FloatRect(18, 64, 72, 96)
            && firstIcon.Width == 52 && firstIcon.Height == 52
            && Near(firstIcon.X + firstIcon.Width / 2, firstCell.X + firstCell.Width / 2)
            && firstLabel.Height == 16 && firstLabel.Width >= 52
            && firstIcon.Bottom <= firstLabel.Y && firstLabel.Bottom <= firstCell.Bottom,
            "application cell/icon/label geometry stays inside the 72x96 slot");

        bool navEntries = true;
        for (int index = 0; index < 4; index++)
        {
            var box = HomeLayout.NavBox(index);
            navEntries &= Near(box.X, nav.X - 1) && Near(box.Bottom, nav.Bottom + 1)
                && Near(box.Width, nav.Width * HomeLayout.NavBoxScales[index])
                && Near(box.Height, nav.Height * HomeLayout.NavBoxScales[index])
                && box.Height >= 20
                && (index == 0 || box.Width < HomeLayout.NavBox(index - 1).Width);
            if (index < 3)
            {
                var entry = HomeLayout.NavEntry(index);
                navEntries &= entry == box && entry.Width >= 44 && entry.Height >= 44;
            }
        }
        check(navEntries,
            "four nested navigation boxes keep the reference 70/50/30/10% bottom-left fan with >=44 DIP destinations");

        bool outside = false;
        try { HomeLayout.AppSlot(4, 0); } catch (ArgumentOutOfRangeException) { outside = true; }
        check(outside, "application slots reject out-of-grid columns");
    }
}

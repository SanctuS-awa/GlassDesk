using GlassDesk.Core;

namespace GlassDesk.App;

internal readonly record struct HomeFolderEntry(string Name, string Path, bool IsFolder);
internal enum HomeFolderStatus { Loading, Ready, Empty, Error }
internal sealed record HomeFolderPreview(string Name, string Path, IReadOnlyList<HomeFolderEntry> Entries,
    HomeFolderStatus Status = HomeFolderStatus.Ready, string? Message = null);

// Snapshot values, not the list reference or record.ToString(): the same directory can
// supply new entries while keeping its name, path and status unchanged.
internal sealed record HomePopupRenderKey(int Selected, string Name, string Path, HomeFolderStatus Status,
    string? Message, int Count, HomeFolderEntry? First, HomeFolderEntry? Second, HomeFolderEntry? Third,
    FloatRect Bounds, float ArrowX, int Pressed, uint Foreground, uint Background, uint Secondary,
    uint Dpi, bool HighContrast)
{
    public static HomePopupRenderKey Create(HomePresentationLayout layout, int selected, HomeFolderPreview preview,
        int pressed, uint foreground, uint background, uint secondary, uint dpi, bool highContrast)
    {
        HomeFolderEntry? Entry(int index) => index < preview.Entries.Count ? preview.Entries[index] : null;
        return new(selected, preview.Name, preview.Path, preview.Status, preview.Message, preview.Entries.Count,
            Entry(0), Entry(1), Entry(2), layout.Popover, layout.DockFolder.X + 22 - layout.Popover.X,
            pressed, foreground, background, secondary, dpi, highContrast);
    }
}

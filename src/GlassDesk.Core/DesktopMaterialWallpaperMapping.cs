namespace GlassDesk.Core;

public enum DesktopWallpaperPositionMode
{
    Fill,
    Fit,
    Stretch,
    Center
}

/// <summary>Monitor bounds in physical screen pixels.</summary>
public readonly record struct PhysicalMonitorRect(double X, double Y, double Width, double Height);

/// <summary>Decoded wallpaper dimensions in physical pixels.</summary>
public readonly record struct WallpaperImageSize(int Width, int Height);

/// <summary>Result of mapping a physical screen point into a wallpaper image.</summary>
public readonly record struct WallpaperMappedPoint(
    bool IsOnMonitor,
    bool IsCoveredByImage,
    double PixelX,
    double PixelY);

/// <summary>Hard decode/input budget used before any wallpaper pixels are allocated.</summary>
public sealed class DesktopWallpaperMappingBudget
{
    public const int DefaultMaxImageDimension = 4_096;
    public const long DefaultMaxImagePixels = 16_000_000;
    public const long DefaultMaxImageFileBytes = 40L * 1024 * 1024;

    public DesktopWallpaperMappingBudget(int maxImageDimension = DefaultMaxImageDimension,
        long maxImagePixels = DefaultMaxImagePixels,
        long maxImageFileBytes = DefaultMaxImageFileBytes)
    {
        if (maxImageDimension <= 0) throw new ArgumentOutOfRangeException(nameof(maxImageDimension));
        if (maxImagePixels <= 0) throw new ArgumentOutOfRangeException(nameof(maxImagePixels));
        if (maxImageFileBytes <= 0) throw new ArgumentOutOfRangeException(nameof(maxImageFileBytes));
        MaxImageDimension = maxImageDimension;
        MaxImagePixels = maxImagePixels;
        MaxImageFileBytes = maxImageFileBytes;
    }

    public int MaxImageDimension { get; }
    public long MaxImagePixels { get; }
    public long MaxImageFileBytes { get; }

    /// <summary>Check compressed/source file size before decoding or allocating pixels.</summary>
    public bool AllowsSourceFileBytes(long bytes) => bytes > 0 && bytes <= MaxImageFileBytes;

    public bool Allows(WallpaperImageSize size)
    {
        if (size.Width <= 0 || size.Height <= 0) return false;
        if (size.Width > MaxImageDimension || size.Height > MaxImageDimension) return false;
        // Widen before multiplication. Two positive Int32 dimensions fit in Int64.
        long pixels = (long)size.Width * size.Height;
        return pixels <= MaxImagePixels;
    }
}

/// <summary>
/// Immutable screen-to-wallpaper mapping for a single physical monitor. It never
/// follows a capsule's size: identical screen points always map to identical pixels.
/// </summary>
public sealed class DesktopWallpaperMapping
{
    private readonly PhysicalMonitorRect monitor;
    private readonly WallpaperImageSize image;
    private readonly double destinationX;
    private readonly double destinationY;
    private readonly double destinationWidth;
    private readonly double destinationHeight;

    private DesktopWallpaperMapping(PhysicalMonitorRect monitor, WallpaperImageSize image,
        DesktopWallpaperPositionMode mode, double destinationX, double destinationY,
        double destinationWidth, double destinationHeight)
    {
        this.monitor = monitor;
        this.image = image;
        Mode = mode;
        this.destinationX = destinationX;
        this.destinationY = destinationY;
        this.destinationWidth = destinationWidth;
        this.destinationHeight = destinationHeight;
    }

    public DesktopWallpaperPositionMode Mode { get; }
    public PhysicalMonitorRect Monitor => monitor;
    public WallpaperImageSize Image => image;
    public double DestinationX => destinationX;
    public double DestinationY => destinationY;
    public double DestinationWidth => destinationWidth;
    public double DestinationHeight => destinationHeight;

    public static bool TryCreate(PhysicalMonitorRect monitor, WallpaperImageSize image,
        DesktopWallpaperPositionMode mode, out DesktopWallpaperMapping? mapping,
        DesktopWallpaperMappingBudget? budget = null)
    {
        mapping = null;
        budget ??= new DesktopWallpaperMappingBudget();
        if (!Enum.IsDefined(mode) || !budget.Allows(image)) return false;
        if (!double.IsFinite(monitor.X) || !double.IsFinite(monitor.Y)
            || !double.IsFinite(monitor.Width) || !double.IsFinite(monitor.Height)
            || monitor.Width <= 0 || monitor.Height <= 0) return false;

        double monitorRight = monitor.X + monitor.Width;
        double monitorBottom = monitor.Y + monitor.Height;
        if (!double.IsFinite(monitorRight) || !double.IsFinite(monitorBottom)
            || monitorRight <= monitor.X || monitorBottom <= monitor.Y) return false;

        double x, y, width, height;
        switch (mode)
        {
            case DesktopWallpaperPositionMode.Fill:
            {
                double scale = Math.Max(monitor.Width / image.Width, monitor.Height / image.Height);
                width = image.Width * scale;
                height = image.Height * scale;
                x = monitor.X + (monitor.Width - width) * .5;
                y = monitor.Y + (monitor.Height - height) * .5;
                break;
            }
            case DesktopWallpaperPositionMode.Fit:
            {
                double scale = Math.Min(monitor.Width / image.Width, monitor.Height / image.Height);
                width = image.Width * scale;
                height = image.Height * scale;
                x = monitor.X + (monitor.Width - width) * .5;
                y = monitor.Y + (monitor.Height - height) * .5;
                break;
            }
            case DesktopWallpaperPositionMode.Stretch:
                x = monitor.X;
                y = monitor.Y;
                width = monitor.Width;
                height = monitor.Height;
                break;
            case DesktopWallpaperPositionMode.Center:
                width = image.Width;
                height = image.Height;
                x = monitor.X + (monitor.Width - width) * .5;
                y = monitor.Y + (monitor.Height - height) * .5;
                break;
            default:
                return false;
        }

        if (!double.IsFinite(x) || !double.IsFinite(y) || !double.IsFinite(width) || !double.IsFinite(height)
            || width <= 0 || height <= 0 || !double.IsFinite(x + width) || !double.IsFinite(y + height)
            || x + width <= x || y + height <= y)
            return false;
        if (!double.IsFinite(image.Width / width) || !double.IsFinite(image.Height / height)) return false;

        mapping = new DesktopWallpaperMapping(monitor, image, mode, x, y, width, height);
        return true;
    }

    /// <summary>Creates a mapping only after the source-file byte budget also passes.</summary>
    public static bool TryCreate(PhysicalMonitorRect monitor, WallpaperImageSize image, long sourceFileBytes,
        DesktopWallpaperPositionMode mode, out DesktopWallpaperMapping? mapping,
        DesktopWallpaperMappingBudget? budget = null)
    {
        budget ??= new DesktopWallpaperMappingBudget();
        if (!budget.AllowsSourceFileBytes(sourceFileBytes))
        {
            mapping = null;
            return false;
        }
        return TryCreate(monitor, image, mode, out mapping, budget);
    }

    /// <summary>
    /// Maps a physical screen point. The output pixel coordinate is continuous in
    /// image-boundary space; callers should clamp sampling footprints to image bounds.
    /// Returns false only when arithmetic cannot be represented.
    /// </summary>
    public bool TryMapScreenPoint(double screenX, double screenY, out WallpaperMappedPoint result)
    {
        result = default;
        if (!double.IsFinite(screenX) || !double.IsFinite(screenY)) return false;
        double monitorRight = monitor.X + monitor.Width;
        double monitorBottom = monitor.Y + monitor.Height;
        bool onMonitor = screenX >= monitor.X && screenX < monitorRight
            && screenY >= monitor.Y && screenY < monitorBottom;

        double pixelX = (screenX - destinationX) * (image.Width / destinationWidth);
        double pixelY = (screenY - destinationY) * (image.Height / destinationHeight);
        if (!double.IsFinite(pixelX) || !double.IsFinite(pixelY)) return false;

        bool covered = onMonitor && pixelX >= 0 && pixelX < image.Width && pixelY >= 0 && pixelY < image.Height;
        result = new WallpaperMappedPoint(onMonitor, covered, pixelX, pixelY);
        return true;
    }
}

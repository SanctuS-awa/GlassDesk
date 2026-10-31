using System.Buffers.Binary;
using System.IO;
using System.IO.Compression;

namespace GlassDesk.Core;

public readonly record struct ReadingSupportMaskLayout(
    float WidthDip, float HeightDip, float DpiScale, FloatRect First, FloatRect Second, float FeatherDip);

public readonly record struct ReadingSupportMaskCanvas(
    float WidthDip, float HeightDip, float DpiScale, float FeatherDip);

public readonly record struct ReadingSupportMaskLine(FloatRect Bounds, ReadingMaterialSupport Material);

/// <summary>Encodes a local, app-owned reading-support mask as an RGBA PNG.</summary>
public static class ReadingSupportMask
{
    const int MaximumEdge = 2048;
    const long MaximumPixels = 4_000_000;
    const int MaximumPngBytes = 16 * 1024 * 1024;
    static readonly byte[] Signature = [137, 80, 78, 71, 13, 10, 26, 10];
    static readonly uint[] CrcTable = BuildCrcTable();

    public static byte[] CreatePng(ReadingSupportMaskLayout layout, ReadingMaterialSupport material)
    {
        ValidateLayout(layout);
        ValidateSupportColor(material, nameof(material));

        Span<ReadingSupportMaskLine> lines = stackalloc ReadingSupportMaskLine[2];
        lines[0] = new(layout.First, material);
        lines[1] = new(layout.Second, material);
        return CreateLinesPng(new(layout.WidthDip, layout.HeightDip, layout.DpiScale, layout.FeatherDip), lines);
    }

    public static byte[] CreateLinesPng(ReadingSupportMaskCanvas canvas, ReadOnlySpan<ReadingSupportMaskLine> lines)
    {
        ValidateCanvas(canvas);
        if (lines.Length > 4)
            throw new ArgumentOutOfRangeException(nameof(lines), "A mask supports at most four text-line platforms.");

        uint supportColor = 0;
        for (int i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            ValidatePlatform(line.Bounds, canvas.WidthDip, canvas.HeightDip, $"lines[{i}].Bounds");
            if (IsEmpty(line.Bounds)) continue;
            ValidateSupportColor(line.Material, $"lines[{i}].Material");
            if (supportColor != 0 && supportColor != line.Material.ColorArgb)
                throw new ArgumentOutOfRangeException(nameof(lines), "All non-empty platforms must use the same white or black support color.");
            supportColor = line.Material.ColorArgb;
        }

        return CreatePngCore(canvas, lines, supportColor);
    }

    static byte[] CreatePngCore(ReadingSupportMaskCanvas canvas, ReadOnlySpan<ReadingSupportMaskLine> lines, uint supportColor)
    {
        int width = PixelExtent(canvas.WidthDip, canvas.DpiScale, nameof(canvas.WidthDip));
        int height = PixelExtent(canvas.HeightDip, canvas.DpiScale, nameof(canvas.HeightDip));
        if ((long)width * height > MaximumPixels)
            throw new ArgumentOutOfRangeException(nameof(canvas), "Mask exceeds the 4,000,000 pixel limit.");

        using var png = new BoundedMemoryStream();
        png.Write(Signature);
        Span<byte> header = stackalloc byte[13];
        BinaryPrimitives.WriteUInt32BigEndian(header, (uint)width);
        BinaryPrimitives.WriteUInt32BigEndian(header[4..], (uint)height);
        header[8] = 8; header[9] = 6; // 8-bit RGBA, straight alpha.
        header[10] = header[11] = header[12] = 0;
        WriteChunk(png, "IHDR"u8, header);

        long idatLengthOffset = png.Position;
        WriteUInt32(png, 0);
        png.Write("IDAT"u8);
        long idatDataOffset = png.Position;
        int rowLength = checked(1 + width * 4);
        byte[] row = new byte[rowLength];
        bool hasSupport = false;
        for (int i = 0; i < lines.Length; i++)
            hasSupport |= !IsEmpty(lines[i].Bounds) && lines[i].Material.Opacity8Bit > 0;
        byte red = (byte)(supportColor >> 16), green = (byte)(supportColor >> 8), blue = (byte)supportColor;

        using (var zlib = new ZLibStream(png, CompressionLevel.Fastest, leaveOpen: true))
        {
            for (int y = 0; y < height; y++)
            {
                row[0] = 0; // PNG filter None.
                if (!hasSupport)
                {
                    Array.Clear(row, 1, row.Length - 1);
                }
                else
                {
                    double py = (y + .5d) / canvas.DpiScale;
                    for (int x = 0; x < width; x++)
                    {
                        double px = (x + .5d) / canvas.DpiScale;
                        byte alpha = 0;
                        for (int i = 0; i < lines.Length; i++)
                        {
                            var line = lines[i];
                            if (line.Material.Opacity8Bit == 0 || IsEmpty(line.Bounds)) continue;
                            double distance = DistanceToRect(px, py, line.Bounds);
                            double mask = FeatherCoverage(distance, canvas.FeatherDip);
                            byte candidate = (byte)Math.Clamp((int)Math.Round(
                                line.Material.Opacity8Bit * mask, MidpointRounding.AwayFromZero), 0, 255);
                            if (candidate > alpha) alpha = candidate;
                        }
                        int offset = 1 + x * 4;
                        if (alpha == 0)
                        {
                            row[offset] = row[offset + 1] = row[offset + 2] = row[offset + 3] = 0;
                        }
                        else
                        {
                            row[offset] = red; row[offset + 1] = green; row[offset + 2] = blue; row[offset + 3] = alpha;
                        }
                    }
                }
                zlib.Write(row);
            }
        }

        long idatCrcOffset = png.Position;
        long compressedLength = idatCrcOffset - idatDataOffset;
        if (compressedLength < 0 || compressedLength > uint.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(canvas), "Compressed mask is outside PNG chunk bounds.");
        Span<byte> contents = png.GetBuffer().AsSpan(checked((int)idatLengthOffset + 4), checked(4 + (int)compressedLength));
        uint idatCrc = Crc32(contents);
        png.Position = idatLengthOffset;
        WriteUInt32(png, (uint)compressedLength);
        png.Position = idatCrcOffset;
        WriteUInt32(png, idatCrc);
        WriteChunk(png, "IEND"u8, ReadOnlySpan<byte>.Empty);
        if (png.Length > MaximumPngBytes)
            throw new ArgumentOutOfRangeException(nameof(canvas), "PNG exceeds the 16 MiB encoded-size limit.");
        return png.ToArray();
    }

    static void ValidateLayout(ReadingSupportMaskLayout layout)
    {
        ValidateCanvas(new(layout.WidthDip, layout.HeightDip, layout.DpiScale, layout.FeatherDip));
        ValidatePlatform(layout.First, layout.WidthDip, layout.HeightDip, nameof(layout.First));
        ValidatePlatform(layout.Second, layout.WidthDip, layout.HeightDip, nameof(layout.Second));
    }

    static void ValidateCanvas(ReadingSupportMaskCanvas canvas)
    {
        if (!float.IsFinite(canvas.DpiScale) || canvas.DpiScale < .5f || canvas.DpiScale > 4f)
            throw new ArgumentOutOfRangeException(nameof(canvas.DpiScale));
        if (!float.IsFinite(canvas.WidthDip) || canvas.WidthDip <= 0)
            throw new ArgumentOutOfRangeException(nameof(canvas.WidthDip));
        if (!float.IsFinite(canvas.HeightDip) || canvas.HeightDip <= 0)
            throw new ArgumentOutOfRangeException(nameof(canvas.HeightDip));
        if (!float.IsFinite(canvas.FeatherDip) || canvas.FeatherDip < 0)
            throw new ArgumentOutOfRangeException(nameof(canvas.FeatherDip));
    }

    static void ValidateSupportColor(ReadingMaterialSupport material, string name)
    {
        if (material.ColorArgb is not (0xFFFFFFFFu or 0xFF000000u))
            throw new ArgumentOutOfRangeException(name, "Support must be opaque white or black.");
    }

    static void ValidatePlatform(FloatRect rect, float canvasWidth, float canvasHeight, string name)
    {
        if (!float.IsFinite(rect.X) || !float.IsFinite(rect.Y) || !float.IsFinite(rect.Width) || !float.IsFinite(rect.Height)
            || rect.X < 0 || rect.Y < 0 || rect.X > canvasWidth || rect.Y > canvasHeight)
            throw new ArgumentOutOfRangeException(name, "Text-line platforms must be finite and inside the logical canvas.");
        if (IsEmpty(rect)) return;
        // A single float ULP covers endpoints assembled from measured DIP values (for example .6f + .1f vs .7f).
        // Raster output is still clipped to the canvas because only in-canvas pixel centers are emitted.
        if (rect.Width <= 0 || rect.Height <= 0
            || rect.Width > canvasWidth || rect.Height > canvasHeight
            || (double)rect.X + rect.Width > MathF.BitIncrement(canvasWidth)
            || (double)rect.Y + rect.Height > MathF.BitIncrement(canvasHeight))
            throw new ArgumentOutOfRangeException(name, "Text-line platforms must be finite, positive, and inside the logical canvas.");
    }

    static bool IsEmpty(FloatRect rect) => rect.Width == 0 && rect.Height == 0;

    static int PixelExtent(float dips, float dpiScale, string name)
    {
        double physical = Math.Ceiling((double)dips * dpiScale);
        if (!double.IsFinite(physical) || physical < 1 || physical > MaximumEdge)
            throw new ArgumentOutOfRangeException(name, "PNG edges are limited to 2048 physical pixels.");
        return (int)physical;
    }

    static double DistanceToRect(double x, double y, FloatRect rect)
    {
        if (IsEmpty(rect)) return double.PositiveInfinity;
        double dx = x < rect.X ? rect.X - x : x > rect.Right ? x - rect.Right : 0;
        double dy = y < rect.Y ? rect.Y - y : y > rect.Bottom ? y - rect.Bottom : 0;
        return Math.Sqrt(dx * dx + dy * dy);
    }

    static double FeatherCoverage(double distance, float feather)
    {
        if (distance <= 0) return 1;
        if (feather <= 0 || distance >= feather) return 0;
        double t = distance / feather;
        return 1 - t * t * (3 - 2 * t);
    }

    static void WriteChunk(Stream output, ReadOnlySpan<byte> type, ReadOnlySpan<byte> data)
    {
        WriteUInt32(output, checked((uint)data.Length));
        output.Write(type); output.Write(data);
        uint crc = Crc32(type, data);
        WriteUInt32(output, crc);
    }

    static void WriteUInt32(Stream output, uint value)
    {
        Span<byte> bytes = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(bytes, value);
        output.Write(bytes);
    }

    static uint Crc32(ReadOnlySpan<byte> data) => Crc32(ReadOnlySpan<byte>.Empty, data);
    static uint Crc32(ReadOnlySpan<byte> first, ReadOnlySpan<byte> second)
    {
        uint crc = uint.MaxValue;
        foreach (byte value in first) crc = (crc >> 8) ^ CrcTable[(int)((crc ^ value) & 0xff)];
        foreach (byte value in second) crc = (crc >> 8) ^ CrcTable[(int)((crc ^ value) & 0xff)];
        return ~crc;
    }

    static uint[] BuildCrcTable()
    {
        var table = new uint[256];
        for (uint i = 0; i < table.Length; i++)
        {
            uint value = i;
            for (int bit = 0; bit < 8; bit++) value = (value & 1) != 0 ? 0xedb88320u ^ (value >> 1) : value >> 1;
            table[i] = value;
        }
        return table;
    }

    sealed class BoundedMemoryStream : MemoryStream
    {
        void EnsureWrite(int count)
        {
            if (count < 0 || Position < 0 || Position > MaximumPngBytes - (long)count)
                throw new ArgumentOutOfRangeException(nameof(count), "PNG exceeds the 16 MiB encoded-size limit.");
        }
        public override void Write(byte[] buffer, int offset, int count) { EnsureWrite(count); base.Write(buffer, offset, count); }
        public override void Write(ReadOnlySpan<byte> buffer) { EnsureWrite(buffer.Length); base.Write(buffer); }
        public override void WriteByte(byte value) { EnsureWrite(1); base.WriteByte(value); }
    }
}

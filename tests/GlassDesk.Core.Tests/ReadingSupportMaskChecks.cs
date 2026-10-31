using System.Buffers.Binary;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using GlassDesk.Core;

internal static class ReadingSupportMaskChecks
{
    public static void Run(Action<bool, string> check)
    {
        var layout = new ReadingSupportMaskLayout(64, 48, 1,
            new(8, 10, 32, 16), new(24, 14, 28, 16), 4);
        const byte opacity = 137;
        var white = Decode(ReadingSupportMask.CreatePng(layout, new(0xFFFFFFFF, opacity)));
        var black = Decode(ReadingSupportMask.CreatePng(layout, new(0xFF000000, opacity)));
        check(white.Width == 64 && white.Height == 48 && white.BitDepth == 8 && white.ColorType == 6,
            "mask PNG declares ceil-scaled 8-bit RGBA dimensions");
        check(Pixel(white, 12, 12) == new PixelValue(255, 255, 255, opacity)
            && Pixel(black, 12, 12) == new PixelValue(0, 0, 0, opacity),
            "straight-alpha white/black support keeps the full quantized plateau");
        check(Pixel(white, 3, 12).Alpha == 0 && Pixel(black, 3, 12).Alpha == 0,
            "feather is cropped to the canvas and reaches transparent outside its outer edge");
        PixelValue feather = Pixel(white, 7, 12);
        check(feather.Alpha > 0 && feather.Alpha < opacity && feather.Red == 255,
            "outer feather fades alpha while keeping straight white RGB");
        check(Pixel(white, 27, 18).Alpha == opacity && Pixel(black, 27, 18).Alpha == opacity,
            "overlapping text-line platforms retain one full plateau instead of accumulating opacity");

        var noLines = Decode(ReadingSupportMask.CreatePng(
            new ReadingSupportMaskLayout(20, 12, 1, new(20, 12, 0, 0), new(4, 5, 0, 0), 20),
            new(0xFFFFFFFF, 110)));
        check(noLines.Rgba.All(value => value == 0), "two empty line platforms produce a fully transparent PNG");

        var emptySet = Decode(ReadingSupportMask.CreateLinesPng(
            new ReadingSupportMaskCanvas(20, 12, 1, 4), ReadOnlySpan<ReadingSupportMaskLine>.Empty));
        var zeroOpacity = Decode(ReadingSupportMask.CreateLinesPng(
            new ReadingSupportMaskCanvas(20, 12, 1, 4),
            [new(new(4, 3, 8, 4), new(0xFFFFFFFF, 0))]));
        check(emptySet.Rgba.All(value => value == 0) && zeroOpacity.Rgba.All(value => value == 0),
            "empty line sets and zero-opacity lines produce transparent PNGs");

        var scaled = Decode(ReadingSupportMask.CreatePng(
            new ReadingSupportMaskLayout(10.1f, 9.5f, 1.5f, new(.5f, .5f, 4, 4), default, 2),
            new(0xFFFFFFFF, 120)));
        check(scaled.Width == 16 && scaled.Height == 15, "physical dimensions use ceil(logical size times DPI)");

        var twoLines = new ReadingSupportMaskLayout(120, 72, 1,
            new(10, 14, 90, 8), new(47.5f, 34, 15, 8), 20);
        var oneBoundingPlate = twoLines with { First = new(10, 14, 90, 28), Second = default };
        var shaped = Decode(ReadingSupportMask.CreatePng(twoLines, new(0xFFFFFFFF, 110)));
        var broad = Decode(ReadingSupportMask.CreatePng(oneBoundingPlate, new(0xFFFFFFFF, 110)));
        long shapedAlpha = AlphaSum(shaped), broadAlpha = AlphaSum(broad);
        check(shapedAlpha < broadAlpha,
            $"20 DIP feather spends less alpha on short-line/inter-line whitespace ({shapedAlpha} < {broadAlpha})");

        var boundary = Decode(ReadingSupportMask.CreatePng(
            new ReadingSupportMaskLayout(.7f, .7f, 4, new(.6f, .6f, .1f, .1f), default, 0),
            new(0xFFFFFFFF, 120)));
        check(boundary.Width == 3 && boundary.Height == 3 && Pixel(boundary, 2, 2) == new PixelValue(255, 255, 255, 120),
            "decimal float endpoints one ULP past the canvas roundoff remain usable and clipped to the PNG canvas");

        var multiCanvas = new ReadingSupportMaskCanvas(28, 24, 1, 3);
        ReadingSupportMaskLine[] threeLines =
        [
            new(new(2, 2, 12, 10), new(0xFFFFFFFF, 75)),
            new(new(8, 2, 12, 10), new(0xFFFFFFFF, 130)),
            new(new(6, 8, 12, 10), new(0xFFFFFFFF, 96))
        ];
        var threeLineImage = Decode(ReadingSupportMask.CreateLinesPng(multiCanvas, threeLines));
        bool threeLinePlateaus = true, threeLineMaxUnion = true;
        int threeLineCoreSamples = 0, threeLineOverlapSamples = 0;
        for (int y = 0; y < threeLineImage.Height; y++)
        {
            double logicalY = (y + .5d) / multiCanvas.DpiScale;
            for (int x = 0; x < threeLineImage.Width; x++)
            {
                double logicalX = (x + .5d) / multiCanvas.DpiScale;
                byte expected = ExpectedMaxAlpha(multiCanvas, threeLines, logicalX, logicalY);
                PixelValue pixel = Pixel(threeLineImage, x, y);
                threeLineMaxUnion &= pixel.Alpha == expected
                    && (expected == 0 ? pixel.Red == 0 && pixel.Green == 0 && pixel.Blue == 0
                        : pixel.Red == 255 && pixel.Green == 255 && pixel.Blue == 255);
                int coveringLines = 0;
                foreach (var line in threeLines)
                {
                    if (!Inside(logicalX, logicalY, line.Bounds)) continue;
                    coveringLines++;
                    threeLineCoreSamples++;
                    threeLinePlateaus &= pixel.Alpha >= line.Material.Opacity8Bit;
                }
                if (coveringLines > 1) threeLineOverlapSamples++;
            }
        }
        PixelValue threeLineOverlap = Pixel(threeLineImage, 10, 9);
        check(threeLinePlateaus && threeLineMaxUnion && threeLineCoreSamples > 0 && threeLineOverlapSamples > 0
            && threeLineOverlap.Alpha == 130,
            $"three different opacity lines preserve each platform and use max-alpha union at overlap ({threeLineOverlap.Alpha}, not additive)");

        ReadingSupportMaskLine[] fourLines =
        [
            new(new(1, 1, 2, 2), new(0xFF000000, 80)),
            new(new(4, 1, 2, 2), new(0xFF000000, 90)),
            new(new(1, 4, 2, 2), new(0xFF000000, 100)),
            new(new(4, 4, 2, 2), new(0xFF000000, 110))
        ];
        var fourLineImage = Decode(ReadingSupportMask.CreateLinesPng(new(8, 8, 1, 1), fourLines));
        check(Pixel(fourLineImage, 1, 1).Alpha == 80 && Pixel(fourLineImage, 4, 4).Alpha == 110,
            "four same-polarity platforms are accepted and retain their individual opacity");

        ReadingSupportMaskLine[] mixedPolarity =
        [threeLines[0], new(new(16, 14, 8, 8), new(0xFF000000, 96))];
        CheckRejectedLines(check, multiCanvas, mixedPolarity, "rejects mixed white and black line support");
        CheckRejectedLines(check, multiCanvas,
            [new(new(16, 14, 8, 8), new(0x80FFFFFF, 96))],
            "new line API rejects support colors other than opaque white or black");
        ReadingSupportMaskLine[] tooManyLines = [.. fourLines, new(new(1, 7, 2, 1), new(0xFF000000, 90))];
        CheckRejectedLines(check, new(8, 8, 1, 1), tooManyLines, "rejects more than four line platforms");
        CheckRejectedLines(check, new(2049, 1, 1, 0), [new(default, new(0xFFFFFFFF, 110))],
            "new canvas overload retains the 2048 physical-edge limit");
        CheckRejectedLines(check, new(2001, 2000, 1, 0), [],
            "new canvas overload retains the four-million-pixel limit");
        var boundaryLines = new[] { new ReadingSupportMaskLine(new(.6f, .6f, .1f, .1f), new(0xFFFFFFFF, 120)) };
        var lineBoundary = Decode(ReadingSupportMask.CreateLinesPng(new(.7f, .7f, 4, 0), boundaryLines));
        check(lineBoundary.Width == 3 && lineBoundary.Height == 3
            && Pixel(lineBoundary, 2, 2) == new PixelValue(255, 255, 255, 120),
            "new line API keeps one-ULP endpoint tolerance and clips raster output to canvas");

        const byte scanOpacity = 123;
        float[] dpiScales = [.5f, 1f, 1.25f, 1.5f, 2f, 4f];
        bool dpiBounds = true, everyCorePixel = true, overlapStaysFlat = true;
        int coreSamples = 0, overlapSamples = 0;
        var sweep = Stopwatch.StartNew();
        foreach (float dpi in dpiScales)
        {
            var multiDpi = layout with { DpiScale = dpi };
            var whiteAtDpi = Decode(ReadingSupportMask.CreatePng(multiDpi, new(0xFFFFFFFF, scanOpacity)));
            var blackAtDpi = Decode(ReadingSupportMask.CreatePng(multiDpi, new(0xFF000000, scanOpacity)));
            dpiBounds &= whiteAtDpi.Width == Math.Ceiling(multiDpi.WidthDip * dpi)
                && whiteAtDpi.Height == Math.Ceiling(multiDpi.HeightDip * dpi)
                && blackAtDpi.Width == whiteAtDpi.Width && blackAtDpi.Height == whiteAtDpi.Height;
            for (int y = 0; y < whiteAtDpi.Height; y++)
            {
                double logicalY = (y + .5d) / dpi;
                for (int x = 0; x < whiteAtDpi.Width; x++)
                {
                    double logicalX = (x + .5d) / dpi;
                    bool inFirst = Inside(logicalX, logicalY, multiDpi.First);
                    bool inSecond = Inside(logicalX, logicalY, multiDpi.Second);
                    if (!inFirst && !inSecond) continue;
                    coreSamples++;
                    PixelValue w = Pixel(whiteAtDpi, x, y), b = Pixel(blackAtDpi, x, y);
                    everyCorePixel &= w == new PixelValue(255, 255, 255, scanOpacity)
                        && b == new PixelValue(0, 0, 0, scanOpacity);
                    if (inFirst && inSecond)
                    {
                        overlapSamples++;
                        overlapStaysFlat &= w.Alpha == scanOpacity && b.Alpha == scanOpacity
                            && w.Red == 255 && w.Green == 255 && w.Blue == 255
                            && b.Red == 0 && b.Green == 0 && b.Blue == 0;
                    }
                }
            }
        }
        sweep.Stop();
        check(dpiBounds && everyCorePixel && overlapStaysFlat && coreSamples > 0 && overlapSamples > 0,
            $"all {coreSamples} platform pixels and {overlapSamples} overlap pixels stay exact white/black plateaus across DPI .5/1/1.25/1.5/2/4");
        Console.WriteLine($"ReadingSupportMask one-pass CPU sweep: {sweep.Elapsed.TotalMilliseconds:F2} ms; core={coreSamples}, overlap={overlapSamples}; alpha sums shaped={shapedAlpha}, broad={broadAlpha}.");

        CheckRejected(check, new(2049, 1, 1, default, default, 20), new(0xFFFFFFFF, 110), "rejects physical edge above 2048");
        CheckRejected(check, new(2001, 2000, 1, default, default, 20), new(0xFFFFFFFF, 110), "rejects canvas above four million pixels");
        CheckRejected(check, new(10, 10, .49f, default, default, 20), new(0xFFFFFFFF, 110), "rejects DPI below .5");
        CheckRejected(check, new(10, 10, 4.01f, default, default, 20), new(0xFFFFFFFF, 110), "rejects DPI above 4");
        CheckRejected(check, new(float.NaN, 10, 1, default, default, 20), new(0xFFFFFFFF, 110), "rejects NaN canvas width");
        CheckRejected(check, new(10, float.PositiveInfinity, 1, default, default, 20), new(0xFFFFFFFF, 110), "rejects infinite canvas height");
        CheckRejected(check, new(10, 10, 1, new(float.NaN, 0, 2, 2), default, 20), new(0xFFFFFFFF, 110), "rejects NaN line geometry");
        CheckRejected(check, new(10, 10, 1, default, new(0, 0, 2, float.PositiveInfinity), 20), new(0xFFFFFFFF, 110), "rejects infinite second line geometry");
        CheckRejected(check, new(10, 10, 1, default, default, float.NaN), new(0xFFFFFFFF, 110), "rejects NaN feather");
        CheckRejected(check, new(10, 10, 1, new(9, 9, 2, 2), default, 20), new(0xFFFFFFFF, 110), "rejects platforms outside logical canvas");
        CheckRejected(check, new(10, 10, 1, default, default, 20), new(0x80FFFFFF, 110), "rejects non-opaque support colors");
    }

    static void CheckRejected(Action<bool, string> check, ReadingSupportMaskLayout layout,
        ReadingMaterialSupport material, string name)
    {
        bool rejected = false;
        try { _ = ReadingSupportMask.CreatePng(layout, material); }
        catch (ArgumentOutOfRangeException) { rejected = true; }
        check(rejected, name);
    }

    static void CheckRejectedLines(Action<bool, string> check, ReadingSupportMaskCanvas canvas,
        ReadOnlySpan<ReadingSupportMaskLine> lines, string name)
    {
        bool rejected = false;
        try { _ = ReadingSupportMask.CreateLinesPng(canvas, lines); }
        catch (ArgumentOutOfRangeException) { rejected = true; }
        check(rejected, name);
    }

    static byte ExpectedMaxAlpha(ReadingSupportMaskCanvas canvas, ReadOnlySpan<ReadingSupportMaskLine> lines,
        double x, double y)
    {
        byte alpha = 0;
        foreach (var line in lines)
        {
            if (line.Material.Opacity8Bit == 0 || line.Bounds.Width == 0 && line.Bounds.Height == 0) continue;
            double dx = x < line.Bounds.X ? line.Bounds.X - x
                : x > line.Bounds.Right ? x - line.Bounds.Right : 0;
            double dy = y < line.Bounds.Y ? line.Bounds.Y - y
                : y > line.Bounds.Bottom ? y - line.Bounds.Bottom : 0;
            double distance = Math.Sqrt(dx * dx + dy * dy);
            double coverage;
            if (distance <= 0) coverage = 1;
            else if (canvas.FeatherDip <= 0 || distance >= canvas.FeatherDip) coverage = 0;
            else
            {
                double t = distance / canvas.FeatherDip;
                coverage = 1 - t * t * (3 - 2 * t);
            }
            byte candidate = (byte)Math.Clamp((int)Math.Round(
                line.Material.Opacity8Bit * coverage, MidpointRounding.AwayFromZero), 0, 255);
            if (candidate > alpha) alpha = candidate;
        }
        return alpha;
    }

    static long AlphaSum(Decoded image)
    {
        long sum = 0;
        for (int i = 3; i < image.Rgba.Length; i += 4) sum += image.Rgba[i];
        return sum;
    }

    static bool Inside(double x, double y, FloatRect rect)
        => rect.Width > 0 && rect.Height > 0 && x >= rect.X && x <= (double)rect.X + rect.Width
            && y >= rect.Y && y <= (double)rect.Y + rect.Height;

    static PixelValue Pixel(Decoded image, int x, int y)
    {
        int offset = (y * image.Width + x) * 4;
        return new(image.Rgba[offset], image.Rgba[offset + 1], image.Rgba[offset + 2], image.Rgba[offset + 3]);
    }

    static Decoded Decode(byte[] png)
    {
        ReadOnlySpan<byte> signature = [137, 80, 78, 71, 13, 10, 26, 10];
        if (png.Length < signature.Length || !png.AsSpan(0, signature.Length).SequenceEqual(signature))
            throw new InvalidDataException("Bad PNG signature.");

        int width = 0, height = 0, bitDepth = 0, colorType = 0, position = signature.Length;
        byte[]? idat = null;
        while (position < png.Length)
        {
            if (png.Length - position < 12) throw new InvalidDataException("Truncated PNG chunk.");
            int length = checked((int)BinaryPrimitives.ReadUInt32BigEndian(png.AsSpan(position, 4)));
            int typeOffset = position + 4, dataOffset = position + 8, crcOffset = checked(dataOffset + length);
            if (length < 0 || crcOffset + 4 > png.Length) throw new InvalidDataException("PNG chunk out of bounds.");
            ReadOnlySpan<byte> type = png.AsSpan(typeOffset, 4), data = png.AsSpan(dataOffset, length);
            uint expected = BinaryPrimitives.ReadUInt32BigEndian(png.AsSpan(crcOffset, 4));
            if (TestCrc(type, data) != expected) throw new InvalidDataException("PNG CRC mismatch.");
            if (type.SequenceEqual("IHDR"u8))
            {
                if (length != 13) throw new InvalidDataException("Bad IHDR.");
                width = checked((int)BinaryPrimitives.ReadUInt32BigEndian(data));
                height = checked((int)BinaryPrimitives.ReadUInt32BigEndian(data[4..]));
                bitDepth = data[8]; colorType = data[9];
                if (data[10] != 0 || data[11] != 0 || data[12] != 0) throw new InvalidDataException("Unsupported PNG mode.");
            }
            else if (type.SequenceEqual("IDAT"u8))
            {
                if (idat is not null) throw new InvalidDataException("Expected one IDAT chunk.");
                idat = data.ToArray();
            }
            else if (type.SequenceEqual("IEND"u8))
            {
                if (length != 0 || crcOffset + 4 != png.Length) throw new InvalidDataException("Bad IEND or trailing bytes.");
                break;
            }
            position = crcOffset + 4;
        }
        if (width <= 0 || height <= 0 || idat is null || bitDepth != 8 || colorType != 6)
            throw new InvalidDataException("Unsupported or incomplete RGBA PNG.");

        byte[] raw;
        using (var input = new MemoryStream(idat, writable: false))
        using (var zlib = new ZLibStream(input, CompressionMode.Decompress))
        using (var decoded = new MemoryStream())
        {
            zlib.CopyTo(decoded);
            raw = decoded.ToArray();
        }
        int rowLength = checked(1 + width * 4);
        if (raw.Length != checked(rowLength * height)) throw new InvalidDataException("Unexpected decompressed PNG size.");
        byte[] rgba = new byte[checked(width * height * 4)];
        for (int y = 0; y < height; y++)
        {
            int source = y * rowLength;
            if (raw[source] != 0) throw new InvalidDataException("Only filter None is expected.");
            Buffer.BlockCopy(raw, source + 1, rgba, y * width * 4, width * 4);
        }
        return new(width, height, bitDepth, colorType, rgba);
    }

    static uint TestCrc(ReadOnlySpan<byte> first, ReadOnlySpan<byte> second)
    {
        uint crc = uint.MaxValue;
        foreach (byte value in first) crc = TestCrcStep(crc, value);
        foreach (byte value in second) crc = TestCrcStep(crc, value);
        return ~crc;
    }
    static uint TestCrcStep(uint crc, byte value)
    {
        crc ^= value;
        for (int bit = 0; bit < 8; bit++) crc = (crc & 1) != 0 ? 0xedb88320u ^ (crc >> 1) : crc >> 1;
        return crc;
    }

    readonly record struct PixelValue(byte Red, byte Green, byte Blue, byte Alpha);
    sealed record Decoded(int Width, int Height, int BitDepth, int ColorType, byte[] Rgba);
}

using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using ImageMagick;
using SkiaSharp;
using VarPlats.Web.Photos;
using Xunit;

namespace VarPlats.Tests;

public sealed class PhotoSanitizerTests
{
    private const int CellSize = 30;
    private const string GpsMarker = "synthetic-gps-marker";
    private const string XmpMarker = "synthetic-xmp-marker";

    [Theory]
    [InlineData(SKEncodedImageFormat.Jpeg, "image/jpeg")]
    [InlineData(SKEncodedImageFormat.Png, "image/png")]
    public void DetectsTheActualFormatAndEncodesNewPixels(SKEncodedImageFormat format, string contentType)
    {
        var photo = PhotoSanitizer.Sanitize(PatternImage(format));

        Assert.Equal(contentType, photo.ContentType);
        Assert.Equal(60, photo.Width);
        Assert.Equal(90, photo.Height);
        Assert.InRange(photo.Bytes.Length, 1, PhotoSanitizer.MaximumOutputBytes);
        using var encoded = SKData.CreateCopy(photo.Bytes.ToArray());
        using var codec = SKCodec.Create(encoded);
        Assert.NotNull(codec);
        Assert.Equal(format, codec.EncodedFormat);
        AssertPixels(photo, "RG/BY/CM");
    }

    [Fact]
    public void RejectsUnsupportedDamagedAndOverlongContainers()
    {
        Reject([], PhotoSanitizationError.InvalidImage);
        Reject("GIF89a"u8.ToArray(), PhotoSanitizationError.UnsupportedFormat);
        Reject(PatternImage(SKEncodedImageFormat.Webp), PhotoSanitizationError.UnsupportedFormat);

        var jpeg = PatternImage(SKEncodedImageFormat.Jpeg);
        Reject(jpeg[..^2], PhotoSanitizationError.InvalidImage);
        Reject([.. jpeg, 0], PhotoSanitizationError.InvalidImage);
        Reject(InsertJpegSegment(jpeg, 0xe2, "MPF\0"u8.ToArray()),
            PhotoSanitizationError.UnsupportedFormat);
        Reject(InsertJpegSegment(jpeg, 0xe1, "Exif\0\0II"u8.ToArray()),
            PhotoSanitizationError.InvalidImage);
        var exif = "Exif\0\0"u8.ToArray().Concat(SyntheticTiff(1)).ToArray();
        Reject(InsertJpegSegment(InsertJpegSegment(jpeg, 0xe1, exif), 0xe1, exif),
            PhotoSanitizationError.InvalidImage);

        var png = PatternImage(SKEncodedImageFormat.Png);
        Reject(png[..^1], PhotoSanitizationError.InvalidImage);
        var badCrc = (byte[])png.Clone();
        badCrc[badCrc.Length - 1] ^= 1;
        Reject(badCrc, PhotoSanitizationError.InvalidImage);
        var badCompression = (byte[])png.Clone();
        var idat = FindPngChunk(badCompression, "IDAT");
        badCompression[idat + 8] = 0;
        UpdateChunkCrc(badCompression, idat);
        Reject(badCompression, PhotoSanitizationError.InvalidImage);
        var badAdler = (byte[])png.Clone();
        var idatLength = (int)BinaryPrimitives.ReadUInt32BigEndian(badAdler.AsSpan(idat));
        badAdler[idat + 8 + idatLength - 1] ^= 1;
        UpdateChunkCrc(badAdler, idat);
        Reject(badAdler, PhotoSanitizationError.InvalidImage);
        byte[] shortIdat = [
            .. png[..idat],
            .. PngChunk("IDAT", png.AsSpan(idat + 8, idatLength - 4).ToArray()),
            .. png[(idat + idatLength + 12)..]
        ];
        Reject(shortIdat, PhotoSanitizationError.InvalidImage);
        byte[] trailingIdat = [
            .. png[..idat],
            .. PngChunk("IDAT", [.. png.AsSpan(idat + 8, idatLength), 0]),
            .. png[(idat + idatLength + 12)..]
        ];
        Reject(trailingIdat, PhotoSanitizationError.InvalidImage);

        using var filteredPixels = new MemoryStream();
        using (var originalData = new MemoryStream(png.AsSpan(idat + 8, idatLength).ToArray()))
        using (var zlib = new ZLibStream(originalData, CompressionMode.Decompress))
        {
            zlib.CopyTo(filteredPixels);
        }

        using var extraPixels = new MemoryStream();
        using (var zlib = new ZLibStream(extraPixels, CompressionLevel.SmallestSize, leaveOpen: true))
        {
            zlib.Write(filteredPixels.ToArray());
            zlib.WriteByte(0);
        }

        byte[] overlongPixels = [
            .. png[..idat],
            .. PngChunk("IDAT", extraPixels.ToArray()),
            .. png[(idat + idatLength + 12)..]
        ];
        Reject(overlongPixels, PhotoSanitizationError.InvalidImage);

        filteredPixels.Position = 0;
        filteredPixels.WriteByte(5);
        using var badFilter = new MemoryStream();
        using (var zlib = new ZLibStream(badFilter, CompressionLevel.SmallestSize, leaveOpen: true))
        {
            zlib.Write(filteredPixels.ToArray());
        }

        byte[] invalidFilter = [
            .. png[..idat],
            .. PngChunk("IDAT", badFilter.ToArray()),
            .. png[(idat + idatLength + 12)..]
        ];
        Reject(invalidFilter, PhotoSanitizationError.InvalidImage);
        Reject([.. png, 0], PhotoSanitizationError.InvalidImage);
        Reject([.. jpeg[..^10], 0xff, 0xd9], PhotoSanitizationError.InvalidImage);
    }

    [Fact]
    public void EnforcesByteAndDimensionLimitsBeforeDecoding()
    {
        var png = PatternImage(SKEncodedImageFormat.Png);
        var padding = new byte[PhotoSanitizer.MaximumInputBytes - png.Length - 12];
        padding[0] = (byte)'x';
        padding[1] = 0;
        padding.AsSpan(2).Fill((byte)'a');
        var atLimit = InsertPngChunk(png, "tEXt", padding, beforeIdat: true);
        Assert.Equal(PhotoSanitizer.MaximumInputBytes, atLimit.Length);
        Assert.Equal("image/png", PhotoSanitizer.Sanitize(atLimit).ContentType);
        Reject([.. atLimit, 0], PhotoSanitizationError.InputTooLarge);

        Reject(WithPngDimensions(png, 0, 1), PhotoSanitizationError.InvalidImage);
        Reject(WithPngDimensions(png, 4097, 1), PhotoSanitizationError.DimensionsTooLarge);
        Reject(WithPngDimensions(png, 4001, 4000), PhotoSanitizationError.DimensionsTooLarge);
        Reject(WithJpegDimensions(PatternImage(SKEncodedImageFormat.Jpeg), 4097, 1),
            PhotoSanitizationError.DimensionsTooLarge);

        using var wide = new SKBitmap(4096, 1);
        wide.Erase(SKColors.Red);
        using var encoded = wide.Encode(SKEncodedImageFormat.Png, 90);
        Assert.Equal(4096, PhotoSanitizer.Sanitize(encoded.ToArray()).Width);
    }

    [Fact]
    public void BoundsJpegScanCountBeforeDecoding()
    {
        var jpeg = PatternImage(SKEncodedImageFormat.Jpeg);
        using var manyScans = new MemoryStream();
        manyScans.Write(jpeg.AsSpan(0, jpeg.Length - 2));
        byte[] scanHeader = [0xff, 0xda, 0x00, 0x08, 0x01, 0x01, 0x00, 0x00, 0x3f, 0x00];
        for (var index = 0; index < 64; index++)
        {
            manyScans.Write(scanHeader);
        }

        manyScans.Write([0xff, 0xd9]);
        Reject(manyScans.ToArray(), PhotoSanitizationError.UnsupportedFormat);
    }

    [Fact]
    public void AcceptsProgressiveJpegAndRestoresDiskLimitAfterSuccessAndFailure()
    {
        var previousDiskLimit = ResourceLimits.Disk;
        using var image = new MagickImage(MagickColors.Red, 8, 8);
        image.Settings.Interlace = Interlace.Jpeg;
        var progressive = image.ToByteArray(MagickFormat.Jpeg);

        Assert.True(progressive.AsSpan().IndexOf(new byte[] { 0xff, 0xc2 }) >= 0);
        Assert.Equal("image/jpeg", PhotoSanitizer.Sanitize(progressive).ContentType);
        Assert.Equal(previousDiskLimit, ResourceLimits.Disk);

        var jpeg = PatternImage(SKEncodedImageFormat.Jpeg);
        Reject([.. jpeg[..^10], 0xff, 0xd9], PhotoSanitizationError.InvalidImage);
        Assert.Equal(previousDiskLimit, ResourceLimits.Disk);
    }

    [Fact]
    public void HandlesConcurrentJpegAndPngCalls()
    {
        var jpeg = PatternImage(SKEncodedImageFormat.Jpeg);
        var png = PatternImage(SKEncodedImageFormat.Png);
        var previousDiskLimit = ResourceLimits.Disk;

        Parallel.For(0, 8, index =>
        {
            var isJpeg = index % 2 == 0;
            var result = PhotoSanitizer.Sanitize(isJpeg ? jpeg : png);
            Assert.Equal(isJpeg ? "image/jpeg" : "image/png", result.ContentType);
            Assert.Equal(60, result.Width);
            Assert.Equal(90, result.Height);
        });

        Assert.Equal(previousDiskLimit, ResourceLimits.Disk);
    }

    [Theory]
    [InlineData(1, 1)]
    [InlineData(2, 3)]
    [InlineData(17, 9)]
    public void HandlesInterlacedPngWithSplitImageData(uint width, uint height)
    {
        using var image = new MagickImage(MagickColors.Red, width, height);
        image.Settings.Interlace = Interlace.Png;
        var png = image.ToByteArray(MagickFormat.Png);
        Assert.Equal(1, png[28]);

        var idat = FindPngChunk(png, "IDAT");
        var length = (int)BinaryPrimitives.ReadUInt32BigEndian(png.AsSpan(idat));
        byte[] split = [
            .. png[..idat],
            .. PngChunk("IDAT", png.AsSpan(idat + 8, 1).ToArray()),
            .. PngChunk("IDAT", png.AsSpan(idat + 9, length - 1).ToArray()),
            .. png[(idat + length + 12)..]
        ];
        var photo = PhotoSanitizer.Sanitize(split);
        Assert.Equal("image/png", photo.ContentType);
        Assert.Equal(width, (uint)photo.Width);
        Assert.Equal(height, (uint)photo.Height);
    }

    [Fact]
    public void RejectsAnimationAndExpandingColorProfiles()
    {
        Reject(AnimatedPng(), PhotoSanitizationError.AnimatedImage);

        using var compressed = new MemoryStream();
        using (var zlib = new ZLibStream(compressed, CompressionLevel.SmallestSize, leaveOpen: true))
        {
            zlib.Write(new byte[1024 * 1024 + 1]);
        }

        var profile = "synthetic\0\0"u8.ToArray().Concat(compressed.ToArray()).ToArray();
        var png = InsertPngChunk(PatternImage(SKEncodedImageFormat.Png), "iCCP", profile, beforeIdat: true);
        Reject(png, PhotoSanitizationError.ColorProfileTooLarge);
    }

    [Fact]
    public void RejectsMalformedPngColorMetadata()
    {
        var png = PatternImage(SKEncodedImageFormat.Png);
        using var compressed = new MemoryStream();
        using (var zlib = new ZLibStream(compressed, CompressionLevel.SmallestSize, leaveOpen: true))
        {
            zlib.Write("not-an-icc-profile"u8);
        }

        var profile = "synthetic\0\0"u8.ToArray().Concat(compressed.ToArray()).ToArray();
        Reject(InsertPngChunk(png, "iCCP", profile, beforeIdat: true),
            PhotoSanitizationError.InvalidImage);

        var duplicateGamma = InsertPngChunk(png, "gAMA", [0, 0, 0xb1, 0x8f], true);
        duplicateGamma = InsertPngChunk(duplicateGamma, "gAMA", [0, 0, 0xb1, 0x8f], true);
        Reject(duplicateGamma, PhotoSanitizationError.InvalidImage);
        Reject(InsertPngChunk(png, "gAMA", [1], true), PhotoSanitizationError.InvalidImage);
        Reject(InsertPngChunk(png, "tRNS", [1], true), PhotoSanitizationError.InvalidImage);
    }

    [Fact]
    public void HandlesValidPngColorProfileAndRejectsItsTruncation()
    {
        var png = PatternImage(SKEncodedImageFormat.Png);
        using var compressed = new MemoryStream();
        using (var zlib = new ZLibStream(compressed, CompressionLevel.SmallestSize, leaveOpen: true))
        {
            zlib.Write(ColorProfiles.SRGB.ToByteArray());
        }

        var data = compressed.ToArray();
        byte[] validProfile = [.. "synthetic\0\0"u8, .. data];
        Assert.Equal("image/png",
            PhotoSanitizer.Sanitize(InsertPngChunk(png, "iCCP", validProfile, true)).ContentType);

        byte[] truncatedProfile = [.. "synthetic\0\0"u8, .. data[..^1]];
        Reject(InsertPngChunk(png, "iCCP", truncatedProfile, true), PhotoSanitizationError.InvalidImage);

        byte[] trailingProfile = [.. "synthetic\0\0"u8, .. data, .. data[^4..]];
        Reject(InsertPngChunk(png, "iCCP", trailingProfile, true), PhotoSanitizationError.InvalidImage);
    }

    [Theory]
    [InlineData(1, "RG/BY/CM")]
    [InlineData(2, "GR/YB/MC")]
    [InlineData(3, "MC/YB/GR")]
    [InlineData(4, "CM/BY/RG")]
    [InlineData(5, "RBC/GYM")]
    [InlineData(6, "CBR/MYG")]
    [InlineData(7, "MYG/CBR")]
    [InlineData(8, "GYM/RBC")]
    public void AppliesJpegOrientationAndRemovesExifGpsAndXmp(ushort orientation, string expected)
    {
        var jpeg = PatternImage(SKEncodedImageFormat.Jpeg);
        var exif = "Exif\0\0"u8.ToArray().Concat(SyntheticTiff(orientation)).ToArray();
        jpeg = InsertJpegSegment(jpeg, 0xe1, exif);
        jpeg = InsertJpegSegment(jpeg, 0xe1,
            Encoding.ASCII.GetBytes("http://ns.adobe.com/xap/1.0/\0" + XmpMarker));

        var photo = PhotoSanitizer.Sanitize(jpeg);
        AssertPixels(photo, expected);
        AssertMetadataAbsent(photo);
        using var encoded = SKData.CreateCopy(photo.Bytes.ToArray());
        using var codec = SKCodec.Create(encoded);
        Assert.NotNull(codec);
        Assert.Equal(SKEncodedOrigin.TopLeft, codec.EncodedOrigin);
    }

    [Fact]
    public void AppliesJpegOrientationAfterImageData()
    {
        var jpeg = PatternImage(SKEncodedImageFormat.Jpeg);
        var segment = InsertJpegSegment(
            [0xff, 0xd8], 0xe1, [.. "Exif\0\0"u8, .. SyntheticTiff(6)])[2..];
        byte[] withExif = [.. jpeg[..^2], .. segment, 0xff, 0xd9];
        var photo = PhotoSanitizer.Sanitize(withExif);

        AssertPixels(photo, "CBR/MYG");
        AssertMetadataAbsent(photo);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void AppliesPngExifOnEitherSideOfPixelsAndRemovesMetadata(bool beforeIdat)
    {
        var png = PatternImage(SKEncodedImageFormat.Png);
        png = InsertPngChunk(png, "eXIf", SyntheticTiff(6), beforeIdat);
        png = InsertPngChunk(png, "tEXt", Encoding.ASCII.GetBytes("Comment\0" + XmpMarker), true);

        var photo = PhotoSanitizer.Sanitize(png);
        AssertPixels(photo, "CBR/MYG");
        AssertMetadataAbsent(photo);
        var output = Encoding.Latin1.GetString(photo.Bytes.Span);
        Assert.DoesNotContain("eXIf", output);
        Assert.DoesNotContain("tEXt", output);
    }

    [Fact]
    public void PreservesPngTransparency()
    {
        using var bitmap = new SKBitmap(2, 1);
        bitmap.SetPixel(0, 0, SKColors.Transparent);
        bitmap.SetPixel(1, 0, new SKColor(255, 0, 0, 128));
        using var encoded = bitmap.Encode(SKEncodedImageFormat.Png, 100);

        var photo = PhotoSanitizer.Sanitize(encoded.ToArray());
        using var decoded = SKBitmap.Decode(photo.Bytes.ToArray());
        Assert.Equal(0, decoded.GetPixel(0, 0).Alpha);
        Assert.InRange(decoded.GetPixel(1, 0).Alpha, 127, 129);
        Assert.InRange(decoded.GetPixel(1, 0).Red, 250, 255);
    }

    [Fact]
    public void PreservesIndexedPngTransparency()
    {
        byte[] header = [0, 0, 0, 2, 0, 0, 0, 1, 8, 3, 0, 0, 0];
        using var compressed = new MemoryStream();
        using (var zlib = new ZLibStream(compressed, CompressionLevel.SmallestSize, leaveOpen: true))
        {
            zlib.Write([0, 0, 1]);
        }

        byte[] png = [
            137, 80, 78, 71, 13, 10, 26, 10,
            .. PngChunk("IHDR", header),
            .. PngChunk("PLTE", [255, 0, 0, 0, 255, 0]),
            .. PngChunk("tRNS", [0, 128]),
            .. PngChunk("IDAT", compressed.ToArray()),
            .. PngChunk("IEND", [])
        ];
        var photo = PhotoSanitizer.Sanitize(png);
        using var decoded = SKBitmap.Decode(photo.Bytes.ToArray());
        Assert.Equal(0, decoded.GetPixel(0, 0).Alpha);
        Assert.InRange(decoded.GetPixel(1, 0).Alpha, 127, 129);
    }

    [Fact]
    public void RejectsAnOutputLargerThanTheBoundedEncoder()
    {
        var input = RandomPalettePng();
        Assert.True(input.Length <= PhotoSanitizer.MaximumInputBytes);
        Reject(input, PhotoSanitizationError.OutputTooLarge);
    }

    private static void Reject(byte[] input, PhotoSanitizationError reason)
    {
        var exception = Assert.Throws<PhotoSanitizationException>(() => PhotoSanitizer.Sanitize(input));
        Assert.Equal(reason, exception.Reason);
    }

    private static byte[] PatternImage(SKEncodedImageFormat format)
    {
        using var bitmap = new SKBitmap(2 * CellSize, 3 * CellSize);
        var colors = new[] { SKColors.Red, SKColors.Lime, SKColors.Blue, SKColors.Yellow, SKColors.Cyan, SKColors.Magenta };
        for (var index = 0; index < colors.Length; index++)
        {
            var x = index % 2 * CellSize;
            var y = index / 2 * CellSize;
            bitmap.Erase(colors[index], new SKRectI(x, y, x + CellSize, y + CellSize));
        }

        using var encoded = bitmap.Encode(format, 100);
        return encoded.ToArray();
    }

    private static void AssertPixels(SanitizedPhoto photo, string expected)
    {
        var rows = expected.Split('/');
        Assert.Equal(rows[0].Length * CellSize, photo.Width);
        Assert.Equal(rows.Length * CellSize, photo.Height);
        using var bitmap = SKBitmap.Decode(photo.Bytes.ToArray());
        Assert.NotNull(bitmap);
        for (var y = 0; y < rows.Length; y++)
        {
            for (var x = 0; x < rows[y].Length; x++)
            {
                var expectedColor = rows[y][x] switch
                {
                    'R' => SKColors.Red,
                    'G' => SKColors.Lime,
                    'B' => SKColors.Blue,
                    'Y' => SKColors.Yellow,
                    'C' => SKColors.Cyan,
                    'M' => SKColors.Magenta,
                    _ => throw new InvalidOperationException()
                };
                var actual = bitmap.GetPixel(x * CellSize + CellSize / 2, y * CellSize + CellSize / 2);
                Assert.InRange(Math.Abs(actual.Red - expectedColor.Red), 0, 30);
                Assert.InRange(Math.Abs(actual.Green - expectedColor.Green), 0, 30);
                Assert.InRange(Math.Abs(actual.Blue - expectedColor.Blue), 0, 30);
            }
        }
    }

    private static void AssertMetadataAbsent(SanitizedPhoto photo)
    {
        var contents = Encoding.Latin1.GetString(photo.Bytes.Span);
        Assert.DoesNotContain(GpsMarker, contents);
        Assert.DoesNotContain(XmpMarker, contents);
        Assert.DoesNotContain("Exif\0\0", contents);
    }

    private static byte[] SyntheticTiff(ushort orientation)
    {
        var marker = Encoding.ASCII.GetBytes(GpsMarker);
        var tiff = new byte[140 + marker.Length];
        "II"u8.CopyTo(tiff);
        BinaryPrimitives.WriteUInt16LittleEndian(tiff.AsSpan(2), 42);
        BinaryPrimitives.WriteUInt32LittleEndian(tiff.AsSpan(4), 8);
        BinaryPrimitives.WriteUInt16LittleEndian(tiff.AsSpan(8), 2);
        WriteTiffEntry(tiff.AsSpan(10), 0x0112, 3, 1, orientation);
        WriteTiffEntry(tiff.AsSpan(22), 0x8825, 4, 1, 38);
        BinaryPrimitives.WriteUInt16LittleEndian(tiff.AsSpan(38), 4);
        WriteTiffEntry(tiff.AsSpan(40), 1, 2, 2, 'N');
        WriteTiffEntry(tiff.AsSpan(52), 2, 5, 3, 92);
        WriteTiffEntry(tiff.AsSpan(64), 3, 2, 2, 'E');
        WriteTiffEntry(tiff.AsSpan(76), 4, 5, 3, 116);
        for (var index = 0; index < 6; index++)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(tiff.AsSpan(92 + index * 8 + 4), 1);
        }

        marker.CopyTo(tiff, 140);
        return tiff;
    }

    private static void WriteTiffEntry(Span<byte> entry, ushort tag, ushort type, uint count, uint value)
    {
        BinaryPrimitives.WriteUInt16LittleEndian(entry, tag);
        BinaryPrimitives.WriteUInt16LittleEndian(entry[2..], type);
        BinaryPrimitives.WriteUInt32LittleEndian(entry[4..], count);
        BinaryPrimitives.WriteUInt32LittleEndian(entry[8..], value);
    }

    private static byte[] InsertJpegSegment(byte[] jpeg, byte marker, byte[] payload)
    {
        var segment = new byte[payload.Length + 4];
        segment[0] = 0xff;
        segment[1] = marker;
        BinaryPrimitives.WriteUInt16BigEndian(segment.AsSpan(2), checked((ushort)(payload.Length + 2)));
        payload.CopyTo(segment, 4);
        return [.. jpeg[..2], .. segment, .. jpeg[2..]];
    }

    private static byte[] WithPngDimensions(byte[] original, uint width, uint height)
    {
        var result = (byte[])original.Clone();
        BinaryPrimitives.WriteUInt32BigEndian(result.AsSpan(16), width);
        BinaryPrimitives.WriteUInt32BigEndian(result.AsSpan(20), height);
        UpdateChunkCrc(result, 8);
        return result;
    }

    private static byte[] WithJpegDimensions(byte[] original, ushort width, ushort height)
    {
        var result = (byte[])original.Clone();
        for (var index = 2; index < result.Length - 9; index++)
        {
            if (result[index] == 0xff && result[index + 1] is 0xc0 or 0xc1 or 0xc2)
            {
                BinaryPrimitives.WriteUInt16BigEndian(result.AsSpan(index + 5), height);
                BinaryPrimitives.WriteUInt16BigEndian(result.AsSpan(index + 7), width);
                return result;
            }
        }

        throw new InvalidOperationException("Synthetic JPEG has no frame.");
    }

    private static byte[] InsertPngChunk(byte[] png, string type, byte[] payload, bool beforeIdat)
    {
        var position = FindPngChunk(png, beforeIdat ? "IDAT" : "IEND");
        return [.. png[..position], .. PngChunk(type, payload), .. png[position..]];
    }

    private static int FindPngChunk(byte[] png, string type)
    {
        var position = 8;
        while (position < png.Length)
        {
            var length = (int)BinaryPrimitives.ReadUInt32BigEndian(png.AsSpan(position));
            if (Encoding.ASCII.GetString(png, position + 4, 4) == type)
            {
                return position;
            }

            position += length + 12;
        }

        throw new InvalidOperationException("Synthetic PNG chunk not found.");
    }

    private static byte[] PngChunk(string type, byte[] payload)
    {
        var chunk = new byte[payload.Length + 12];
        BinaryPrimitives.WriteUInt32BigEndian(chunk, (uint)payload.Length);
        Encoding.ASCII.GetBytes(type).CopyTo(chunk, 4);
        payload.CopyTo(chunk, 8);
        UpdateChunkCrc(chunk, 0);
        return chunk;
    }

    private static void UpdateChunkCrc(byte[] png, int position)
    {
        var length = (int)BinaryPrimitives.ReadUInt32BigEndian(png.AsSpan(position));
        var crc = uint.MaxValue;
        foreach (var value in png.AsSpan(position + 4, length + 4))
        {
            crc ^= value;
            for (var bit = 0; bit < 8; bit++)
            {
                crc = (crc & 1) != 0 ? 0xedb88320 ^ (crc >> 1) : crc >> 1;
            }
        }

        BinaryPrimitives.WriteUInt32BigEndian(png.AsSpan(position + length + 8), ~crc);
    }

    private static byte[] AnimatedPng()
    {
        using var first = new SKBitmap(1, 1);
        first.Erase(SKColors.Red);
        using var firstEncoded = first.Encode(SKEncodedImageFormat.Png, 100);
        var png = firstEncoded.ToArray();
        var headerOffset = FindPngChunk(png, "IHDR");
        var idatOffset = FindPngChunk(png, "IDAT");
        var idatLength = (int)BinaryPrimitives.ReadUInt32BigEndian(png.AsSpan(idatOffset));
        var header = png.AsSpan(headerOffset + 8, 13).ToArray();
        var pixels = png.AsSpan(idatOffset + 8, idatLength).ToArray();

        var control = new byte[26];
        BinaryPrimitives.WriteUInt32BigEndian(control.AsSpan(4), 1);
        BinaryPrimitives.WriteUInt32BigEndian(control.AsSpan(8), 1);
        BinaryPrimitives.WriteUInt16BigEndian(control.AsSpan(20), 1);
        BinaryPrimitives.WriteUInt16BigEndian(control.AsSpan(22), 10);
        var secondControl = (byte[])control.Clone();
        BinaryPrimitives.WriteUInt32BigEndian(secondControl, 1);
        var secondFrame = new byte[pixels.Length + 4];
        BinaryPrimitives.WriteUInt32BigEndian(secondFrame, 2);
        pixels.CopyTo(secondFrame, 4);

        return [
            .. png[..8],
            .. PngChunk("IHDR", header),
            .. PngChunk("acTL", [0, 0, 0, 2, 0, 0, 0, 0]),
            .. PngChunk("fcTL", control),
            .. PngChunk("IDAT", pixels),
            .. PngChunk("fcTL", secondControl),
            .. PngChunk("fdAT", secondFrame),
            .. PngChunk("IEND", [])
        ];
    }

    private static byte[] RandomPalettePng()
    {
        const int dimension = 4000;
        var header = new byte[13];
        BinaryPrimitives.WriteUInt32BigEndian(header, dimension);
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(4), dimension);
        header[8] = 2;
        header[9] = 3;
        var palette = new byte[] { 255, 0, 0, 0, 255, 0, 0, 0, 255, 255, 255, 0 };
        using var compressed = new MemoryStream();
        using (var zlib = new ZLibStream(compressed, CompressionLevel.NoCompression, leaveOpen: true))
        {
            var row = new byte[dimension / 4 + 1];
            var random = new Random(12345);
            for (var y = 0; y < dimension; y++)
            {
                random.NextBytes(row.AsSpan(1));
                zlib.Write(row);
            }
        }

        return [
            137, 80, 78, 71, 13, 10, 26, 10,
            .. PngChunk("IHDR", header),
            .. PngChunk("PLTE", palette),
            .. PngChunk("IDAT", compressed.ToArray()),
            .. PngChunk("IEND", [])
        ];
    }
}

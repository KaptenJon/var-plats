using System.Buffers.Binary;
using System.IO.Compression;
using SkiaSharp;

namespace VarPlats.Web.Photos;

internal sealed record InspectedPhoto(
    byte[] Bytes, SKEncodedImageFormat Format, int Width, int Height, SKEncodedOrigin? Origin);

internal static class PhotoContainerInspector
{
    private const int MaximumColorProfileBytes = 1024 * 1024;
    private const int MaximumJpegScans = 64;
    private static readonly byte[] PngSignature = [137, 80, 78, 71, 13, 10, 26, 10];
    private static readonly uint[] CrcTable = CreateCrcTable();

    public static InspectedPhoto Inspect(byte[] bytes)
    {
        if (bytes.AsSpan().StartsWith(PngSignature))
        {
            return InspectPng(bytes);
        }

        if (bytes.Length >= 2 && bytes[0] == 0xff && bytes[1] == 0xd8)
        {
            return InspectJpeg(bytes);
        }

        throw PhotoSanitizer.Reject(PhotoSanitizationError.UnsupportedFormat);
    }

    private static InspectedPhoto InspectPng(byte[] bytes)
    {
        var data = bytes.AsSpan();
        using var filtered = new MemoryStream();
        using var imageData = new MemoryStream();
        filtered.Write(PngSignature);
        var position = PngSignature.Length;
        var width = 0;
        var height = 0;
        var bitDepth = 0;
        var colorType = 0;
        var bitsPerPixel = 0;
        var interlace = 0;
        var paletteEntries = 0;
        var headerSeen = false;
        var paletteSeen = false;
        var dataSeen = false;
        var dataEnded = false;
        var exifSeen = false;
        var profileSeen = false;
        var colorChunksSeen = new HashSet<uint>();
        SKEncodedOrigin? origin = null;

        while (position < data.Length)
        {
            if (data.Length - position < 12)
            {
                throw Invalid();
            }

            var length = BinaryPrimitives.ReadUInt32BigEndian(data[position..]);
            if (length > data.Length - position - 12)
            {
                throw Invalid();
            }

            var size = (int)length;
            var type = data.Slice(position + 4, 4);
            var payload = data.Slice(position + 8, size);
            var storedCrc = BinaryPrimitives.ReadUInt32BigEndian(data.Slice(position + 8 + size, 4));
            if (!ValidChunkType(type) || Crc32(data.Slice(position + 4, size + 4)) != storedCrc)
            {
                throw Invalid();
            }

            var next = position + size + 12;
            var keep = false;
            if (type.SequenceEqual("IHDR"u8))
            {
                if (headerSeen || position != PngSignature.Length || size != 13)
                {
                    throw Invalid();
                }

                var rawWidth = BinaryPrimitives.ReadUInt32BigEndian(payload);
                var rawHeight = BinaryPrimitives.ReadUInt32BigEndian(payload[4..]);
                PhotoSanitizer.ValidateDimensions(rawWidth, rawHeight);
                width = (int)rawWidth;
                height = (int)rawHeight;
                bitDepth = payload[8];
                colorType = payload[9];
                bitsPerPixel = PngBitsPerPixel(bitDepth, colorType);
                interlace = payload[12];
                if (payload[10] != 0 || payload[11] != 0 || interlace > 1)
                {
                    throw Invalid();
                }

                headerSeen = true;
                keep = true;
            }
            else if (!headerSeen)
            {
                throw Invalid();
            }
            else if (type.SequenceEqual("acTL"u8) || type.SequenceEqual("fcTL"u8) ||
                type.SequenceEqual("fdAT"u8))
            {
                throw PhotoSanitizer.Reject(PhotoSanitizationError.AnimatedImage);
            }
            else if (type.SequenceEqual("IDAT"u8))
            {
                if (dataEnded || (colorType == 3 && !paletteSeen))
                {
                    throw Invalid();
                }

                dataSeen = true;
                imageData.Write(payload);
                keep = true;
            }
            else
            {
                dataEnded |= dataSeen;
                if (type.SequenceEqual("IEND"u8))
                {
                    if (size != 0 || !dataSeen || next != data.Length)
                    {
                        throw Invalid();
                    }

                    filtered.Write(data.Slice(position, size + 12));
                    ValidatePngData(imageData.ToArray(), width, height, bitsPerPixel, interlace);
                    return new InspectedPhoto(filtered.ToArray(), SKEncodedImageFormat.Png,
                        width, height, origin);
                }

                if (type.SequenceEqual("PLTE"u8))
                {
                    if (paletteSeen || dataSeen ||
                        colorChunksSeen.Contains(BinaryPrimitives.ReadUInt32BigEndian("tRNS"u8)) ||
                        size == 0 || size > 768 || size % 3 != 0 ||
                        colorType is 0 or 4 || (colorType == 3 && size / 3 > (1 << bitDepth)))
                    {
                        throw Invalid();
                    }

                    paletteSeen = true;
                    paletteEntries = size / 3;
                    keep = true;
                }
                else if (type.SequenceEqual("eXIf"u8))
                {
                    if (exifSeen)
                    {
                        throw Invalid();
                    }

                    exifSeen = true;
                    origin = ReadExifOrientation(payload);
                }
                else if (type.SequenceEqual("iCCP"u8))
                {
                    if (profileSeen || paletteSeen || dataSeen)
                    {
                        throw Invalid();
                    }

                    profileSeen = true;
                    ValidateColorProfile(payload, colorType);
                    keep = true;
                }
                else if (type.SequenceEqual("tRNS"u8) || type.SequenceEqual("gAMA"u8) ||
                    type.SequenceEqual("cHRM"u8) || type.SequenceEqual("sRGB"u8) ||
                    type.SequenceEqual("sBIT"u8) || type.SequenceEqual("cICP"u8) ||
                    type.SequenceEqual("mDCv"u8) || type.SequenceEqual("cLLi"u8))
                {
                    if (dataSeen || !colorChunksSeen.Add(BinaryPrimitives.ReadUInt32BigEndian(type)) ||
                        (paletteSeen && (type.SequenceEqual("gAMA"u8) ||
                            type.SequenceEqual("cHRM"u8) || type.SequenceEqual("sRGB"u8) ||
                            type.SequenceEqual("sBIT"u8) || type.SequenceEqual("cICP"u8))))
                    {
                        throw Invalid();
                    }

                    ValidatePngColorChunk(type, payload, bitDepth, colorType, paletteEntries);
                    keep = true;
                }
                else if (type[0] is >= (byte)'A' and <= (byte)'Z')
                {
                    throw Invalid();
                }
            }

            // Text and unknown ancillary chunks are not passed to the native decoder.
            if (keep)
            {
                filtered.Write(data.Slice(position, size + 12));
            }

            position = next;
        }

        throw Invalid();
    }

    private static InspectedPhoto InspectJpeg(byte[] bytes)
    {
        var data = bytes.AsSpan();
        var position = 2;
        var width = 0;
        var height = 0;
        var frameSeen = false;
        var scanSeen = false;
        var inScan = false;
        var scanCount = 0;
        var exifSeen = false;
        SKEncodedOrigin? origin = null;

        while (position < data.Length)
        {
            if (inScan)
            {
                while (position < data.Length && data[position] != 0xff)
                {
                    position++;
                }
            }

            if (position >= data.Length || data[position] != 0xff)
            {
                throw Invalid();
            }

            while (position < data.Length && data[position] == 0xff)
            {
                position++;
            }

            if (position >= data.Length)
            {
                throw Invalid();
            }

            var marker = data[position++];
            if (inScan && (marker == 0 || marker is >= 0xd0 and <= 0xd7))
            {
                continue;
            }

            if (marker == 0xd9)
            {
                if (!frameSeen || !scanSeen || position != data.Length)
                {
                    throw Invalid();
                }

                return new InspectedPhoto(bytes, SKEncodedImageFormat.Jpeg, width, height, origin);
            }

            if (marker == 0xd8 || marker == 0 || marker is >= 0xd0 and <= 0xd7)
            {
                throw Invalid();
            }

            if (marker == 0x01)
            {
                continue;
            }

            inScan = false;
            if (data.Length - position < 2)
            {
                throw Invalid();
            }

            var segmentLength = BinaryPrimitives.ReadUInt16BigEndian(data[position..]);
            if (segmentLength < 2 || segmentLength > data.Length - position)
            {
                throw Invalid();
            }

            var payload = data.Slice(position + 2, segmentLength - 2);
            if (marker == 0xe2 && payload.StartsWith("MPF\0"u8))
            {
                throw PhotoSanitizer.Reject(PhotoSanitizationError.UnsupportedFormat);
            }

            if (marker == 0xe1 && payload.StartsWith("Exif\0\0"u8))
            {
                if (exifSeen)
                {
                    throw Invalid();
                }

                exifSeen = true;
                origin = ReadExifOrientation(payload[6..]);
            }

            if (IsStartOfFrame(marker))
            {
                if (frameSeen || payload.Length < 6)
                {
                    throw Invalid();
                }

                var rawHeight = BinaryPrimitives.ReadUInt16BigEndian(payload[1..]);
                var rawWidth = BinaryPrimitives.ReadUInt16BigEndian(payload[3..]);
                PhotoSanitizer.ValidateDimensions(rawWidth, rawHeight);
                width = rawWidth;
                height = rawHeight;
                frameSeen = true;
            }
            else if (marker == 0xda)
            {
                if (!frameSeen)
                {
                    throw Invalid();
                }

                if (++scanCount > MaximumJpegScans)
                {
                    throw PhotoSanitizer.Reject(PhotoSanitizationError.UnsupportedFormat);
                }

                scanSeen = true;
                inScan = true;
            }

            position += segmentLength;
        }

        throw Invalid();
    }

    private static bool IsStartOfFrame(byte marker) =>
        marker is >= 0xc0 and <= 0xcf and not (0xc4 or 0xc8 or 0xcc);

    private static SKEncodedOrigin ReadExifOrientation(ReadOnlySpan<byte> tiff)
    {
        if (tiff.Length < 8)
        {
            throw Invalid();
        }

        var littleEndian = tiff[..2].SequenceEqual("II"u8);
        if ((!littleEndian && !tiff[..2].SequenceEqual("MM"u8)) ||
            ReadUInt16(tiff[2..], littleEndian) != 42)
        {
            throw Invalid();
        }

        var offset = ReadUInt32(tiff[4..], littleEndian);
        if (offset < 8 || offset > tiff.Length - 2)
        {
            throw Invalid();
        }

        var entries = ReadUInt16(tiff[(int)offset..], littleEndian);
        if ((long)offset + 2 + (long)entries * 12 + 4 > tiff.Length)
        {
            throw Invalid();
        }

        ushort? orientation = null;
        for (var index = 0; index < entries; index++)
        {
            var entry = tiff.Slice((int)offset + 2 + index * 12, 12);
            if (ReadUInt16(entry, littleEndian) != 0x0112)
            {
                continue;
            }

            if (orientation.HasValue || ReadUInt16(entry[2..], littleEndian) != 3 ||
                ReadUInt32(entry[4..], littleEndian) != 1)
            {
                throw Invalid();
            }

            orientation = ReadUInt16(entry[8..], littleEndian);
            if (orientation is < 1 or > 8)
            {
                throw Invalid();
            }
        }

        return (SKEncodedOrigin)(orientation ?? 1);
    }

    private static ushort ReadUInt16(ReadOnlySpan<byte> value, bool littleEndian) => littleEndian
        ? BinaryPrimitives.ReadUInt16LittleEndian(value)
        : BinaryPrimitives.ReadUInt16BigEndian(value);

    private static uint ReadUInt32(ReadOnlySpan<byte> value, bool littleEndian) => littleEndian
        ? BinaryPrimitives.ReadUInt32LittleEndian(value)
        : BinaryPrimitives.ReadUInt32BigEndian(value);

    private static int PngBitsPerPixel(int bitDepth, int colorType)
    {
        var channels = colorType switch
        {
            0 when bitDepth is 1 or 2 or 4 or 8 or 16 => 1,
            2 when bitDepth is 8 or 16 => 3,
            3 when bitDepth is 1 or 2 or 4 or 8 => 1,
            4 when bitDepth is 8 or 16 => 2,
            6 when bitDepth is 8 or 16 => 4,
            _ => throw Invalid()
        };
        return channels * bitDepth;
    }

    private static void ValidatePngColorChunk(
        ReadOnlySpan<byte> type, ReadOnlySpan<byte> payload, int bitDepth, int colorType, int paletteEntries)
    {
        if (type.SequenceEqual("tRNS"u8))
        {
            var sampleCount = colorType switch
            {
                0 => 1,
                2 => 3,
                3 when paletteEntries > 0 && payload.Length > 0 && payload.Length <= paletteEntries => 0,
                _ => throw Invalid()
            };
            if (sampleCount != 0)
            {
                if (payload.Length != sampleCount * 2)
                {
                    throw Invalid();
                }

                for (var index = 0; index < sampleCount; index++)
                {
                    if (BinaryPrimitives.ReadUInt16BigEndian(payload[(index * 2)..]) >= (1 << bitDepth))
                    {
                        throw Invalid();
                    }
                }
            }
        }
        else if (type.SequenceEqual("gAMA"u8))
        {
            if (payload.Length != 4 || BinaryPrimitives.ReadUInt32BigEndian(payload) == 0)
            {
                throw Invalid();
            }
        }
        else if ((type.SequenceEqual("cHRM"u8) && payload.Length != 32) ||
            (type.SequenceEqual("sRGB"u8) && (payload.Length != 1 || payload[0] > 3)) ||
            (type.SequenceEqual("cICP"u8) && payload.Length != 4))
        {
            throw Invalid();
        }
        else if (type.SequenceEqual("sBIT"u8))
        {
            var expected = colorType switch { 0 => 1, 2 or 3 => 3, 4 => 2, 6 => 4, _ => 0 };
            var maximum = colorType == 3 ? 8 : bitDepth;
            if (payload.Length != expected)
            {
                throw Invalid();
            }

            foreach (var value in payload)
            {
                if (value == 0 || value > maximum)
                {
                    throw Invalid();
                }
            }
        }
    }

    private static void ValidatePngData(byte[] data, int width, int height, int bitsPerPixel, int interlace)
    {
        var expected = PngScanlineBytes(width, height, bitsPerPixel, interlace);
        try
        {
            using var compressed = new CheckedZlibInput(data);
            using var zlib = new ZLibStream(compressed, CompressionMode.Decompress);
            Span<byte> buffer = stackalloc byte[8192];
            long total = 0;
            int read;
            while ((read = zlib.Read(buffer)) != 0)
            {
                total += read;
                if (total > expected)
                {
                    throw Invalid();
                }
            }

            if (total != expected)
            {
                throw Invalid();
            }

            ValidateZlibEnd(compressed);
        }
        catch (InvalidDataException)
        {
            throw Invalid();
        }
    }

    private static long PngScanlineBytes(int width, int height, int bitsPerPixel, int interlace)
    {
        if (interlace == 0)
        {
            return (((long)width * bitsPerPixel + 7) / 8 + 1) * height;
        }

        ReadOnlySpan<int> startX = [0, 4, 0, 2, 0, 1, 0];
        ReadOnlySpan<int> startY = [0, 0, 4, 0, 2, 0, 1];
        ReadOnlySpan<int> stepX = [8, 8, 4, 4, 2, 2, 1];
        ReadOnlySpan<int> stepY = [8, 8, 8, 4, 4, 2, 2];
        long total = 0;
        for (var pass = 0; pass < 7; pass++)
        {
            if (width <= startX[pass] || height <= startY[pass])
            {
                continue;
            }

            var columns = (width - startX[pass] + stepX[pass] - 1) / stepX[pass];
            var rows = (height - startY[pass] + stepY[pass] - 1) / stepY[pass];
            total += (((long)columns * bitsPerPixel + 7) / 8 + 1) * rows;
        }

        return total;
    }

    private static void ValidateColorProfile(ReadOnlySpan<byte> payload, int colorType)
    {
        var separator = payload.IndexOf((byte)0);
        if (separator is < 1 or > 79 || separator + 2 >= payload.Length || payload[separator + 1] != 0)
        {
            throw Invalid();
        }

        try
        {
            using var compressed = new CheckedZlibInput(payload[(separator + 2)..].ToArray());
            using var zlib = new ZLibStream(compressed, CompressionMode.Decompress);
            using var profileBytes = new MemoryStream();
            Span<byte> buffer = stackalloc byte[8192];
            var total = 0;
            int read;
            while ((read = zlib.Read(buffer)) != 0)
            {
                total += read;
                if (total > MaximumColorProfileBytes)
                {
                    throw PhotoSanitizer.Reject(PhotoSanitizationError.ColorProfileTooLarge);
                }

                profileBytes.Write(buffer[..read]);
            }

            ValidateZlibEnd(compressed);

            var profileData = profileBytes.GetBuffer().AsSpan(0, total);
            if (total < 128 || !profileData.Slice(16, 4).SequenceEqual(
                colorType is 0 or 4 ? "GRAY"u8 : "RGB "u8))
            {
                throw Invalid();
            }

            using var profile = SKColorSpaceIccProfile.Create(profileData);
            if (profile is null)
            {
                throw Invalid();
            }
        }
        catch (InvalidDataException)
        {
            throw Invalid();
        }
    }

    private static void ValidateZlibEnd(CheckedZlibInput compressed)
    {
        if (compressed.ReadPastEnd || compressed.Position != compressed.Length)
        {
            throw Invalid();
        }
    }

    private static bool ValidChunkType(ReadOnlySpan<byte> type)
    {
        foreach (var value in type)
        {
            if (value is not (>= (byte)'A' and <= (byte)'Z') and
                not (>= (byte)'a' and <= (byte)'z'))
            {
                return false;
            }
        }

        return type[2] is >= (byte)'A' and <= (byte)'Z';
    }

    private static uint Crc32(ReadOnlySpan<byte> bytes)
    {
        var crc = uint.MaxValue;
        foreach (var value in bytes)
        {
            crc = CrcTable[(int)((crc ^ value) & 0xff)] ^ (crc >> 8);
        }

        return ~crc;
    }

    private static uint[] CreateCrcTable()
    {
        var table = new uint[256];
        for (uint index = 0; index < table.Length; index++)
        {
            var value = index;
            for (var bit = 0; bit < 8; bit++)
            {
                value = (value & 1) != 0 ? 0xedb88320 ^ (value >> 1) : value >> 1;
            }

            table[index] = value;
        }

        return table;
    }

    private sealed class CheckedZlibInput(byte[] bytes) : MemoryStream(bytes, writable: false)
    {
        public bool ReadPastEnd { get; private set; }

        public override int Read(byte[] buffer, int offset, int count)
        {
            if (count == 0)
            {
                return 0;
            }

            var remaining = Length - Position;
            if (remaining == 0)
            {
                ReadPastEnd = true;
                return 0;
            }

            // Hold back the final byte so inflater read-ahead cannot hide a trailing suffix.
            var available = remaining > 1 ? remaining - 1 : 1;
            return base.Read(buffer, offset, (int)Math.Min(count, available));
        }
    }

    private static PhotoSanitizationException Invalid() =>
        PhotoSanitizer.Reject(PhotoSanitizationError.InvalidImage);
}

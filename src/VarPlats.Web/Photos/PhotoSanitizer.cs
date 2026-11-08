using SkiaSharp;

namespace VarPlats.Web.Photos;

/// <summary>Sanerar en redan storleksbegränsad bild i minnet, utan att publicera eller lagra den.</summary>
/// <remarks>
/// Anroparen måste begränsa uppladdningen innan den läses in och begränsa samtidiga anrop.
/// Synliga personer och annan känslig information upptäcks inte. Moderering och skadlig kod-
/// skanning behövs fortfarande, liksom isolering och en uppdaterad native-avkodare.
/// JPEG-kontrollen ändrar tillfälligt ImageMagicks processglobala diskcachegräns; samordna
/// annan ImageMagick-användning i samma process.
/// </remarks>
public static class PhotoSanitizer
{
    public const int MaximumInputBytes = 5 * 1024 * 1024;
    public const int MaximumOutputBytes = 5 * 1024 * 1024;
    public const int MaximumDimension = 4096;
    public const int MaximumPixels = 16_000_000;

    /// <summary>
    /// Identifierar JPEG eller PNG från bytes, validerar och ritar om en stillbild till sRGB.
    /// Metadata från indata följer inte med; resultatet är högst 5 MiB. JPEG får ha högst
    /// 64 skanningar och en expanderad PNG-färgprofil högst 1 MiB.
    /// </summary>
    /// <exception cref="PhotoSanitizationException">Indata eller resultatet avvisades.</exception>
    /// <exception cref="InvalidOperationException">Validering, allokering eller omkodning misslyckades.</exception>
    /// <remarks>
    /// JPEG-varningar avvisas konservativt. Alla skador kan inte upptäckas i format utan
    /// fullständig kontrollsumma. Plattforms-, native-biblioteks- och resursfel kan propageras.
    /// </remarks>
    public static SanitizedPhoto Sanitize(ReadOnlyMemory<byte> input)
    {
        if (input.Length > MaximumInputBytes)
        {
            throw Reject(PhotoSanitizationError.InputTooLarge);
        }

        if (input.IsEmpty)
        {
            throw Reject(PhotoSanitizationError.InvalidImage);
        }

        // Work from a private snapshot so validation and decoding see the same bytes.
        var inspected = PhotoContainerInspector.Inspect(input.ToArray());
        if (inspected.Format == SKEncodedImageFormat.Jpeg)
        {
            StrictJpegValidator.Validate(inspected.Bytes);
        }

        using var encoded = SKData.CreateCopy(inspected.Bytes);
        using var codec = SKCodec.Create(encoded);
        if (codec is null || codec.EncodedFormat != inspected.Format)
        {
            throw Reject(PhotoSanitizationError.InvalidImage);
        }

        if (codec.FrameCount > 1)
        {
            throw Reject(PhotoSanitizationError.AnimatedImage);
        }

        var sourceInfo = codec.Info;
        if (sourceInfo.Width != inspected.Width || sourceInfo.Height != inspected.Height)
        {
            throw Reject(PhotoSanitizationError.InvalidImage);
        }

        var origin = inspected.Origin ?? codec.EncodedOrigin;
        var swapsDimensions = origin is SKEncodedOrigin.LeftTop or SKEncodedOrigin.RightTop
            or SKEncodedOrigin.RightBottom or SKEncodedOrigin.LeftBottom;
        var width = swapsDimensions ? sourceInfo.Height : sourceInfo.Width;
        var height = swapsDimensions ? sourceInfo.Width : sourceInfo.Height;

        using var colorSpace = SKColorSpace.CreateSrgb();
        var decodeInfo = new SKImageInfo(sourceInfo.Width, sourceInfo.Height,
            SKColorType.Rgba8888, SKAlphaType.Premul, colorSpace);
        using var decoded = new SKBitmap();
        if (!decoded.TryAllocPixels(decodeInfo))
        {
            throw new InvalidOperationException("Bildens pixlar kunde inte allokeras.");
        }

        if (codec.GetPixels(decodeInfo, decoded.GetPixels()) != SKCodecResult.Success)
        {
            throw Reject(PhotoSanitizationError.InvalidImage);
        }

        var renderInfo = new SKImageInfo(width, height, SKColorType.Rgba8888,
            SKAlphaType.Premul, colorSpace);
        using var rendered = new SKBitmap();
        if (!rendered.TryAllocPixels(renderInfo))
        {
            throw new InvalidOperationException("Den omritade bilden kunde inte allokeras.");
        }

        using (var canvas = new SKCanvas(rendered))
        {
            canvas.Clear(SKColors.Transparent);
            canvas.SetMatrix(OrientationMatrix(origin, sourceInfo.Width, sourceInfo.Height));
            canvas.DrawBitmap(decoded, 0, 0, new SKSamplingOptions(SKFilterMode.Nearest));
        }

        using var output = new BoundedPhotoStream();
        var success = rendered.Encode(output, inspected.Format, 90);
        if (output.WriteError is { } writeError)
        {
            throw new InvalidOperationException("Bilden kunde inte skrivas i minnet.", writeError);
        }

        if (output.LimitExceeded)
        {
            throw Reject(PhotoSanitizationError.OutputTooLarge);
        }

        if (!success)
        {
            throw new InvalidOperationException("Bilden kunde inte kodas om.");
        }

        var contentType = inspected.Format == SKEncodedImageFormat.Jpeg ? "image/jpeg" : "image/png";
        return new SanitizedPhoto(output.ToArray(), contentType, width, height);
    }

    internal static PhotoSanitizationException Reject(PhotoSanitizationError reason) => new(reason);

    internal static void ValidateDimensions(uint width, uint height)
    {
        if (width == 0 || height == 0)
        {
            throw Reject(PhotoSanitizationError.InvalidImage);
        }

        if (width > MaximumDimension || height > MaximumDimension ||
            (long)width * height > MaximumPixels)
        {
            throw Reject(PhotoSanitizationError.DimensionsTooLarge);
        }
    }

    private static SKMatrix OrientationMatrix(SKEncodedOrigin origin, int width, int height) => origin switch
    {
        SKEncodedOrigin.TopLeft => SKMatrix.Identity,
        SKEncodedOrigin.TopRight => new(-1, 0, width, 0, 1, 0, 0, 0, 1),
        SKEncodedOrigin.BottomRight => new(-1, 0, width, 0, -1, height, 0, 0, 1),
        SKEncodedOrigin.BottomLeft => new(1, 0, 0, 0, -1, height, 0, 0, 1),
        SKEncodedOrigin.LeftTop => new(0, 1, 0, 1, 0, 0, 0, 0, 1),
        SKEncodedOrigin.RightTop => new(0, -1, height, 1, 0, 0, 0, 0, 1),
        SKEncodedOrigin.RightBottom => new(0, -1, height, -1, 0, width, 0, 0, 1),
        SKEncodedOrigin.LeftBottom => new(0, 1, 0, -1, 0, width, 0, 0, 1),
        _ => throw Reject(PhotoSanitizationError.InvalidImage)
    };
}

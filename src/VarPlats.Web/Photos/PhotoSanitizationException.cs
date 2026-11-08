namespace VarPlats.Web.Photos;

public enum PhotoSanitizationError
{
    InputTooLarge,
    UnsupportedFormat,
    InvalidImage,
    AnimatedImage,
    DimensionsTooLarge,
    ColorProfileTooLarge,
    OutputTooLarge
}

/// <summary>Indata avvisades; <see cref="Reason"/> anger orsaken utan att innehålla bilddata.</summary>
public sealed class PhotoSanitizationException : Exception
{
    internal PhotoSanitizationException(PhotoSanitizationError reason)
        : base(reason switch
        {
            PhotoSanitizationError.InputTooLarge => "Bilden är större än den tillåtna indatafilen.",
            PhotoSanitizationError.UnsupportedFormat => "Bildformatet stöds inte.",
            PhotoSanitizationError.InvalidImage => "Bilden är ogiltig eller ofullständig.",
            PhotoSanitizationError.AnimatedImage => "Animerade bilder stöds inte.",
            PhotoSanitizationError.DimensionsTooLarge => "Bildens dimensioner är för stora.",
            PhotoSanitizationError.ColorProfileTooLarge => "Bildens färgprofil är för stor.",
            PhotoSanitizationError.OutputTooLarge => "Den omkodade bilden är för stor.",
            _ => "Bilden kunde inte saneras."
        })
    {
        Reason = reason;
    }

    public PhotoSanitizationError Reason { get; }
}

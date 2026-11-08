namespace VarPlats.Web.Photos;

/// <summary>Nyomkodade bildbytes och deras faktiska innehållstyp och orienterade dimensioner.</summary>
public sealed class SanitizedPhoto
{
    internal SanitizedPhoto(byte[] bytes, string contentType, int width, int height)
    {
        Bytes = bytes;
        ContentType = contentType;
        Width = width;
        Height = height;
    }

    public ReadOnlyMemory<byte> Bytes { get; }
    public string ContentType { get; }
    public int Width { get; }
    public int Height { get; }
}

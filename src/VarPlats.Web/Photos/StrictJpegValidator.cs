using ImageMagick;
using ImageMagick.Formats;

namespace VarPlats.Web.Photos;

internal static class StrictJpegValidator
{
    private static readonly object CacheLimitLock = new();

    public static void Validate(byte[] bytes)
    {
        lock (CacheLimitLock)
        {
            var previousDiskLimit = ResourceLimits.Disk;
            try
            {
                ResourceLimits.Disk = 0;
                if (ResourceLimits.Disk != 0)
                {
                    throw new InvalidOperationException("Bildvalideringens diskcache kunde inte spärras.");
                }

                var warning = false;
                using var image = new MagickImage();
                image.Warning += (_, _) => warning = true;
                var settings = new MagickReadSettings(new JpegReadDefines
                {
                    Size = new MagickGeometry(1, 1),
                    SkipProfiles = JpegProfileTypes.App | JpegProfileTypes.EightBim |
                        JpegProfileTypes.Exif | JpegProfileTypes.Icc |
                        JpegProfileTypes.Iptc | JpegProfileTypes.Xmp
                })
                {
                    Format = MagickFormat.Jpeg
                };
                image.Read(bytes, settings);
                if (warning)
                {
                    throw PhotoSanitizer.Reject(PhotoSanitizationError.InvalidImage);
                }
            }
            catch (MagickResourceLimitErrorException)
            {
                throw new InvalidOperationException("Resurserna räckte inte för bildvalideringen.");
            }
            catch (MagickException)
            {
                throw PhotoSanitizer.Reject(PhotoSanitizationError.InvalidImage);
            }
            finally
            {
                ResourceLimits.Disk = previousDiskLimit;
            }
        }
    }
}

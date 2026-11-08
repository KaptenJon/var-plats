using System.Runtime.InteropServices;
using SkiaSharp;

namespace VarPlats.Web.Photos;

internal sealed class BoundedPhotoStream : SKAbstractManagedWStream
{
    private readonly MemoryStream _buffer = new();

    public bool LimitExceeded { get; private set; }
    public Exception? WriteError { get; private set; }

    public byte[] ToArray() => _buffer.ToArray();

    protected override bool OnWrite(IntPtr source, IntPtr size)
    {
        try
        {
            var count = size.ToInt64();
            if (count < 0 || count > PhotoSanitizer.MaximumOutputBytes - _buffer.Length)
            {
                LimitExceeded = true;
                return false;
            }

            if (count == 0)
            {
                return true;
            }

            if (source == IntPtr.Zero)
            {
                return false;
            }

            var start = (int)_buffer.Length;
            var end = start + (int)count;
            if (end > _buffer.Capacity)
            {
                _buffer.Capacity = Math.Min(PhotoSanitizer.MaximumOutputBytes,
                    Math.Max(end, Math.Max(256, _buffer.Capacity * 2)));
            }

            _buffer.SetLength(end);
            Marshal.Copy(source, _buffer.GetBuffer(), start, (int)count);
            _buffer.Position = end;
            return true;
        }
        catch (Exception error)
        {
            // Managed exceptions must not escape through a native encoder callback.
            WriteError = error;
            return false;
        }
    }

    protected override void OnFlush()
    {
    }

    protected override IntPtr OnBytesWritten() => (IntPtr)_buffer.Length;

    protected override void DisposeManaged()
    {
        _buffer.Dispose();
        base.DisposeManaged();
    }
}

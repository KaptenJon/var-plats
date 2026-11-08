using System.Buffers.Binary;
using System.Net.Sockets;

namespace VarPlats.Web.Photos;

public sealed class ClamAvUnixScanner(PhotoOptions options) : IPhotoMalwareScanner
{
    public async Task<bool> IsCleanAsync(ReadOnlyMemory<byte> bytes, CancellationToken cancellationToken)
    {
        var parent = Path.GetDirectoryName(options.ScannerSocketPath)
            ?? throw new IOException("Skannerns privata katalog saknas.");
        using var directory = LinuxPrivateDirectory.Open(parent);
        directory.RequirePrivateSocket(Path.GetFileName(options.ScannerSocketPath));
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(options.ScannerTimeoutSeconds));
        using var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        await socket.ConnectAsync(new UnixDomainSocketEndPoint(options.ScannerSocketPath), timeout.Token);
        await using var stream = new NetworkStream(socket, ownsSocket: false);
        await stream.WriteAsync("zINSTREAM\0"u8.ToArray(), timeout.Token);

        var length = new byte[4];
        for (var offset = 0; offset < bytes.Length;)
        {
            var count = Math.Min(64 * 1024, bytes.Length - offset);
            BinaryPrimitives.WriteUInt32BigEndian(length, (uint)count);
            await stream.WriteAsync(length, timeout.Token);
            await stream.WriteAsync(bytes.Slice(offset, count), timeout.Token);
            offset += count;
        }
        Array.Clear(length);
        await stream.WriteAsync(length, timeout.Token);

        var response = new byte[256];
        var used = 0;
        while (used < response.Length)
        {
            var count = await stream.ReadAsync(response.AsMemory(used), timeout.Token);
            if (count == 0) return false;
            var end = Array.IndexOf(response, (byte)0, used, count);
            if (end >= 0)
                return response.AsSpan(0, end).SequenceEqual("stream: OK"u8);
            used += count;
        }
        return false;
    }
}

using System.Buffers.Binary;
using System.Net.Sockets;
using System.Text;
using VarPlats.Web.Photos;
using Xunit;

namespace VarPlats.Tests;

public sealed class LinuxPhotoInfrastructureTests
{
    [LinuxOnlyFact]
    public async Task PrivateStorageUsesExclusiveAtomicFilesAndRefusesSymlinks()
    {
        if (!OperatingSystem.IsLinux()) return;
        var directory = CreatePrivateDirectory();
        var link = Path.GetFullPath(Path.Combine("artifacts", $"l-{Guid.NewGuid():N}"));
        try
        {
            Directory.CreateSymbolicLink(link, directory);
            Assert.Throws<IOException>(() => new LinuxPhotoStorage(new PhotoOptions { StoragePath = link }));

            using var storage = new LinuxPhotoStorage(new PhotoOptions { StoragePath = directory });
            var name = Guid.NewGuid().ToString("N") + ".png";
            var original = "synthetic photo bytes"u8.ToArray();
            await storage.WriteAsync(name, original, CancellationToken.None);
            Assert.Equal(original, await storage.ReadAsync(name, original.Length, CancellationToken.None));
            Assert.True(storage.AvailableBytes() > 0);
            await Assert.ThrowsAsync<IOException>(() =>
                storage.WriteAsync(name, "replacement"u8.ToArray(), CancellationToken.None));
            Assert.Equal(original, await storage.ReadAsync(name, original.Length, CancellationToken.None));

            var symlinkName = Guid.NewGuid().ToString("N") + ".png";
            var symlinkPath = Path.Combine(directory, symlinkName);
            File.CreateSymbolicLink(symlinkPath, Path.Combine(directory, name));
            await Assert.ThrowsAsync<IOException>(() => storage.ReadAsync(symlinkName, original.Length, CancellationToken.None));
            Assert.Throws<IOException>(() => storage.List());
            File.Delete(symlinkPath);

            File.SetUnixFileMode(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite |
                UnixFileMode.UserExecute | UnixFileMode.OtherWrite);
            Assert.Throws<IOException>(() => storage.List());
            File.SetUnixFileMode(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            storage.Delete(name);
            Assert.Empty(storage.List());
        }
        finally
        {
            if (Directory.Exists(link)) Directory.Delete(link);
            Directory.Delete(directory, recursive: true);
        }
    }

    [LinuxOnlyFact]
    public async Task ClamdProtocolAcceptsOnlyAnExactCleanReplyAndTimesOut()
    {
        if (!OperatingSystem.IsLinux()) return;
        var directory = CreatePrivateDirectory();
        var path = Path.Combine(directory, "c.sock");
        try
        {
            var scanner = new ClamAvUnixScanner(new PhotoOptions
            {
                ScannerSocketPath = path,
                ScannerTimeoutSeconds = 1
            });
            await Assert.ThrowsAsync<IOException>(() =>
                scanner.IsCleanAsync("synthetic scan payload"u8.ToArray(), CancellationToken.None));
            using var listener = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            listener.Bind(new UnixDomainSocketEndPoint(path));
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            listener.Listen(4);
            var bytes = "synthetic scan payload"u8.ToArray();
            Assert.True(await ScanWithReply(scanner, listener, bytes, "stream: OK\0"));
            Assert.False(await ScanWithReply(scanner, listener, bytes, "stream: Synthetic FOUND\0"));
            Assert.False(await ScanWithReply(scanner, listener, bytes, "stream: OKAY\0"));

            var waiting = scanner.IsCleanAsync(bytes, CancellationToken.None);
            using var peer = await listener.AcceptAsync().WaitAsync(TimeSpan.FromSeconds(5));
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static async Task<bool> ScanWithReply(
        ClamAvUnixScanner scanner, Socket listener, byte[] input, string reply)
    {
        var scan = scanner.IsCleanAsync(input, CancellationToken.None);
        using var peer = await listener.AcceptAsync().WaitAsync(TimeSpan.FromSeconds(5));
        await using var stream = new NetworkStream(peer, ownsSocket: false);
        var command = new byte["zINSTREAM\0"u8.Length];
        await stream.ReadExactlyAsync(command);
        Assert.Equal("zINSTREAM\0"u8.ToArray(), command);
        using var received = new MemoryStream();
        var length = new byte[4];
        while (true)
        {
            await stream.ReadExactlyAsync(length);
            var count = BinaryPrimitives.ReadUInt32BigEndian(length);
            if (count == 0) break;
            Assert.InRange(count, 1u, 64u * 1024);
            var chunk = new byte[count];
            await stream.ReadExactlyAsync(chunk);
            received.Write(chunk);
        }
        Assert.Equal(input, received.ToArray());
        await stream.WriteAsync(Encoding.ASCII.GetBytes(reply));
        return await scan;
    }

    private static string CreatePrivateDirectory()
    {
        if (!OperatingSystem.IsLinux()) throw new PlatformNotSupportedException();
        Directory.CreateDirectory("artifacts");
        var path = Path.GetFullPath(Path.Combine("artifacts", $"p-{Guid.NewGuid():N}"[..10]));
        Directory.CreateDirectory(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        return path;
    }
}

public sealed class LinuxOnlyFactAttribute : FactAttribute
{
    public LinuxOnlyFactAttribute()
    {
        if (!OperatingSystem.IsLinux()) Skip = "Requires Linux private-directory and Unix-socket semantics.";
    }
}

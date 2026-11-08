using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace VarPlats.Web.Photos;

public sealed record PhotoDiskEntry(string Name, long Length);

public interface IPhotoFileStorage
{
    IReadOnlyList<PhotoDiskEntry> List();
    long AvailableBytes();
    Task WriteAsync(string name, ReadOnlyMemory<byte> bytes, CancellationToken cancellationToken);
    Task<byte[]> ReadAsync(string name, long length, CancellationToken cancellationToken);
    void Delete(string name);
}

public sealed class LinuxPhotoStorage : IPhotoFileStorage, IDisposable
{
    private readonly LinuxPrivateDirectory directory;

    public LinuxPhotoStorage(PhotoOptions options)
    {
        directory = LinuxPrivateDirectory.Open(options.StoragePath);
        if (!Directory.Exists($"/proc/self/fd/{directory.FileDescriptor}"))
        {
            directory.Dispose();
            throw new InvalidOperationException("Bildlagring kräver en tillgänglig procfs-katalog för säkra kataloghandtag.");
        }
    }

    public IReadOnlyList<PhotoDiskEntry> List()
    {
        directory.CheckPrivate();
        var result = new List<PhotoDiskEntry>();
        foreach (var path in Directory.EnumerateFileSystemEntries($"/proc/self/fd/{directory.FileDescriptor}"))
        {
            var name = Path.GetFileName(path);
            if (!IsStorageName(name, allowPart: true))
                throw new IOException("Den privata bildkatalogen innehåller ett okänt objekt.");
            var stat = directory.StatEntry(name);
            LinuxPrivateDirectory.RequirePrivateFile(stat);
            if (stat.Size > long.MaxValue)
                throw new IOException("Bildfilens storlek är ogiltig.");
            result.Add(new(name, (long)stat.Size));
        }
        return result;
    }

    public long AvailableBytes() => directory.AvailableBytes();

    public async Task WriteAsync(string name, ReadOnlyMemory<byte> bytes, CancellationToken cancellationToken)
    {
        if (!IsStorageName(name, allowPart: false) || bytes.IsEmpty
            || bytes.Length > PhotoSanitizer.MaximumOutputBytes)
            throw new IOException("Bildfilens namn eller storlek är ogiltig.");
        directory.CheckPrivate();
        var partName = name[..32] + ".part";
        var created = false;
        var published = false;
        try
        {
            using var handle = directory.CreateFile(partName);
            created = true;
            LinuxPrivateDirectory.RequirePrivateFile(LinuxPrivateDirectory.StatHandle(handle));
            await using (var stream = new FileStream(handle, FileAccess.Write, 64 * 1024, isAsync: false))
            {
                await stream.WriteAsync(bytes, cancellationToken);
                stream.Flush(flushToDisk: true);
            }
            directory.RenameExclusive(partName, name);
            published = true;
            directory.Sync();
        }
        catch
        {
            if (created)
            {
                try { directory.Unlink(published ? name : partName); }
                catch (IOException) { }
            }
            throw;
        }
    }

    public async Task<byte[]> ReadAsync(string name, long length, CancellationToken cancellationToken)
    {
        if (!IsStorageName(name, allowPart: false) || length is < 1 or > PhotoSanitizer.MaximumOutputBytes)
            throw new IOException("Bildfilens namn eller storlek är ogiltig.");
        directory.CheckPrivate();
        using var handle = directory.OpenFile(name);
        var stat = LinuxPrivateDirectory.StatHandle(handle);
        LinuxPrivateDirectory.RequirePrivateFile(stat);
        if (stat.Size != (ulong)length)
            throw new IOException("Bildfilens storlek stämmer inte med registret.");

        var bytes = new byte[(int)length];
        await using var stream = new FileStream(handle, FileAccess.Read, 64 * 1024, isAsync: false);
        await stream.ReadExactlyAsync(bytes, cancellationToken);
        if (stream.ReadByte() != -1)
            throw new IOException("Bildfilens storlek stämmer inte med registret.");
        return bytes;
    }

    public void Delete(string name)
    {
        if (!IsStorageName(name, allowPart: true))
            throw new IOException("Bildfilens namn är ogiltigt.");
        directory.CheckPrivate();
        directory.Unlink(name);
        directory.Sync();
    }

    internal static bool IsStorageName(string name, bool allowPart)
    {
        if (name.Length != 36 && !(allowPart && name.Length == 37)) return false;
        if (!name.AsSpan(0, 32).ToString().All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f'))
            return false;
        var suffix = name[32..];
        return suffix is ".jpg" or ".png" || (allowPart && suffix == ".part");
    }

    public void Dispose() => directory.Dispose();
}

internal sealed class LinuxPrivateDirectory : IDisposable
{
    private const int ReadOnly = 0;
    private const int WriteOnly = 1;
    private const int Create = 0x40;
    private const int Exclusive = 0x80;
    private const int NonBlock = 0x800;
    private const int CloseOnExec = 0x80000;
    private const int AtSymlinkNoFollow = 0x100;
    private const int AtEmptyPath = 0x1000;
    private const uint BasicStats = 0x7ff;
    private const ushort FileTypeMask = 0xf000;
    private const ushort DirectoryType = 0x4000;
    private const ushort RegularFileType = 0x8000;
    private const ushort SocketType = 0xc000;

    // Linux ARM64 uses different O_DIRECTORY and O_NOFOLLOW bits than x64.
    private static readonly int DirectoryFlag = RuntimeInformation.ProcessArchitecture == Architecture.Arm64 ? 0x4000 : 0x10000;
    private static readonly int NoFollow = RuntimeInformation.ProcessArchitecture == Architecture.Arm64 ? 0x8000 : 0x20000;

    private readonly SafeFileHandle handle;
    public int FileDescriptor => handle.DangerousGetHandle().ToInt32();

    private LinuxPrivateDirectory(SafeFileHandle handle) => this.handle = handle;

    public static LinuxPrivateDirectory Open(string path)
    {
        if (!OperatingSystem.IsLinux()
            || RuntimeInformation.ProcessArchitecture is not (Architecture.X64 or Architecture.Arm64)
            || !Path.IsPathFullyQualified(path))
            throw new InvalidOperationException("Privat bildlagring och skanner kräver Linux på x64 eller ARM64.");

        var current = OpenHandle("/", ReadOnly | DirectoryFlag | NoFollow | CloseOnExec);
        try
        {
            RequireSafeAncestor(StatHandle(current));
            var components = Path.GetFullPath(path).Split('/', StringSplitOptions.RemoveEmptyEntries);
            foreach (var component in components)
            {
                var next = OpenAt(current.DangerousGetHandle().ToInt32(), component,
                    ReadOnly | DirectoryFlag | NoFollow | CloseOnExec, 0);
                current.Dispose();
                current = next;
                RequireSafeAncestor(StatHandle(current));
            }
            var result = new LinuxPrivateDirectory(current);
            result.CheckPrivate();
            return result;
        }
        catch
        {
            current.Dispose();
            throw;
        }
    }

    public void CheckPrivate()
    {
        var stat = StatHandle(handle);
        if ((stat.Mode & FileTypeMask) != DirectoryType || stat.Uid != GetEuid()
            || (stat.Mode & 0x0fff) != 0x01c0)
            throw new IOException("Bildkatalogen måste ägas av webbprocessen och ha rättigheten 0700.");
    }

    public LinuxStatx StatEntry(string name)
    {
        if (name.Contains('/') || name is "" or "." or "..")
            throw new IOException("Ogiltigt filnamn.");
        if (Statx(FileDescriptor, name, AtSymlinkNoFollow, BasicStats, out var stat) != 0)
            throw IoError("Objektet i den privata katalogen kunde inte kontrolleras.");
        RequireBasicStats(stat);
        return stat;
    }

    public void RequirePrivateSocket(string name)
    {
        CheckPrivate();
        var stat = StatEntry(name);
        if ((stat.Mode & FileTypeMask) != SocketType || stat.Uid != GetEuid()
            || (stat.Mode & 0x003f) != 0 || (stat.Mode & 0x0080) == 0)
            throw new IOException("Skannerns Unix-socket måste vara privat och ägas av webbprocessen.");
    }

    public static void RequirePrivateFile(LinuxStatx stat)
    {
        if ((stat.Mode & FileTypeMask) != RegularFileType || stat.Uid != GetEuid()
            || (stat.Mode & 0x0fff) != 0x0180 || stat.LinkCount != 1)
            throw new IOException("Bildfilen är inte en privat, vanlig fil.");
    }

    public SafeFileHandle CreateFile(string name)
        => OpenAt(FileDescriptor, name, WriteOnly | Create | Exclusive | NoFollow | CloseOnExec, 0x180);

    public SafeFileHandle OpenFile(string name)
        => OpenAt(FileDescriptor, name, ReadOnly | NonBlock | NoFollow | CloseOnExec, 0);

    public void RenameExclusive(string source, string destination)
    {
        if (RenameAt2(FileDescriptor, source, FileDescriptor, destination, 1) != 0)
            throw IoError("Bildfilen kunde inte placeras atomärt.");
    }

    public void Unlink(string name)
    {
        if (UnlinkAt(FileDescriptor, name, 0) != 0 && Marshal.GetLastPInvokeError() != 2)
            throw IoError("Bildfilen kunde inte tas bort.");
    }

    public void Sync()
    {
        if (Fsync(FileDescriptor) != 0)
            throw IoError("Bildkatalogen kunde inte synkroniseras.");
    }

    public long AvailableBytes()
    {
        CheckPrivate();
        if (Fstatvfs(FileDescriptor, out var stat) != 0 || stat.FragmentSize == 0)
            throw IoError("Lagringens lediga utrymme kunde inte kontrolleras.");
        return stat.AvailableBlocks > (ulong)long.MaxValue / stat.FragmentSize
            ? long.MaxValue : (long)(stat.AvailableBlocks * stat.FragmentSize);
    }

    public static LinuxStatx StatHandle(SafeFileHandle file)
    {
        if (Statx(file.DangerousGetHandle().ToInt32(), "", AtEmptyPath | AtSymlinkNoFollow,
                BasicStats, out var stat) != 0)
            throw IoError("Ett privat filhandtag kunde inte kontrolleras.");
        RequireBasicStats(stat);
        return stat;
    }

    private static void RequireBasicStats(LinuxStatx stat)
    {
        if ((stat.Mask & 0x20f) != 0x20f)
            throw new IOException("Filsystemet saknar nödvändig säkerhetsinformation.");
    }

    private static void RequireSafeAncestor(LinuxStatx stat)
    {
        if ((stat.Mode & FileTypeMask) != DirectoryType
            || (stat.Uid != 0 && stat.Uid != GetEuid()) || (stat.Mode & 0x12) != 0)
            throw new IOException("Katalogvägen har en osäker ägare eller skrivbehörighet.");
    }

    private static SafeFileHandle OpenHandle(string path, int flags)
    {
        var fd = OpenNative(path, flags);
        return fd < 0 ? throw IoError("Den privata katalogvägen kunde inte öppnas.")
            : new SafeFileHandle((IntPtr)fd, ownsHandle: true);
    }

    private static SafeFileHandle OpenAt(int directory, string name, int flags, uint mode)
    {
        var fd = OpenAtNative(directory, name, flags, mode);
        return fd < 0 ? throw IoError("Objektet i den privata katalogen kunde inte öppnas säkert.")
            : new SafeFileHandle((IntPtr)fd, ownsHandle: true);
    }

    private static IOException IoError(string message)
        => new(message, new Win32Exception(Marshal.GetLastPInvokeError()));

    public void Dispose() => handle.Dispose();

    [StructLayout(LayoutKind.Explicit, Size = 256)]
    internal struct LinuxStatx
    {
        [FieldOffset(0)] public uint Mask;
        [FieldOffset(16)] public uint LinkCount;
        [FieldOffset(20)] public uint Uid;
        [FieldOffset(28)] public ushort Mode;
        [FieldOffset(40)] public ulong Size;
    }

    [StructLayout(LayoutKind.Sequential, Size = 112)]
    private struct LinuxStatVfs
    {
        public ulong BlockSize;
        public ulong FragmentSize;
        public ulong Blocks;
        public ulong FreeBlocks;
        public ulong AvailableBlocks;
    }

    [DllImport("libc", EntryPoint = "open", SetLastError = true)]
    private static extern int OpenNative(string path, int flags);
    [DllImport("libc", EntryPoint = "openat", SetLastError = true)]
    private static extern int OpenAtNative(int directory, string path, int flags, uint mode);
    [DllImport("libc", EntryPoint = "statx", SetLastError = true)]
    private static extern int Statx(int directory, string path, int flags, uint mask, out LinuxStatx stat);
    [DllImport("libc", EntryPoint = "renameat2", SetLastError = true)]
    private static extern int RenameAt2(int oldDirectory, string oldPath, int newDirectory, string newPath, uint flags);
    [DllImport("libc", EntryPoint = "unlinkat", SetLastError = true)]
    private static extern int UnlinkAt(int directory, string path, int flags);
    [DllImport("libc", EntryPoint = "fsync", SetLastError = true)]
    private static extern int Fsync(int fd);
    [DllImport("libc", EntryPoint = "fstatvfs", SetLastError = true)]
    private static extern int Fstatvfs(int fd, out LinuxStatVfs stat);
    [DllImport("libc", EntryPoint = "geteuid")]
    private static extern uint GetEuid();
}

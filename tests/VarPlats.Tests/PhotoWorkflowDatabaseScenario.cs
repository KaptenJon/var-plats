using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using VarPlats.Core;
using VarPlats.Web;
using VarPlats.Web.Data;
using VarPlats.Web.Photos;
using Xunit;

namespace VarPlats.Tests;

internal sealed class PhotoWorkflowDatabaseScenario : IDisposable
{
    private readonly IDbContextFactory<ReportDbContext> contexts;
    private readonly ClaimsPrincipal owner;
    private readonly ClaimsPrincipal other;
    private readonly ClaimsPrincipal admin;
    private readonly MemoryStorage storage = new();
    private readonly PhotoOptions options = new()
    {
        Enabled = true,
        MaxFiles = 4,
        MaxStorageBytes = 20 * 1024 * 1024,
        MinimumFreeBytes = 64 * 1024 * 1024,
        MaxConcurrentUploads = 1
    };
    private readonly DatabasePhotoWriter writer;
    private readonly DatabasePhotoReader reader;
    private readonly PhotoProcessor processor;
    private readonly PhotoUploadCommands uploads;
    private readonly ReportCommands reports;
    private readonly ReportActionCommands actions;
    private readonly byte[] image = PhotoWorkflowTests.SyntheticPngWithText("synthetic-private-marker");
    private Report pending = default!, rejected = default!, approved = default!;
    private Guid actionId;

    public PhotoWorkflowDatabaseScenario(
        IDbContextFactory<ReportDbContext> contexts, ClaimsPrincipal owner, ClaimsPrincipal other, ClaimsPrincipal admin)
    {
        this.contexts = contexts;
        this.owner = owner;
        this.other = other;
        this.admin = admin;
        var mode = new ReportingMode(false, false, true, true);
        writer = new(contexts, storage, options, mode, NullLogger<DatabasePhotoWriter>.Instance);
        reader = new(contexts, storage, options, mode, NullLogger<DatabasePhotoReader>.Instance,
            SyntheticReportAccountAccess.Instance);
        processor = new(options, new CleanScanner());
        uploads = new(mode, options, processor, writer, SyntheticReportAccountAccess.Instance);
        reports = new(new DatabaseReportStore(contexts, mode), mode,
            new DatabaseReportWorkflowStore(contexts, mode, options, storage), writer,
            SyntheticReportAccountAccess.Instance);
        actions = new(mode, new DatabaseReportActionStore(contexts, mode), writer, SyntheticReportAccountAccess.Instance);
    }

    public async Task BeforeAccountDeletionAsync()
    {
        pending = await uploads.SubmitReportAsync(owner, ReportTests.ValidDraft(), new MemoryStream(image));
        rejected = await uploads.SubmitReportAsync(owner, ReportTests.ValidDraft(), new MemoryStream(image));
        approved = await uploads.SubmitReportAsync(owner, ReportTests.ValidDraft(), new MemoryStream(image));
        Assert.Null(await reader.ReadReportAsync(new ClaimsPrincipal(), pending.Id));
        Assert.Null(await reader.ReadReportAsync(other, pending.Id));
        Assert.NotNull(await reader.ReadReportAsync(owner, pending.Id));
        Assert.NotNull(await reader.ReadReportAsync(admin, pending.Id));
        Assert.True(await reports.DecideAsync(admin, rejected.Id, ReportReviewStatus.Rejected));
        storage.FailRead = true;
        await Assert.ThrowsAsync<PhotoReviewException>(() =>
            reports.DecideAsync(admin, approved.Id, ReportReviewStatus.Approved));
        storage.FailRead = false;
        options.Enabled = false;
        await Assert.ThrowsAsync<DbUpdateException>(() =>
            reports.DecideAsync(admin, approved.Id, ReportReviewStatus.Approved));
        options.Enabled = true;
        Assert.True(await reports.DecideAsync(admin, approved.Id, ReportReviewStatus.Approved));
        Assert.Null(await reader.ReadReportAsync(new ClaimsPrincipal(), rejected.Id));
        Assert.NotNull(await reader.ReadReportAsync(owner, rejected.Id));
        Assert.NotNull(await reader.ReadReportAsync(new ClaimsPrincipal(), approved.Id));

        var draft = new ReportActionDraft { Description = "Syntetisk åtgärd utan personuppgifter." };
        Assert.False(await uploads.SubmitActionAsync(owner, pending.Id, draft, new MemoryStream(image)));
        Assert.True(await uploads.SubmitActionAsync(owner, approved.Id, draft, new MemoryStream(image)));
        await using (var db = contexts.CreateDbContext())
            actionId = await db.ReportActions.Where(action => action.ReportId == approved.Id)
                .Select(action => action.Id).SingleAsync();
        var actionImage = await reader.ReadActionAsync(actionId);
        Assert.NotNull(actionImage);
        Assert.DoesNotContain("synthetic-private-marker", System.Text.Encoding.Latin1.GetString(actionImage.Bytes));

        await using (var db = contexts.CreateDbContext())
        {
            var count = await db.ReportSubmissions.CountAsync();
            await Assert.ThrowsAsync<PhotoUploadException>(() => uploads.SubmitReportAsync(
                owner, ReportTests.ValidDraft(), new MemoryStream(image)));
            Assert.Equal(count, await db.ReportSubmissions.CountAsync());
        }
        options.MaxFiles = 5;
        storage.FailWrite = true;
        await Assert.ThrowsAsync<IOException>(() => uploads.SubmitReportAsync(
            owner, ReportTests.ValidDraft(), new MemoryStream(image)));
        storage.FailWrite = false;
        Assert.Equal(4, storage.List().Count);

        await using var check = contexts.CreateDbContext();
        check.ReportPhotos.Add(new()
        {
            Id = Guid.NewGuid(),
            SubmissionId = approved.Id,
            StorageName = Guid.NewGuid().ToString("N") + ".png",
            ContentType = "image/png",
            Sha256 = new string('0', 64),
            ByteLength = 1,
            Width = 1,
            Height = 1
        });
        var exception = await Assert.ThrowsAsync<DbUpdateException>(() => check.SaveChangesAsync());
        Assert.Equal(Npgsql.PostgresErrorCodes.CheckViolation,
            Assert.IsType<Npgsql.PostgresException>(exception.InnerException).SqlState);
    }

    public async Task AfterAccountDeletionAndRemovalAsync()
    {
        Assert.Null(await reader.ReadReportAsync(owner, pending.Id));
        Assert.NotNull(await reader.ReadReportAsync(admin, pending.Id));
        Assert.Null(await reader.ReadReportAsync(new ClaimsPrincipal(), rejected.Id));
        Assert.NotNull(await reader.ReadReportAsync(new ClaimsPrincipal(), approved.Id));
        Assert.NotNull(await reader.ReadActionAsync(actionId));
        Assert.True(await reports.DecideAsync(admin, pending.Id, ReportReviewStatus.Rejected));
        Assert.Equal(4, storage.List().Count);
        await using (var db = contexts.CreateDbContext())
        {
            Assert.Null((await db.ReportSubmissions.SingleAsync(item => item.Id == pending.Id)).AuthorId);
            Assert.Null((await db.ReportActions.SingleAsync(item => item.Id == actionId)).AuthorId);
        }

        Assert.True(await actions.RemoveAsync(admin, approved.Id, actionId));
        Assert.Null(await reader.ReadActionAsync(actionId));
        Assert.Equal(3, storage.List().Count);
        storage.FailDelete = true;
        Assert.True(await reports.RemoveAsync(admin, approved.Id));
        Assert.Null(await reader.ReadReportAsync(new ClaimsPrincipal(), approved.Id));
        Assert.Equal(3, storage.List().Count);
        storage.FailDelete = false;
        await writer.CleanupAsync();
        Assert.Equal(2, storage.List().Count);

        storage.AddOrphan();
        Assert.Equal(3, storage.List().Count);
        await writer.CleanupAsync();
        Assert.Equal(2, storage.List().Count);
    }

    public void Dispose() => processor.Dispose();

    private sealed class CleanScanner : IPhotoMalwareScanner
    {
        public Task<bool> IsCleanAsync(ReadOnlyMemory<byte> bytes, CancellationToken cancellationToken)
            => Task.FromResult(true);
    }

    private sealed class MemoryStorage : IPhotoFileStorage
    {
        private readonly Dictionary<string, byte[]> files = [];
        public bool FailWrite { get; set; }
        public bool FailRead { get; set; }
        public bool FailDelete { get; set; }
        public IReadOnlyList<PhotoDiskEntry> List()
            => files.Select(item => new PhotoDiskEntry(item.Key, item.Value.Length)).ToArray();
        public long AvailableBytes() => 1024L * 1024 * 1024;
        public Task WriteAsync(string name, ReadOnlyMemory<byte> bytes, CancellationToken cancellationToken)
        {
            if (FailWrite) throw new IOException("synthetic storage failure");
            files.Add(name, bytes.ToArray());
            return Task.CompletedTask;
        }
        public Task<byte[]> ReadAsync(string name, long length, CancellationToken cancellationToken)
        {
            if (FailRead) throw new IOException("synthetic read failure");
            return Task.FromResult(files[name].ToArray());
        }
        public void Delete(string name)
        {
            if (FailDelete) throw new IOException("synthetic deletion failure");
            files.Remove(name);
        }
        public void AddOrphan() => files.Add(Guid.NewGuid().ToString("N") + ".part", [1, 2, 3]);
    }
}

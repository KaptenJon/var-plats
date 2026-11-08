using System.Security.Claims;
using System.Text;
using System.Net;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.JSInterop;
using NetTopologySuite.Geometries;
using SkiaSharp;
using VarPlats.Core;
using VarPlats.Web;
using VarPlats.Web.Components;
using VarPlats.Web.Data;
using VarPlats.Web.Photos;
using Xunit;

namespace VarPlats.Tests;

public sealed class PhotoWorkflowTests
{
    [Fact]
    public async Task BothUploadControlsAssociateTheSwedishPrivacyAndRetentionWarning()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IJSRuntime, UnusedJs>();
        await using var provider = services.BuildServiceProvider();
        await using var renderer = new HtmlRenderer(provider, provider.GetRequiredService<ILoggerFactory>());
        foreach (var id in new[] { "report-photo", "action-photo" })
        {
            var html = await renderer.Dispatcher.InvokeAsync(async () =>
            {
                var parameters = ParameterView.FromDictionary(new Dictionary<string, object?> { ["Id"] = id });
                return (await renderer.RenderComponentAsync<PhotoUploadField>(parameters)).ToHtmlString();
            });
            var text = WebUtility.HtmlDecode(html);
            Assert.Contains($"id=\"{id}\"", text);
            Assert.Contains($"aria-describedby=\"{id}-warning\"", text);
            Assert.Contains("Bilden får inte innehålla människor", text);
            Assert.Contains("ansikten eller personuppgifter", text);
            Assert.Contains("kan finnas kvar även om du tar bort kontot", text);
            Assert.Contains("Metadatarensning tar inte bort synliga personer", text);
        }
    }

    [Fact]
    public async Task ValidUploadScansBothVersionsAndNeverPersistsSyntheticMetadata()
    {
        const string marker = "synthetic-private-metadata-marker";
        var input = SyntheticPngWithText(marker);
        Assert.Contains(marker, Encoding.Latin1.GetString(input));
        var scanner = new TestScanner();
        var writer = new RecordingWriter();
        using var processor = new PhotoProcessor(Enabled(), scanner);
        var commands = new PhotoUploadCommands(Writable(), Enabled(), processor, writer,
            SyntheticReportAccountAccess.Instance);

        var report = await commands.SubmitReportAsync(Account("Member"), ReportTests.ValidDraft(), new MemoryStream(input));

        Assert.Equal(report.Id, writer.Report!.Id);
        Assert.Equal(2, scanner.Inputs.Count);
        Assert.Equal(input, scanner.Inputs[0]);
        Assert.Equal(writer.Bytes, scanner.Inputs[1]);
        Assert.Equal("image/png", writer.ContentType);
        Assert.DoesNotContain(marker, Encoding.Latin1.GetString(writer.Bytes!));
    }

    [Fact]
    public async Task StoredBytesMustMatchTheRecordedFormatAndHash()
    {
        var image = PhotoSanitizer.Sanitize(SyntheticPngWithText("synthetic-private-marker")).Bytes.ToArray();
        var storage = new OneFileStorage(image);
        var name = Guid.NewGuid().ToString("N") + ".png";
        var sha = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(image));
        var visible = await PhotoIntegrity.ReadAsync(storage, name, "image/png", sha, image.Length, CancellationToken.None);
        Assert.Equal(image, visible.Bytes);
        storage.Bytes[0] ^= 1;
        await Assert.ThrowsAsync<IOException>(() =>
            PhotoIntegrity.ReadAsync(storage, name, "image/png", sha, image.Length, CancellationToken.None));
        await Assert.ThrowsAsync<IOException>(() =>
            PhotoIntegrity.ReadAsync(storage, "../not-a-photo.png", "image/png", sha, image.Length, CancellationToken.None));
    }

    [Fact]
    public async Task FailedScanningAndSpoofedOrOversizedContentNeverSaveText()
    {
        var png = SyntheticPngWithText("synthetic-marker");
        foreach (var failingScan in new[] { 1, 2 })
        {
            var scanner = new TestScanner { OnScan = index => Task.FromResult(index != failingScan) };
            var writer = new RecordingWriter();
            using var processor = new PhotoProcessor(Enabled(), scanner);
            var commands = new PhotoUploadCommands(Writable(), Enabled(), processor, writer,
                SyntheticReportAccountAccess.Instance);
            await Assert.ThrowsAsync<PhotoUploadException>(() => commands.SubmitActionAsync(
                Account("Member"), Guid.NewGuid(), ValidAction(), new MemoryStream(png)));
            Assert.Equal(0, writer.Saved);
        }

        var unavailable = new TestScanner { OnScan = _ => throw new TimeoutException("synthetic scanner failure") };
        var untouched = new RecordingWriter();
        using var failureProcessor = new PhotoProcessor(Enabled(), unavailable);
        var failedCommands = new PhotoUploadCommands(Writable(), Enabled(), failureProcessor, untouched,
            SyntheticReportAccountAccess.Instance);
        await Assert.ThrowsAsync<PhotoUploadException>(() => failedCommands.SubmitReportAsync(
            Account("Member"), ReportTests.ValidDraft(), new MemoryStream(png)));
        Assert.Equal(0, untouched.Saved);

        var scannerForInvalid = new TestScanner();
        using var invalidProcessor = new PhotoProcessor(Enabled(), scannerForInvalid);
        var invalidCommands = new PhotoUploadCommands(Writable(), Enabled(), invalidProcessor, untouched,
            SyntheticReportAccountAccess.Instance);
        await Assert.ThrowsAsync<PhotoSanitizationException>(() => invalidCommands.SubmitReportAsync(
            Account("Member"), ReportTests.ValidDraft(), new MemoryStream("not really a PNG"u8.ToArray())));
        await Assert.ThrowsAsync<PhotoUploadException>(() => invalidCommands.SubmitActionAsync(
            Account("Member"), Guid.NewGuid(), ValidAction(),
            new MemoryStream(new byte[PhotoSanitizer.MaximumInputBytes + 1])));
        Assert.Single(scannerForInvalid.Inputs);
        Assert.Equal(0, untouched.Saved);
    }

    [Fact]
    public async Task StorageFailureDoesNotFallBackToTextOnlyAndConcurrentWorkIsBounded()
    {
        var png = SyntheticPngWithText("synthetic-marker");
        var failingWriter = new RecordingWriter { Fail = true };
        using var failedProcessor = new PhotoProcessor(Enabled(), new TestScanner());
        var failedCommands = new PhotoUploadCommands(Writable(), Enabled(), failedProcessor, failingWriter,
            SyntheticReportAccountAccess.Instance);
        await Assert.ThrowsAsync<IOException>(() => failedCommands.SubmitActionAsync(
            Account("Member"), Guid.NewGuid(), ValidAction(), new MemoryStream(png)));
        Assert.Equal(0, failingWriter.Saved);

        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var scanner = new TestScanner
        {
            OnScan = async index =>
            {
                if (index == 1) { entered.SetResult(); await release.Task; }
                return true;
            }
        };
        var writer = new RecordingWriter();
        using var processor = new PhotoProcessor(Enabled(), scanner);
        var commands = new PhotoUploadCommands(Writable(), Enabled(), processor, writer,
            SyntheticReportAccountAccess.Instance);
        var first = commands.SubmitReportAsync(Account("Member"), ReportTests.ValidDraft(), new MemoryStream(png));
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await Assert.ThrowsAsync<PhotoUploadException>(() => commands.SubmitReportAsync(
            Account("Member"), ReportTests.ValidDraft(), new MemoryStream(png)));
        release.SetResult();
        await first;
        Assert.Equal(1, writer.Saved);
    }

    [Fact]
    public async Task SlowUploadTimesOutWithoutSavingAndReleasesItsSlot()
    {
        var options = Enabled();
        options.UploadTimeoutSeconds = 1;
        var scanner = new TestScanner();
        var writer = new RecordingWriter();
        using var processor = new PhotoProcessor(options, scanner);
        var commands = new PhotoUploadCommands(Writable(), options, processor, writer,
            SyntheticReportAccountAccess.Instance);
        using var slow = new StalledReadStream();

        var error = await Assert.ThrowsAsync<PhotoUploadException>(() =>
            commands.SubmitReportAsync(Account("Member"), ReportTests.ValidDraft(), slow)
                .WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Contains("tog för lång tid", error.Message);
        Assert.Empty(scanner.Inputs);
        Assert.Equal(0, writer.Saved);

        using var cancelled = new CancellationTokenSource();
        using var anotherSlow = new StalledReadStream();
        var pending = commands.SubmitReportAsync(Account("Member"), ReportTests.ValidDraft(), anotherSlow,
            cancelled.Token);
        await anotherSlow.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await cancelled.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);

        await commands.SubmitReportAsync(Account("Member"), ReportTests.ValidDraft(),
            new MemoryStream(SyntheticPngWithText("synthetic-marker")));
        Assert.Equal(1, writer.Saved);
    }

    [Fact]
    public async Task AccountAccessIsCheckedAgainBeforeSavingAnUploadedPhoto()
    {
        var writer = new RecordingWriter();
        var access = new RevokedAfterFirstCheck();
        using var processor = new PhotoProcessor(Enabled(), new TestScanner());
        var commands = new PhotoUploadCommands(Writable(), Enabled(), processor, writer, access);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => commands.SubmitActionAsync(
            Account("Member"), Guid.NewGuid(), ValidAction(),
            new MemoryStream(SyntheticPngWithText("synthetic-marker"))));
        Assert.Equal(2, access.Checks);
        Assert.Equal(0, writer.Saved);
    }

    [Fact]
    public async Task UploadsRequireFeatureWorkflowPublicWritesAndAnOrdinaryAuthorizedAccount()
    {
        var scanner = new TestScanner();
        var writer = new RecordingWriter();
        using var processor = new PhotoProcessor(Enabled(), scanner);
        foreach (var mode in new[]
        {
            new ReportingMode(false, false, false, true),
            new ReportingMode(false, false, true, false),
            new ReportingMode(true, false, true, true),
            new ReportingMode(false, true, true, true)
        })
        {
            var commands = new PhotoUploadCommands(mode, Enabled(), processor, writer);
            await Assert.ThrowsAsync<InvalidOperationException>(() => commands.SubmitReportAsync(
                Account("Member"), ReportTests.ValidDraft(), new MemoryStream([])));
        }
        var disabled = new PhotoUploadCommands(Writable(), new PhotoOptions(), processor, writer);
        await Assert.ThrowsAsync<InvalidOperationException>(() => disabled.SubmitActionAsync(
            Account("Member"), Guid.NewGuid(), ValidAction(), new MemoryStream([])));

        var enabled = new PhotoUploadCommands(Writable(), Enabled(), processor, writer,
            SyntheticReportAccountAccess.Instance);
        foreach (var user in new[] { new ClaimsPrincipal(), Account("Other"), Account("Admin", recovery: true) })
        {
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => enabled.SubmitReportAsync(
                user, ReportTests.ValidDraft(), new MemoryStream([])));
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => enabled.SubmitActionAsync(
                user, Guid.NewGuid(), ValidAction(), new MemoryStream([])));
        }
        Assert.Empty(scanner.Inputs);
        Assert.Equal(0, writer.Saved);
    }

    [Fact]
    public void ConfigurationRejectsWebrootOverlapSharedSocketAndUnsafeLimits()
    {
        var root = Path.GetPathRoot(AppContext.BaseDirectory)!;
        var webRoot = Path.Combine(root, "synthetic-webroot");
        var storage = Path.Combine(root, "synthetic-private-photos");
        var scanner = Path.Combine(root, "synthetic-scanner", "clamd.sock");
        var mode = new ReportingMode(false, false, false, true);
        PhotoOptions Config(string root, string socket) => new()
        {
            Enabled = true,
            StoragePath = root,
            ScannerSocketPath = socket
        };
        Config(storage, scanner).Validate(mode, true, webRoot);
        Assert.Throws<InvalidOperationException>(() =>
            Config(Path.Combine(webRoot, "images"), scanner).Validate(mode, true, webRoot));
        Assert.Throws<InvalidOperationException>(() =>
            Config(storage, Path.Combine(storage, "clamd.sock")).Validate(mode, true, webRoot));
        var invalidLimits = Config(storage, scanner);
        invalidLimits.MaxConcurrentUploads = 3;
        Assert.Throws<InvalidOperationException>(() => invalidLimits.Validate(mode, true, webRoot));
        invalidLimits.MaxConcurrentUploads = 1;
        invalidLimits.UploadTimeoutSeconds = 0;
        Assert.Throws<InvalidOperationException>(() => invalidLimits.Validate(mode, true, webRoot));
    }

    [Fact]
    public void VisibilityFollowsModerationOwnershipPublicationAndParentRemoval()
    {
        var owner = Guid.NewGuid();
        var other = Guid.NewGuid();
        var pending = Submission(owner, ReportReviewStatus.Pending);
        var rejected = Submission(owner, ReportReviewStatus.Rejected);
        var approved = Submission(owner, ReportReviewStatus.Approved);
        var unpublishedApproved = Submission(owner, ReportReviewStatus.Approved);
        var published = Published(approved.Id);
        var submissions = new[] { pending, rejected, approved, unpublishedApproved }.AsQueryable();
        var reports = new[] { published }.AsQueryable();
        var photos = new[] { pending, rejected, approved, unpublishedApproved }
            .Select(submission => Photo(submission.Id, null)).ToArray().AsQueryable();
        var ids = submissions.Select(submission => submission.Id).ToArray();

        HashSet<Guid> Seen(ClaimsPrincipal viewer) => PhotoVisibility.Reports(
            photos, submissions, reports, SyntheticReportAccountAccess.Rights(viewer), ids)
            .Select(photo => photo.ParentId).ToHashSet();
        Assert.True(Seen(new ClaimsPrincipal()).SetEquals([approved.Id]));
        Assert.True(Seen(Account("Member", other)).SetEquals([approved.Id]));
        Assert.True(Seen(Account("Member", owner)).SetEquals([pending.Id, rejected.Id, approved.Id]));
        Assert.True(Seen(Account("Admin", other)).SetEquals([pending.Id, rejected.Id, approved.Id]));
        Assert.True(Seen(Account("Admin", owner, recovery: true)).SetEquals([approved.Id]));

        pending.AuthorId = null;
        rejected.AuthorId = null;
        approved.AuthorId = null;
        Assert.True(Seen(Account("Member", owner)).SetEquals([approved.Id]));
        Assert.True(Seen(Account("Admin", other)).SetEquals([pending.Id, rejected.Id, approved.Id]));
        Assert.True(Seen(new ClaimsPrincipal()).SetEquals([approved.Id]));

        var action = new ReportActionEntity
        {
            Id = Guid.NewGuid(),
            ReportId = published.Id,
            AuthorId = null,
            Description = "Syntetisk åtgärd utan personuppgifter.",
            CreatedAt = DateTimeOffset.UtcNow
        };
        var actionPhotos = new[] { Photo(null, action.Id) }.AsQueryable();
        var actions = new[] { action }.AsQueryable();
        Assert.Single(PhotoVisibility.Actions(actionPhotos, actions, reports, [action.Id]));
        Assert.Empty(PhotoVisibility.Actions(actionPhotos, Array.Empty<ReportActionEntity>().AsQueryable(), reports, [action.Id]));
        Assert.Empty(PhotoVisibility.Actions(actionPhotos, actions, Array.Empty<ReportEntity>().AsQueryable(), [action.Id]));
        Assert.Empty(PhotoVisibility.Reports(photos, submissions, Array.Empty<ReportEntity>().AsQueryable(),
            default, ids));
    }

    [Fact]
    public void EfModelAndMigrationPreserveAnonymityAndOnePhotoPerParent()
    {
        using var db = new ReportDbContext(new DbContextOptionsBuilder<ReportDbContext>()
            .UseNpgsql("Host=127.0.0.1;Database=unused", postgres => postgres.UseNetTopologySuite()).Options);
        var entity = db.Model.FindEntityType(typeof(ReportPhotoEntity))!;
        Assert.DoesNotContain(entity.GetForeignKeys(), key => key.PrincipalEntityType.ClrType == typeof(IdentityUser<Guid>));
        Assert.Contains(entity.GetForeignKeys(), key => key.PrincipalEntityType.ClrType == typeof(ReportSubmissionEntity)
            && key.DeleteBehavior == DeleteBehavior.Cascade);
        Assert.Contains(entity.GetForeignKeys(), key => key.PrincipalEntityType.ClrType == typeof(ReportActionEntity)
            && key.DeleteBehavior == DeleteBehavior.Cascade);
        Assert.Contains(entity.GetIndexes(), index => index.IsUnique
            && index.Properties.Select(property => property.Name).SequenceEqual(["SubmissionId"]));
        Assert.Contains(entity.GetIndexes(), index => index.IsUnique
            && index.Properties.Select(property => property.Name).SequenceEqual(["ActionId"]));
        Assert.False(db.Database.HasPendingModelChanges());
        var script = db.GetService<IMigrator>().GenerateScript(
            "20261108162158_ReportActionsAndConfirmations", "20261108164953_ReportPhotos");
        Assert.Contains("CREATE TABLE report_photos", script);
        Assert.Contains("ON DELETE CASCADE", script);
        Assert.Contains("CREATE TRIGGER varplats_guard_report_photo", script);
        Assert.Contains("CREATE TRIGGER varplats_guard_photo_approval", script);
        Assert.Contains("FOR UPDATE", script);
        Assert.DoesNotContain("AuthorId", script);

        var reportSql = PhotoVisibility.Reports(db.ReportPhotos, db.ReportSubmissions, db.Reports,
            SyntheticReportAccountAccess.Rights(Account("Member")), [Guid.NewGuid()]).ToQueryString();
        var actionSql = PhotoVisibility.Actions(db.ReportPhotos, db.ReportActions, db.Reports, [Guid.NewGuid()])
            .ToQueryString();
        Assert.Contains("report_submissions", reportSql);
        Assert.Contains("reports", reportSql);
        Assert.Contains("report_actions", actionSql);
        Assert.Contains("reports", actionSql);
    }

    private static ReportSubmissionEntity Submission(Guid authorId, ReportReviewStatus status)
    {
        var id = Guid.NewGuid();
        return new()
        {
            Id = id,
            Title = "Syntetisk rapport",
            Description = "Påhittad text utan personuppgifter.",
            Location = new Point(18, 59) { SRID = 4326 },
            CreatedAt = DateTimeOffset.UtcNow,
            ReviewStatus = status,
            AuthorId = authorId,
            PublishedReportId = status == ReportReviewStatus.Approved ? id : null
        };
    }

    private static ReportEntity Published(Guid id) => new()
    {
        Id = id,
        Title = "Syntetisk rapport",
        Description = "Påhittad text utan personuppgifter.",
        Location = new Point(18, 59) { SRID = 4326 },
        CreatedAt = DateTimeOffset.UtcNow
    };

    private static ReportPhotoEntity Photo(Guid? submissionId, Guid? actionId) => new()
    {
        Id = Guid.NewGuid(),
        SubmissionId = submissionId,
        ActionId = actionId,
        StorageName = "0123456789abcdef0123456789abcdef.png",
        ContentType = "image/png",
        Sha256 = new string('0', 64),
        ByteLength = 1,
        Width = 1,
        Height = 1
    };

    internal static byte[] SyntheticPngWithText(string marker)
    {
        using var bitmap = new SKBitmap(2, 2);
        bitmap.Erase(SKColors.Red);
        using var encoded = bitmap.Encode(SKEncodedImageFormat.Png, 90);
        var png = encoded.ToArray();
        var payload = Encoding.ASCII.GetBytes("comment\0" + marker);
        var type = "tEXt"u8.ToArray();
        using var chunk = new MemoryStream();
        var length = new byte[4];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(length, (uint)payload.Length);
        chunk.Write(length);
        chunk.Write(type);
        chunk.Write(payload);
        var crc = 0xffffffffu;
        foreach (var value in type.Concat(payload))
        {
            crc ^= value;
            for (var bit = 0; bit < 8; bit++)
                crc = (crc >> 1) ^ ((crc & 1) == 1 ? 0xedb88320u : 0);
        }
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(length, ~crc);
        chunk.Write(length);
        return [.. png[..^12], .. chunk.ToArray(), .. png[^12..]];
    }

    private static PhotoOptions Enabled() => new() { Enabled = true, MaxConcurrentUploads = 1 };
    private static ReportingMode Writable() => new(false, false, true, true);
    private static ReportActionDraft ValidAction() => new() { Description = "Syntetisk åtgärd utan personuppgifter." };
    private static ClaimsPrincipal Account(string role, Guid? id = null, bool recovery = false)
    {
        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, (id ?? Guid.NewGuid()).ToString()),
            new(ClaimTypes.Role, role)
        };
        if (recovery) claims.Add(new("recovery_only", "true"));
        return new(new ClaimsIdentity(claims, "synthetic"));
    }

    private sealed class TestScanner : IPhotoMalwareScanner
    {
        public List<byte[]> Inputs { get; } = [];
        public Func<int, Task<bool>> OnScan { get; init; } = _ => Task.FromResult(true);
        public Task<bool> IsCleanAsync(ReadOnlyMemory<byte> bytes, CancellationToken cancellationToken)
        {
            Inputs.Add(bytes.ToArray());
            return OnScan(Inputs.Count);
        }
    }

    private sealed class RevokedAfterFirstCheck : IReportAccountAccess
    {
        public int Checks { get; private set; }
        public Task<ReportAccountRights> VerifyAsync(
            ClaimsPrincipal principal, CancellationToken cancellationToken = default)
            => Task.FromResult(++Checks == 1 ? SyntheticReportAccountAccess.Rights(principal) : default);
    }

    private sealed class StalledReadStream : Stream
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() => throw new NotSupportedException();
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override async ValueTask<int> ReadAsync(
            Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            Started.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return 0;
        }
    }

    private sealed class RecordingWriter : IPhotoWriter
    {
        public int Saved { get; private set; }
        public bool Fail { get; init; }
        public Report? Report { get; private set; }
        public byte[]? Bytes { get; private set; }
        public string? ContentType { get; private set; }
        public Task<Report> SubmitReportAsync(
            Guid authorId, Report report, SanitizedPhoto photo, CancellationToken cancellationToken)
        {
            Save(photo);
            Report = report;
            return Task.FromResult(report);
        }
        public Task<bool> SubmitActionAsync(
            Guid authorId, Guid reportId, string description, SanitizedPhoto photo, CancellationToken cancellationToken)
        {
            Save(photo);
            return Task.FromResult(true);
        }
        private void Save(SanitizedPhoto photo)
        {
            if (Fail) throw new IOException("synthetic storage failure");
            Bytes = photo.Bytes.ToArray();
            ContentType = photo.ContentType;
            Saved++;
        }
    }

    private sealed class UnusedJs : IJSRuntime
    {
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args)
            => throw new NotSupportedException();
        public ValueTask<TValue> InvokeAsync<TValue>(
            string identifier, CancellationToken cancellationToken, object?[]? args)
            => throw new NotSupportedException();
    }

    private sealed class OneFileStorage(byte[] bytes) : IPhotoFileStorage
    {
        public byte[] Bytes { get; } = bytes.ToArray();
        public IReadOnlyList<PhotoDiskEntry> List() => throw new NotSupportedException();
        public long AvailableBytes() => throw new NotSupportedException();
        public Task WriteAsync(string name, ReadOnlyMemory<byte> data, CancellationToken cancellationToken)
            => throw new NotSupportedException();
        public Task<byte[]> ReadAsync(string name, long length, CancellationToken cancellationToken)
            => Task.FromResult(Bytes.ToArray());
        public void Delete(string name) => throw new NotSupportedException();
    }
}

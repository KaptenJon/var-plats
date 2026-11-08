using System.Security.Claims;
using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using VarPlats.Core;
using VarPlats.Web.Data;

namespace VarPlats.Web.Photos;

public sealed record PhotoContent(byte[] Bytes, string ContentType);
public sealed record VisiblePhoto(Guid ParentId, string Name, string ContentType, string Sha256, long Length);

public static class PhotoIntegrity
{
    public static async Task<PhotoContent> ReadAsync(
        IPhotoFileStorage storage, string name, string contentType, string sha256, long length,
        CancellationToken cancellationToken)
    {
        if (!LinuxPhotoStorage.IsStorageName(name, allowPart: false)
            || !((contentType == "image/jpeg" && name.EndsWith(".jpg", StringComparison.Ordinal))
                || (contentType == "image/png" && name.EndsWith(".png", StringComparison.Ordinal))))
            throw new IOException("Bildens registrerade format är ogiltigt.");
        var bytes = await storage.ReadAsync(name, length, cancellationToken);
        var expected = Convert.FromHexString(sha256);
        if (expected.Length != 32 || !CryptographicOperations.FixedTimeEquals(expected, SHA256.HashData(bytes)))
            throw new IOException("Bildfilen stämmer inte med registret.");
        return new(bytes, contentType);
    }
}

public static class PhotoVisibility
{
    public static IQueryable<VisiblePhoto> Reports(
        IQueryable<ReportPhotoEntity> photos, IQueryable<ReportSubmissionEntity> submissions,
        IQueryable<ReportEntity> reports, ReportAccountRights viewer, Guid[] ids)
    {
        var canModerate = viewer.CanModerate;
        var canOwn = viewer.CanReport;
        var viewerId = viewer.AccountId.GetValueOrDefault();
        return from photo in photos
               join submission in submissions on photo.SubmissionId equals (Guid?)submission.Id
               where ids.Contains(submission.Id)
                   && ((submission.ReviewStatus == ReportReviewStatus.Approved
                        && submission.PublishedReportId == submission.Id
                        && reports.Any(report => report.Id == submission.Id))
                       || (submission.ReviewStatus != ReportReviewStatus.Approved
                           && (canModerate || (canOwn && submission.AuthorId == viewerId))))
               select new VisiblePhoto(submission.Id, photo.StorageName, photo.ContentType,
                   photo.Sha256, photo.ByteLength);
    }

    public static IQueryable<VisiblePhoto> Actions(
        IQueryable<ReportPhotoEntity> photos, IQueryable<ReportActionEntity> actions,
        IQueryable<ReportEntity> reports, Guid[] ids)
        => from photo in photos
           join action in actions on photo.ActionId equals (Guid?)action.Id
           where ids.Contains(action.Id) && reports.Any(report => report.Id == action.ReportId)
           select new VisiblePhoto(action.Id, photo.StorageName, photo.ContentType, photo.Sha256, photo.ByteLength);
}

public interface IPhotoReader
{
    Task<IReadOnlySet<Guid>> VisibleReportIdsAsync(
        ClaimsPrincipal viewer, IReadOnlyCollection<Guid> ids, CancellationToken cancellationToken = default);
    Task<IReadOnlySet<Guid>> VisibleActionIdsAsync(
        IReadOnlyCollection<Guid> ids, CancellationToken cancellationToken = default);
    Task<PhotoContent?> ReadReportAsync(
        ClaimsPrincipal viewer, Guid id, CancellationToken cancellationToken = default);
    Task<PhotoContent?> ReadActionAsync(Guid id, CancellationToken cancellationToken = default);
}

public sealed class DisabledPhotoReader : IPhotoReader
{
    public Task<IReadOnlySet<Guid>> VisibleReportIdsAsync(
        ClaimsPrincipal viewer, IReadOnlyCollection<Guid> ids, CancellationToken cancellationToken = default)
        => Task.FromResult<IReadOnlySet<Guid>>(new HashSet<Guid>());
    public Task<IReadOnlySet<Guid>> VisibleActionIdsAsync(
        IReadOnlyCollection<Guid> ids, CancellationToken cancellationToken = default)
        => Task.FromResult<IReadOnlySet<Guid>>(new HashSet<Guid>());
    public Task<PhotoContent?> ReadReportAsync(
        ClaimsPrincipal viewer, Guid id, CancellationToken cancellationToken = default)
        => Task.FromResult<PhotoContent?>(null);
    public Task<PhotoContent?> ReadActionAsync(Guid id, CancellationToken cancellationToken = default)
        => Task.FromResult<PhotoContent?>(null);
}

public sealed class DatabasePhotoReader(
    IDbContextFactory<ReportDbContext> factory, IPhotoFileStorage storage,
    PhotoOptions options, ReportingMode mode, ILogger<DatabasePhotoReader> logger,
    IReportAccountAccess? accountAccess = null) : IPhotoReader
{
    public async Task<IReadOnlySet<Guid>> VisibleReportIdsAsync(
        ClaimsPrincipal viewer, IReadOnlyCollection<Guid> ids, CancellationToken cancellationToken = default)
    {
        if (!CanRead || ids.Count == 0) return new HashSet<Guid>();
        var rights = await VerifyAsync(viewer, cancellationToken);
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        var matches = await ReportQuery(db, rights, ids.ToArray())
            .Select(row => row.ParentId).ToArrayAsync(cancellationToken);
        return matches.ToHashSet();
    }

    public async Task<IReadOnlySet<Guid>> VisibleActionIdsAsync(
        IReadOnlyCollection<Guid> ids, CancellationToken cancellationToken = default)
    {
        if (!CanRead || ids.Count == 0) return new HashSet<Guid>();
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        var matches = await ActionQuery(db, ids.ToArray())
            .Select(row => row.ParentId).ToArrayAsync(cancellationToken);
        return matches.ToHashSet();
    }

    public async Task<PhotoContent?> ReadReportAsync(
        ClaimsPrincipal viewer, Guid id, CancellationToken cancellationToken = default)
    {
        if (!CanRead || id == Guid.Empty) return null;
        var rights = await VerifyAsync(viewer, cancellationToken);
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        var row = await ReportQuery(db, rights, [id]).SingleOrDefaultAsync(cancellationToken);
        return await ReadAsync(row, cancellationToken);
    }

    public async Task<PhotoContent?> ReadActionAsync(Guid id, CancellationToken cancellationToken = default)
    {
        if (!CanRead || id == Guid.Empty) return null;
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        var row = await ActionQuery(db, [id]).SingleOrDefaultAsync(cancellationToken);
        return await ReadAsync(row, cancellationToken);
    }

    private static IQueryable<VisiblePhoto> ReportQuery(ReportDbContext db, ReportAccountRights viewer, Guid[] ids)
        => PhotoVisibility.Reports(db.ReportPhotos.AsNoTracking(), db.ReportSubmissions.AsNoTracking(),
            db.Reports.AsNoTracking(), viewer, ids);

    private static IQueryable<VisiblePhoto> ActionQuery(ReportDbContext db, Guid[] ids)
        => PhotoVisibility.Actions(db.ReportPhotos.AsNoTracking(), db.ReportActions.AsNoTracking(),
            db.Reports.AsNoTracking(), ids);

    private async Task<PhotoContent?> ReadAsync(VisiblePhoto? row, CancellationToken cancellationToken)
    {
        if (row is null) return null;
        try
        {
            return await PhotoIntegrity.ReadAsync(storage, row.Name, row.ContentType, row.Sha256, row.Length,
                cancellationToken);
        }
        catch (Exception exception) when (exception is IOException or FormatException or UnauthorizedAccessException)
        {
            logger.LogError(exception, "En registrerad bild kunde inte läsas säkert.");
            return null;
        }
    }

    private bool CanRead => options.Enabled && mode.WorkflowEnabled;

    private Task<ReportAccountRights> VerifyAsync(ClaimsPrincipal viewer, CancellationToken cancellationToken)
        => accountAccess?.VerifyAsync(viewer, cancellationToken) ?? Task.FromResult(default(ReportAccountRights));
}

public static class PhotoEndpoints
{
    public static void MapPhotoEndpoints(this WebApplication app)
    {
        app.MapGet("/photos/reports/{id:guid}", async (
            HttpContext context, Guid id, IPhotoReader reader, CancellationToken cancellationToken) =>
        {
            SetPrivacyHeaders(context);
            return Result(await reader.ReadReportAsync(context.User, id, cancellationToken));
        });
        app.MapGet("/photos/actions/{id:guid}", async (
            HttpContext context, Guid id, IPhotoReader reader, CancellationToken cancellationToken) =>
        {
            SetPrivacyHeaders(context);
            return Result(await reader.ReadActionAsync(id, cancellationToken));
        });
    }

    private static IResult Result(PhotoContent? photo)
        => photo is null ? Results.NotFound() : Results.File(photo.Bytes, photo.ContentType);

    private static void SetPrivacyHeaders(HttpContext context)
    {
        context.Response.Headers.CacheControl = "private, no-store, max-age=0";
        context.Response.Headers.Pragma = "no-cache";
        context.Response.Headers.XContentTypeOptions = "nosniff";
        context.Response.Headers["Cross-Origin-Resource-Policy"] = "same-origin";
        context.Response.Headers["Referrer-Policy"] = "no-referrer";
    }
}

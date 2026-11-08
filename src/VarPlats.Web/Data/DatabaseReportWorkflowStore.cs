using Microsoft.EntityFrameworkCore;
using VarPlats.Core;
using VarPlats.Web.Photos;

namespace VarPlats.Web.Data;

public sealed class DatabaseReportWorkflowStore(
    IDbContextFactory<ReportDbContext> factory, ReportingMode mode,
    PhotoOptions? photoOptions = null, IPhotoFileStorage? photoStorage = null) : IReportWorkflowStore
{
    public async Task<Report> SubmitAsync(
        Guid authorId, ReportDraft draft, CancellationToken cancellationToken = default)
    {
        RequireWrite();
        if (authorId == Guid.Empty)
            throw new ArgumentException("Ett konto krävs.", nameof(authorId));
        var report = draft.ToReport();
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        db.ReportSubmissions.Add(new()
        {
            Id = report.Id,
            Title = report.Title,
            Description = report.Description,
            Category = report.Category,
            Location = new(report.Longitude, report.Latitude) { SRID = 4326 },
            CreatedAt = report.CreatedAt,
            ReviewStatus = ReportReviewStatus.Pending,
            AuthorId = authorId
        });
        await db.SaveChangesAsync(cancellationToken);
        return report;
    }

    public async Task<IReadOnlyList<ReportSubmission>> ListOwnAsync(
        Guid authorId, CancellationToken cancellationToken = default)
    {
        RequireWorkflow();
        if (authorId == Guid.Empty)
            throw new ArgumentException("Ett konto krävs.", nameof(authorId));
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        return await ReadAsync(db.ReportSubmissions.AsNoTracking()
            .Where(submission => submission.AuthorId == authorId)
            .OrderByDescending(submission => submission.CreatedAt)
            .Take(200), cancellationToken);
    }

    public async Task<IReadOnlyList<ReportSubmission>> ListPendingAsync(
        CancellationToken cancellationToken = default)
    {
        RequireWorkflow();
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        return await ReadAsync(db.ReportSubmissions.AsNoTracking()
            .Where(submission => submission.ReviewStatus == ReportReviewStatus.Pending)
            .OrderBy(submission => submission.CreatedAt)
            .Take(200), cancellationToken);
    }

    public async Task<bool> DecideAsync(
        Guid id, ReportReviewStatus decision, CancellationToken cancellationToken = default)
    {
        RequireWrite();
        if (decision is not (ReportReviewStatus.Approved or ReportReviewStatus.Rejected))
            throw new ArgumentOutOfRangeException(nameof(decision));

        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var rows = await db.ReportSubmissions
            .FromSql($"""
                SELECT * FROM "report_submissions"
                WHERE "Id" = {id}
                FOR UPDATE
                """)
            .ToArrayAsync(cancellationToken);
        var submission = rows.SingleOrDefault();
        if (submission is null || submission.ReviewStatus != ReportReviewStatus.Pending)
        {
            await transaction.CommitAsync(cancellationToken);
            return false;
        }

        if (decision == ReportReviewStatus.Approved)
        {
            await db.Database.ExecuteSqlRawAsync(
                "SET LOCAL varplats.photo_review_verified = ''", cancellationToken);
            if (photoOptions?.Enabled == true)
            {
                var photo = await db.ReportPhotos.AsNoTracking()
                    .SingleOrDefaultAsync(candidate => candidate.SubmissionId == submission.Id, cancellationToken);
                if (photo is not null)
                {
                    if (photoStorage is null)
                        throw new PhotoReviewException();
                    try
                    {
                        await PhotoIntegrity.ReadAsync(photoStorage, photo.StorageName, photo.ContentType,
                            photo.Sha256, photo.ByteLength, cancellationToken);
                    }
                    catch (Exception exception) when (exception is IOException or FormatException or UnauthorizedAccessException)
                    {
                        throw new PhotoReviewException(exception);
                    }
                    await db.Database.ExecuteSqlAsync(
                        $"SELECT set_config('varplats.photo_review_verified', {submission.Id.ToString()}, true)",
                        cancellationToken);
                }
            }
            db.Reports.Add(new()
            {
                Id = submission.Id,
                Title = submission.Title,
                Description = submission.Description,
                Category = submission.Category,
                Location = new(submission.Location.X, submission.Location.Y) { SRID = 4326 },
                CreatedAt = submission.CreatedAt,
                Status = ReportStatus.Reported
            });
            submission.PublishedReportId = submission.Id;
        }

        submission.ReviewStatus = decision;
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return true;
    }

    private static async Task<IReadOnlyList<ReportSubmission>> ReadAsync(
        IQueryable<ReportSubmissionEntity> query, CancellationToken cancellationToken)
    {
        var rows = await query.Select(submission => new
        {
            submission.Id,
            submission.Title,
            submission.Description,
            submission.Category,
            submission.Location,
            submission.CreatedAt,
            submission.ReviewStatus
        }).ToArrayAsync(cancellationToken);
        return rows.Select(submission => new ReportSubmission(
            new Report(submission.Id, submission.Title, submission.Description, submission.Category,
                submission.Location.Y, submission.Location.X, submission.CreatedAt),
            submission.ReviewStatus)).ToArray();
    }

    private void RequireWorkflow()
    {
        if (!mode.WorkflowEnabled)
            throw new InvalidOperationException("Rapportgranskning är inte aktiverad.");
    }

    private void RequireWrite()
    {
        if (!mode.PublicWorkflow)
            throw new InvalidOperationException("Rapportgranskning är inte aktiverad för skrivning.");
    }
}

public sealed class PhotoReviewException(Exception? inner = null)
    : Exception("Rapportens bild kan inte kontrolleras. Godkänn inte förrän bilden går att granska.", inner);

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
        var submission = new ReportSubmissionEntity
        {
            Id = report.Id,
            Title = report.Title,
            Description = report.Description,
            Category = report.Category,
            Location = new(report.Longitude, report.Latitude) { SRID = 4326 },
            CreatedAt = report.CreatedAt,
            ReviewStatus = ReportReviewStatus.Pending,
            AuthorId = authorId
        };
        db.ReportSubmissions.Add(submission);
        if (!mode.DirectPublish)
        {
            await db.SaveChangesAsync(cancellationToken);
            return report;
        }

        // Direct publishing: the submission is saved and approved in one transaction, exactly as an approval,
        // so the review history and the author link stay in report_submissions.
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        await db.SaveChangesAsync(cancellationToken);
        await ReportPublication.PublishAsync(db, submission, photoOptions, photoStorage, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
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
            await ReportPublication.PublishAsync(db, submission, photoOptions, photoStorage, cancellationToken);
        else
        {
            submission.ReviewStatus = decision;
            await db.SaveChangesAsync(cancellationToken);
        }
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

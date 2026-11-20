using Microsoft.EntityFrameworkCore;
using VarPlats.Core;
using VarPlats.Web.Photos;

namespace VarPlats.Web.Data;

/// <summary>
/// Copies a pending submission into the public reports table. Used by an admin approval and, with
/// <see cref="ReportingMode.DirectPublish"/>, right after the submission is saved, inside the caller's transaction.
/// A photo is read back and checked against its registered hash first, and the approval trigger only accepts the
/// status change when that check has run for this submission.
/// </summary>
internal static class ReportPublication
{
    public static async Task PublishAsync(
        ReportDbContext db, ReportSubmissionEntity submission, PhotoOptions? photoOptions,
        IPhotoFileStorage? photoStorage, CancellationToken cancellationToken)
    {
        if (submission.ReviewStatus != ReportReviewStatus.Pending)
            throw new InvalidOperationException("Endast väntande rapporter kan publiceras.");
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
        submission.ReviewStatus = ReportReviewStatus.Approved;
        await db.SaveChangesAsync(cancellationToken);
    }
}

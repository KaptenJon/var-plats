using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using VarPlats.Core;
using VarPlats.Web.Data;

namespace VarPlats.Web.Photos;

public interface IPhotoCleanup
{
    Task CleanupAsync(CancellationToken cancellationToken = default);
}

public sealed class DisabledPhotoCleanup : IPhotoCleanup
{
    public Task CleanupAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
}

public sealed class DatabasePhotoWriter(
    IDbContextFactory<ReportDbContext> factory, IPhotoFileStorage storage,
    PhotoOptions options, ReportingMode mode, ILogger<DatabasePhotoWriter> logger) : IPhotoWriter, IPhotoCleanup
{
    public async Task<Report> SubmitReportAsync(
        Guid authorId, Report report, SanitizedPhoto photo, CancellationToken cancellationToken)
    {
        RequireWrite(authorId);
        await SaveAsync(photo, db =>
        {
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
            return new ReportPhotoEntity
            {
                Id = Guid.NewGuid(),
                SubmissionId = report.Id,
                StorageName = "",
                ContentType = "",
                Sha256 = ""
            };
        }, null, cancellationToken);
        return report;
    }

    public Task<bool> SubmitActionAsync(
        Guid authorId, Guid reportId, string description, SanitizedPhoto photo, CancellationToken cancellationToken)
    {
        RequireWrite(authorId);
        return SaveAsync(photo, db =>
        {
            var actionId = Guid.NewGuid();
            db.ReportActions.Add(new()
            {
                Id = actionId,
                ReportId = reportId,
                AuthorId = authorId,
                Description = description,
                CreatedAt = DateTimeOffset.UtcNow
            });
            return new ReportPhotoEntity
            {
                Id = Guid.NewGuid(),
                ActionId = actionId,
                StorageName = "",
                ContentType = "",
                Sha256 = ""
            };
        }, reportId, cancellationToken);
    }

    public async Task CleanupAsync(CancellationToken cancellationToken = default)
    {
        if (!options.Enabled) return;
        try
        {
            await using var db = await factory.CreateDbContextAsync(cancellationToken);
            await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
            await LockAsync(db, cancellationToken);
            await ReconcileAsync(db, cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning("Bildstädning avbröts; kvarvarande filer räknas mot kvoten och städas vid nästa uppladdning.");
        }
        catch (Exception exception) when (exception is IOException or DbUpdateException
            or Npgsql.NpgsqlException or InvalidOperationException or UnauthorizedAccessException)
        {
            logger.LogError(exception, "Bildstädning misslyckades; kvarvarande filer räknas mot kvoten.");
        }
    }

    private async Task<bool> SaveAsync(
        SanitizedPhoto photo, Func<ReportDbContext, ReportPhotoEntity> addParent, Guid? reportId,
        CancellationToken cancellationToken)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        await LockAsync(db, cancellationToken);
        if (reportId is Guid publishedId
            && !await db.Reports.AnyAsync(report => report.Id == publishedId, cancellationToken))
            return false;

        var entries = await ReconcileAsync(db, cancellationToken);
        var usedBytes = entries.Sum(entry => entry.Length);
        var available = storage.AvailableBytes();
        if (entries.Count >= options.MaxFiles || usedBytes > options.MaxStorageBytes - photo.Bytes.Length
            || available < options.MinimumFreeBytes
            || photo.Bytes.Length > available - options.MinimumFreeBytes)
            throw new PhotoUploadException("Bildlagringens gräns är nådd. Ingen text eller bild sparades.");

        var suffix = photo.ContentType switch
        {
            "image/jpeg" => ".jpg",
            "image/png" => ".png",
            _ => throw new InvalidOperationException("Bildformatet är inte tillåtet.")
        };
        var name = Guid.NewGuid().ToString("N") + suffix;
        var written = false;
        var commitAttempted = false;
        try
        {
            await storage.WriteAsync(name, photo.Bytes, cancellationToken);
            written = true;
            var entity = addParent(db);
            entity.StorageName = name;
            entity.ContentType = photo.ContentType;
            entity.ByteLength = photo.Bytes.Length;
            entity.Width = photo.Width;
            entity.Height = photo.Height;
            entity.Sha256 = Convert.ToHexStringLower(SHA256.HashData(photo.Bytes.Span));
            db.ReportPhotos.Add(entity);
            await db.SaveChangesAsync(cancellationToken);
            commitAttempted = true;
            await transaction.CommitAsync(cancellationToken);
            return true;
        }
        catch
        {
            if (written && !commitAttempted)
            {
                try { storage.Delete(name); }
                catch (IOException exception)
                {
                    logger.LogError(exception, "En osparad bild kunde inte städas; den räknas mot lagringskvoten.");
                }
            }
            throw;
        }
    }

    private async Task<IReadOnlyList<PhotoDiskEntry>> ReconcileAsync(
        ReportDbContext db, CancellationToken cancellationToken)
    {
        var references = await db.ReportPhotos.AsNoTracking()
            .Select(photo => new { photo.StorageName, photo.ByteLength })
            .ToArrayAsync(cancellationToken);
        if (references.Any(reference => !LinuxPhotoStorage.IsStorageName(reference.StorageName, allowPart: false)
            || reference.ByteLength is < 1 or > PhotoSanitizer.MaximumOutputBytes))
            throw new IOException("Bildregistret innehåller en ogiltig filreferens.");
        var expected = references.ToDictionary(reference => reference.StorageName, reference => reference.ByteLength);
        var retained = new List<PhotoDiskEntry>();
        foreach (var entry in storage.List())
        {
            if (!expected.TryGetValue(entry.Name, out var length))
            {
                storage.Delete(entry.Name);
                continue;
            }
            if (entry.Length != length)
                throw new IOException("En bildfil stämmer inte med den registrerade storleken.");
            expected.Remove(entry.Name);
            retained.Add(entry);
        }
        if (expected.Count != 0)
            throw new IOException("En registrerad bildfil saknas; lagringen behöver kontrolleras.");
        return retained;
    }

    private static Task<int> LockAsync(ReportDbContext db, CancellationToken cancellationToken)
        => db.Database.ExecuteSqlRawAsync("SELECT pg_advisory_xact_lock(1448104016, 1346916180)", cancellationToken);

    private void RequireWrite(Guid authorId)
    {
        if (!options.Enabled || !mode.PublicWorkflow)
            throw new InvalidOperationException("Bilduppladdning är inte aktiverad för skrivning.");
        if (authorId == Guid.Empty)
            throw new UnauthorizedAccessException("Ett behörigt konto krävs.");
    }
}

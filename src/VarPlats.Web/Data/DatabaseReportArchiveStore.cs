using Microsoft.EntityFrameworkCore;
using VarPlats.Core;

namespace VarPlats.Web.Data;

public sealed class DatabaseReportArchiveStore(
    IDbContextFactory<ReportDbContext> factory, ReportingMode mode) : IReportArchiveStore
{
    private const int ListLimit = 200;
    private const int HistoryLimit = 50;

    public async Task<bool> ArchiveAsync(Guid reportId, ReportArchiveDraft draft,
        CancellationToken cancellationToken = default)
    {
        RequireWrite();
        var (reason, note) = draft.Validated();
        var reasonValue = (int)reason;
        var archiveKind = (int)ArchiveEventKind.Archived;
        var now = DateTimeOffset.UtcNow;
        var eventId = Guid.NewGuid();
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var inserted = await db.Database.ExecuteSqlAsync($"""
            INSERT INTO "report_archives" ("ReportId", "Reason", "Note", "ArchivedAt")
            SELECT "Id", {reasonValue}, {note}, {now} FROM "reports" WHERE "Id" = {reportId}
            ON CONFLICT ("ReportId") DO NOTHING;
            """, cancellationToken);
        if (inserted != 1)
            return false;
        await db.Database.ExecuteSqlAsync($"""
            INSERT INTO "report_archive_events" ("Id", "ReportId", "Kind", "Reason", "Note", "CreatedAt")
            VALUES ({eventId}, {reportId}, {archiveKind}, {reasonValue}, {note}, {now});
            """, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return true;
    }

    public async Task<bool> RestoreAsync(Guid reportId, CancellationToken cancellationToken = default)
    {
        RequireWrite();
        var restoreKind = (int)ArchiveEventKind.Restored;
        var now = DateTimeOffset.UtcNow;
        var eventId = Guid.NewGuid();
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var deleted = await db.ReportArchives.Where(archive => archive.ReportId == reportId)
            .ExecuteDeleteAsync(cancellationToken);
        if (deleted != 1)
            return false;
        await db.Database.ExecuteSqlAsync($"""
            INSERT INTO "report_archive_events" ("Id", "ReportId", "Kind", "Reason", "Note", "CreatedAt")
            VALUES ({eventId}, {reportId}, {restoreKind}, NULL, NULL, {now});
            """, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return true;
    }

    public async Task<ReportArchive?> GetAsync(Guid reportId, CancellationToken cancellationToken = default)
    {
        RequireRead();
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        return await db.ReportArchives.AsNoTracking()
            .Where(archive => archive.ReportId == reportId)
            .Select(archive => new ReportArchive(archive.ReportId, archive.Reason, archive.Note, archive.ArchivedAt))
            .SingleOrDefaultAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<ArchivedReport>> ListAsync(CancellationToken cancellationToken = default)
    {
        RequireRead();
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        return await (
                from archive in db.ReportArchives.AsNoTracking()
                join report in db.Reports on archive.ReportId equals report.Id
                orderby archive.ArchivedAt descending, archive.ReportId
                select new ArchivedReport(report.Id, report.Title, archive.Reason, archive.ArchivedAt))
            .Take(ListLimit)
            .ToArrayAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<ReportArchiveEvent>> HistoryAsync(Guid reportId,
        CancellationToken cancellationToken = default)
    {
        RequireRead();
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        return await db.ReportArchiveEvents.AsNoTracking()
            .Where(item => item.ReportId == reportId)
            .OrderByDescending(item => item.CreatedAt).ThenBy(item => item.Id)
            .Take(HistoryLimit)
            .Select(item => new ReportArchiveEvent(item.Kind, item.Reason, item.Note, item.CreatedAt))
            .ToArrayAsync(cancellationToken);
    }

    private void RequireRead()
    {
        if (!mode.Archive)
            throw new InvalidOperationException("Arkivering är inte aktiverad.");
    }

    private void RequireWrite()
    {
        if (!mode.CanArchive)
            throw new InvalidOperationException("Arkivering kan inte ändras när skrivningar är avstängda.");
    }
}

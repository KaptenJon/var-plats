using Microsoft.EntityFrameworkCore;
using VarPlats.Core;

namespace VarPlats.Web.Data;

public sealed class DatabaseContentFlagStore(
    IDbContextFactory<ReportDbContext> factory, ReportingMode mode) : IContentFlagStore
{
    // Enough rows to fill the grouped queue; flags beyond it surface once older items are handled.
    private const int QueueRowLimit = 2000;

    public async Task<FlagResult> FlagAsync(Guid reportId, Guid? actionId, Guid accountId, ContentFlagDraft draft,
        CancellationToken cancellationToken = default)
    {
        if (!mode.CanFlag)
            throw new InvalidOperationException("Anmälan av innehåll är inte aktiverad.");
        if (accountId == Guid.Empty)
            throw new ArgumentException("Ett konto krävs.", nameof(accountId));
        var (reason, note) = draft.Validated();
        var flagId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        var since = now.AddDays(-1);
        var reasonValue = (int)reason;
        var limit = ContentFlagLimits.PerAccountPerDay;
        var lockKey = accountId.ToString();

        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        // Serialises one account's flags so parallel requests cannot pass the daily limit together.
        await db.Database.ExecuteSqlAsync(
            $"SELECT pg_advisory_xact_lock(hashtextextended({lockKey}, 0))", cancellationToken);
        var inserted = await db.Database.ExecuteSqlAsync($"""
            INSERT INTO "content_flags" ("Id", "ReportId", "ActionId", "ReporterId", "Reason", "Note", "CreatedAt")
            SELECT {flagId}, r."Id", {actionId}, {accountId}, {reasonValue}, {note}, {now}
            FROM "reports" r
            WHERE r."Id" = {reportId}
              AND ({actionId}::uuid IS NULL OR EXISTS (
                  SELECT 1 FROM "report_actions" a WHERE a."Id" = {actionId} AND a."ReportId" = r."Id"))
              AND (SELECT count(*) FROM "content_flags" f
                   WHERE f."ReporterId" = {accountId} AND f."CreatedAt" > {since}) < {limit}
            ON CONFLICT DO NOTHING;
            """, cancellationToken);
        if (inserted == 1)
        {
            await transaction.CommitAsync(cancellationToken);
            return FlagResult.Flagged;
        }

        var targetExists = actionId is { } action
            ? await db.ReportActions.AnyAsync(item => item.Id == action && item.ReportId == reportId, cancellationToken)
            : await db.Reports.AnyAsync(item => item.Id == reportId, cancellationToken);
        if (!targetExists)
            return FlagResult.NotFound;
        var alreadyFlagged = await db.ContentFlags.AnyAsync(flag => flag.ReportId == reportId
            && flag.ActionId == actionId && flag.ReporterId == accountId, cancellationToken);
        return alreadyFlagged ? FlagResult.AlreadyFlagged : FlagResult.LimitReached;
    }

    public async Task<IReadOnlyList<FlaggedContent>> ListOpenAsync(CancellationToken cancellationToken = default)
    {
        if (!mode.FlagQueue)
            throw new InvalidOperationException("Anmälan av innehåll är inte aktiverad.");
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        var rows = await (
                from flag in db.ContentFlags.AsNoTracking()
                where flag.DismissedAt == null
                join report in db.Reports on flag.ReportId equals report.Id
                orderby flag.CreatedAt, flag.Id
                select new
                {
                    flag.ReportId,
                    flag.ActionId,
                    flag.Reason,
                    flag.Note,
                    flag.CreatedAt,
                    report.Title,
                    ReportText = report.Description,
                    ActionText = db.ReportActions.Where(action => action.Id == flag.ActionId)
                        .Select(action => action.Description).FirstOrDefault(),
                    // Flags from the author never hide their own content (see FlagVisibility).
                    ByAuthor = flag.ActionId == null
                        ? db.ReportSubmissions.Any(submission => submission.PublishedReportId == flag.ReportId
                            && submission.AuthorId == flag.ReporterId)
                        : db.ReportActions.Any(action => action.Id == flag.ActionId
                            && action.AuthorId == flag.ReporterId)
                })
            .Take(QueueRowLimit)
            .ToArrayAsync(cancellationToken);

        return rows
            .GroupBy(row => (row.ReportId, row.ActionId))
            .Select(group =>
            {
                var first = group.First();
                return new FlaggedContent(
                    first.ReportId, first.ActionId, first.Title,
                    first.ActionId is null ? first.ReportText : first.ActionText ?? "",
                    group.Count(),
                    group.Select(row => row.Reason).Distinct().Order().ToArray(),
                    group.Where(row => row.Note is not null).Select(row => row.Note!).ToArray(),
                    first.CreatedAt,
                    mode.HideOnFlag && group.Count(row => !row.ByAuthor) >= mode.HideOnFlagMinimumFlags);
            })
            .OrderBy(item => item.FirstFlaggedAt)
            .Take(ContentFlagLimits.QueueSize)
            .ToArray();
    }

    public async Task<HiddenContent> GetHiddenAsync(
        Guid reportId, Guid? viewerId, CancellationToken cancellationToken = default)
    {
        if (!mode.HideOnFlag)
            throw new InvalidOperationException("Dölj vid anmälan är inte aktiverat.");
        var minimum = mode.HideOnFlagMinimumFlags;
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        var reportHidden = await FlagVisibility.IsReportHiddenAsync(db, reportId, minimum, cancellationToken);
        var viewerIsAuthor = viewerId is { } viewer && viewer != Guid.Empty
            && await db.ReportSubmissions.AnyAsync(submission => submission.PublishedReportId == reportId
                && submission.AuthorId == viewer, cancellationToken);
        var hiddenActions = await FlagVisibility.HiddenActionIdsForReportAsync(db, reportId, minimum, cancellationToken);
        return new(reportHidden, viewerIsAuthor, hiddenActions.ToHashSet());
    }

    public async Task<int> DismissAsync(Guid reportId, Guid? actionId, CancellationToken cancellationToken = default)
    {
        if (!mode.CanFlag)
            throw new InvalidOperationException("Anmälningar kan inte hanteras när skrivningar är avstängda.");
        var now = DateTimeOffset.UtcNow;
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        // Dismissed rows are kept so the same account cannot flag the same item again.
        return await db.ContentFlags
            .Where(flag => flag.ReportId == reportId && flag.ActionId == actionId && flag.DismissedAt == null)
            .ExecuteUpdateAsync(setters => setters.SetProperty(flag => flag.DismissedAt, now), cancellationToken);
    }
}

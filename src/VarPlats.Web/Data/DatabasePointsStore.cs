using Microsoft.EntityFrameworkCore;
using VarPlats.Core;

namespace VarPlats.Web.Data;

public sealed class DatabasePointsStore(
    IDbContextFactory<ReportDbContext> factory, ReportingMode mode, PointsOptions options) : IPointsStore
{
    private const int HistoryLimit = 200;
    private const int ActionLimit = 200;

    public async Task<IReadOnlyDictionary<Guid, ActionAppreciation>> ListForActionsAsync(
        IReadOnlyCollection<Guid> actionIds, Guid? viewerId, CancellationToken cancellationToken = default)
    {
        RequireRead();
        var ids = actionIds.Distinct().Take(ActionLimit).ToArray();
        if (ids.Length == 0)
            return new Dictionary<Guid, ActionAppreciation>();
        var hasViewer = viewerId.HasValue && viewerId.Value != Guid.Empty;
        var accountId = viewerId.GetValueOrDefault();
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        // Points shown on an action: its award (net of reversals) plus gifts received for it. The giving side of
        // a gift (Reason GiftSent) also carries the action id but belongs to the giver, so it is left out.
        var rows = await db.ReportActions.AsNoTracking()
            .Where(action => ids.Contains(action.Id))
            .Select(action => new ActionAppreciation(
                action.Id,
                db.ActionHearts.Count(heart => heart.ActionId == action.Id),
                db.PointsEvents.Where(item => item.ActionId == action.Id && item.Reason != PointsReason.GiftSent)
                    .Sum(item => item.Amount),
                hasViewer && db.ActionHearts.Any(heart => heart.ActionId == action.Id && heart.AccountId == accountId)))
            .ToArrayAsync(cancellationToken);
        return rows.ToDictionary(row => row.ActionId);
    }

    public async Task<HeartResult> ToggleHeartAsync(
        Guid reportId, Guid actionId, Guid accountId, CancellationToken cancellationToken = default)
    {
        RequireWrite();
        RequireAccount(accountId);
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var action = await db.ReportActions.AsNoTracking()
            .Where(item => item.Id == actionId && item.ReportId == reportId)
            .Select(item => new { item.AuthorId })
            .SingleOrDefaultAsync(cancellationToken);
        if (action is null)
            return HeartResult.NotFound;
        if (action.AuthorId == accountId)
            return HeartResult.OwnAction;
        // Removing a heart is always allowed; it never changes points.
        if (await db.ActionHearts.Where(heart => heart.ActionId == actionId && heart.AccountId == accountId)
                .ExecuteDeleteAsync(cancellationToken) > 0)
        {
            await transaction.CommitAsync(cancellationToken);
            return HeartResult.Removed;
        }
        if (await IsArchivedAsync(db, reportId, cancellationToken))
            return HeartResult.Archived;
        var now = DateTimeOffset.UtcNow;
        var inserted = await db.Database.ExecuteSqlAsync($"""
            INSERT INTO "action_hearts" ("ActionId", "AccountId", "CreatedAt")
            SELECT a."Id", {accountId}, {now}
            FROM "report_actions" a
            WHERE a."Id" = {actionId} AND a."ReportId" = {reportId} AND a."AuthorId" IS DISTINCT FROM {accountId}
            ON CONFLICT ("ActionId", "AccountId") DO NOTHING;
            """, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return inserted == 1 ? HeartResult.Added : HeartResult.NotFound;
    }

    public async Task<GiftResult> GiftAsync(
        Guid reportId, Guid actionId, Guid giverId, int amount, CancellationToken cancellationToken = default)
    {
        RequireWrite();
        RequireAccount(giverId);
        if (!options.GiftsEnabled)
            throw new InvalidOperationException("Poänggåvor är inte aktiverade.");
        if (amount < 1)
            throw new ArgumentOutOfRangeException(nameof(amount));
        if (amount > options.MaxGiftPerTransfer)
            return GiftResult.OverTransferLimit;

        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        await PointsLedger.LockAccountAsync(db, giverId, cancellationToken);
        var action = await db.ReportActions.AsNoTracking()
            .Where(item => item.Id == actionId && item.ReportId == reportId)
            .Select(item => new { item.AuthorId })
            .SingleOrDefaultAsync(cancellationToken);
        if (action is null)
            return GiftResult.NotFound;
        if (action.AuthorId == giverId)
            return GiftResult.OwnAction;
        if (action.AuthorId is null)
            return GiftResult.NoRecipient;
        if (await IsArchivedAsync(db, reportId, cancellationToken))
            return GiftResult.Archived;
        var now = DateTimeOffset.UtcNow;
        var balance = await BalanceAsync(db, giverId, cancellationToken);
        if (balance < amount)
            return GiftResult.InsufficientBalance;
        if (await GiftedSinceAsync(db, giverId, now.AddDays(-1), cancellationToken) + amount > options.MaxGiftPerDay)
            return GiftResult.OverDailyLimit;

        // Both rows come from the action row in one statement, so a concurrently removed action writes nothing.
        var transferId = Guid.NewGuid();
        var sentId = Guid.NewGuid();
        var receivedId = Guid.NewGuid();
        var negative = -amount;
        var ruleVersion = options.RuleVersion;
        var inserted = await db.Database.ExecuteSqlAsync($"""
            INSERT INTO "points_events" ("Id", "AccountId", "Amount", "Reason", "ActionId", "ConfirmerId",
                "TransferId", "ReversesEventId", "ReversalCause", "RuleVersion", "CreatedAt")
            SELECT {sentId}, {giverId}, {negative}, 2, a."Id", NULL, {transferId}, NULL, NULL, {ruleVersion}, {now}
            FROM "report_actions" a
            WHERE a."Id" = {actionId} AND a."ReportId" = {reportId} AND a."AuthorId" IS NOT NULL
              AND a."AuthorId" <> {giverId}
            UNION ALL
            SELECT {receivedId}, a."AuthorId", {amount}, 3, a."Id", NULL, {transferId}, NULL, NULL, {ruleVersion}, {now}
            FROM "report_actions" a
            WHERE a."Id" = {actionId} AND a."ReportId" = {reportId} AND a."AuthorId" IS NOT NULL
              AND a."AuthorId" <> {giverId};
            """, cancellationToken);
        if (inserted != 2)
            return GiftResult.NotFound;
        await transaction.CommitAsync(cancellationToken);
        return GiftResult.Sent;
    }

    public async Task<PointsSummary> GetSummaryAsync(Guid accountId, CancellationToken cancellationToken = default)
    {
        RequireRead();
        RequireAccount(accountId);
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        var balance = await BalanceAsync(db, accountId, cancellationToken);
        var giftedToday = await GiftedSinceAsync(db, accountId, DateTimeOffset.UtcNow.AddDays(-1), cancellationToken);
        var history = await db.PointsEvents.AsNoTracking()
            .Where(item => item.AccountId == accountId)
            .OrderByDescending(item => item.CreatedAt).ThenBy(item => item.Id)
            .Take(HistoryLimit)
            .Select(item => new PointsEvent(
                item.Id, item.Amount, item.Reason, item.ReversalCause,
                db.ReportActions.Where(action => action.Id == item.ActionId)
                    .Select(action => (Guid?)action.ReportId).FirstOrDefault(),
                item.RuleVersion, item.CreatedAt))
            .ToArrayAsync(cancellationToken);
        return new(balance, giftedToday, history);
    }

    public async Task<int> ReverseConfirmationAsync(Guid actionId, Guid confirmerId, PointsReversalCause cause,
        CancellationToken cancellationToken = default)
    {
        RequireWrite();
        RequireAccount(confirmerId);
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        await PointsLedger.LockActionAsync(db, actionId, cancellationToken);
        var reversed = await PointsLedger.ReverseAwardsAsync(db, actionId, confirmerId, cause, options, cancellationToken);
        if (reversed > 0)
            await PointsLedger.AwardAsync(db, actionId, confirmerId, options, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return reversed;
    }

    public async Task<int> ReverseActionAsync(Guid actionId, PointsReversalCause cause,
        CancellationToken cancellationToken = default)
    {
        RequireWrite();
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        await PointsLedger.LockActionAsync(db, actionId, cancellationToken);
        var reversed = await PointsLedger.ReverseAwardsAsync(db, actionId, null, cause, options, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return reversed;
    }

    private static Task<int> BalanceAsync(ReportDbContext db, Guid accountId, CancellationToken cancellationToken)
        => db.PointsEvents.Where(item => item.AccountId == accountId).SumAsync(item => item.Amount, cancellationToken);

    private static async Task<int> GiftedSinceAsync(ReportDbContext db, Guid accountId, DateTimeOffset since,
        CancellationToken cancellationToken)
        => -await db.PointsEvents
            .Where(item => item.AccountId == accountId && item.Reason == PointsReason.GiftSent && item.CreatedAt > since)
            .SumAsync(item => item.Amount, cancellationToken);

    // The archive table is only named when its own switch is on (see DatabaseReportActionStore.AddAsync).
    private Task<bool> IsArchivedAsync(ReportDbContext db, Guid reportId, CancellationToken cancellationToken)
        => mode.Archive
            ? db.ReportArchives.AnyAsync(archive => archive.ReportId == reportId, cancellationToken)
            : Task.FromResult(false);

    private void RequireRead()
    {
        if (!mode.Points)
            throw new InvalidOperationException("Poäng är inte aktiverade.");
    }

    private void RequireWrite()
    {
        if (!mode.CanUsePoints)
            throw new InvalidOperationException("Poäng kan inte ändras när skrivningar är avstängda.");
    }

    private static void RequireAccount(Guid accountId)
    {
        if (accountId == Guid.Empty)
            throw new ArgumentException("Ett konto krävs.", nameof(accountId));
    }
}

using Microsoft.EntityFrameworkCore;
using VarPlats.Core;

namespace VarPlats.Web.Data;

public sealed class DatabaseReportActionStore(
    IDbContextFactory<ReportDbContext> factory, ReportingMode mode, PointsOptions? points = null) : IReportActionStore
{
    public async Task<IReadOnlyList<ReportAction>> ListAsync(
        Guid reportId, Guid? viewerId, CancellationToken cancellationToken = default)
    {
        RequireWorkflow();
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        var hasViewer = viewerId.HasValue && viewerId.Value != Guid.Empty;
        var accountId = viewerId.GetValueOrDefault();
        var actions = db.ReportActions.AsNoTracking()
            .Where(action => action.ReportId == reportId)
            .OrderByDescending(action => action.CreatedAt)
            .ThenBy(action => action.Id)
            .Take(200);
        // The withdrawal table is only queried when the switch is on, so older schemas keep working.
        if (!mode.ConfirmationWithdrawal)
            return await actions.Select(action => new ReportAction(
                    action.Id, action.Description, action.CreatedAt,
                    db.ReportActionConfirmations.Count(confirmation => confirmation.ActionId == action.Id),
                    hasViewer && action.AuthorId == accountId,
                    hasViewer && db.ReportActionConfirmations.Any(confirmation =>
                        confirmation.ActionId == action.Id && confirmation.AccountId == accountId),
                    0, false))
                .ToArrayAsync(cancellationToken);
        return await actions.Select(action => new ReportAction(
                action.Id, action.Description, action.CreatedAt,
                db.ReportActionConfirmations.Count(confirmation => confirmation.ActionId == action.Id),
                hasViewer && action.AuthorId == accountId,
                hasViewer && db.ReportActionConfirmations.Any(confirmation =>
                    confirmation.ActionId == action.Id && confirmation.AccountId == accountId),
                db.ReportActionConfirmationWithdrawals.Count(item => item.ActionId == action.Id),
                hasViewer && db.ReportActionConfirmationWithdrawals.Any(item =>
                    item.ActionId == action.Id && item.AccountId == accountId)))
            .ToArrayAsync(cancellationToken);
    }

    public async Task<bool> AddAsync(
        Guid reportId, Guid authorId, ReportActionDraft draft, CancellationToken cancellationToken = default)
    {
        RequireWrite();
        RequireAccount(authorId);
        var description = draft.ValidatedDescription();
        var actionId = Guid.NewGuid();
        var createdAt = DateTimeOffset.UtcNow;
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        // A report hidden by flags takes no new actions until a moderator restores it.
        if (mode.HideOnFlag && await FlagVisibility.IsReportHiddenAsync(
                db, reportId, mode.HideOnFlagMinimumFlags, cancellationToken))
            return false;
        // Archived reports take no new actions. The archive table is only named when the switch is on, because
        // PostgreSQL rejects a statement that mentions a missing table even in an unused branch.
        var inserted = mode.Archive
            ? await db.Database.ExecuteSqlAsync($"""
                INSERT INTO "report_actions" ("Id", "ReportId", "AuthorId", "Description", "CreatedAt")
                SELECT {actionId}, r."Id", {authorId}, {description}, {createdAt}
                FROM "reports" r WHERE r."Id" = {reportId}
                  AND NOT EXISTS (SELECT 1 FROM "report_archives" a WHERE a."ReportId" = r."Id");
                """, cancellationToken)
            : await db.Database.ExecuteSqlAsync($"""
                INSERT INTO "report_actions" ("Id", "ReportId", "AuthorId", "Description", "CreatedAt")
                SELECT {actionId}, "Id", {authorId}, {description}, {createdAt}
                FROM "reports" WHERE "Id" = {reportId};
                """, cancellationToken);
        return inserted == 1;
    }

    public async Task<ActionConfirmationResult> ConfirmAsync(
        Guid reportId, Guid actionId, Guid accountId, CancellationToken cancellationToken = default)
    {
        RequireWrite();
        RequireAccount(accountId);
        var createdAt = DateTimeOffset.UtcNow;
        var pointsOptions = mode.Points ? Points() : null;
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        // With points on, the confirmation and its award commit together. Without points no ledger table is named.
        await using var transaction = mode.Points ? await db.Database.BeginTransactionAsync(cancellationToken) : null;
        if (transaction is not null)
            await PointsLedger.LockActionAsync(db, actionId, cancellationToken);
        // Hidden content earns no new confirmations (and so no points) while it waits for review.
        if (mode.HideOnFlag && await FlagVisibility.IsActionOrReportHiddenAsync(
                db, reportId, actionId, mode.HideOnFlagMinimumFlags, cancellationToken))
            return await db.ReportActions.AnyAsync(
                    action => action.Id == actionId && action.ReportId == reportId, cancellationToken)
                ? ActionConfirmationResult.Hidden : ActionConfirmationResult.NotFound;
        // A withdrawn confirmation cannot be given again, so the status cannot be flipped back and forth.
        // The withdrawal table is only named when the switch is on, because PostgreSQL rejects a statement
        // that mentions a missing table even in an unused branch.
        var inserted = mode.ConfirmationWithdrawal
            ? await db.Database.ExecuteSqlAsync($"""
                INSERT INTO "report_action_confirmations" ("ActionId", "AccountId", "CreatedAt")
                SELECT a."Id", {accountId}, {createdAt}
                FROM "report_actions" a
                WHERE a."Id" = {actionId} AND a."ReportId" = {reportId}
                    AND a."AuthorId" IS DISTINCT FROM {accountId}
                    AND NOT EXISTS (SELECT 1 FROM "report_action_confirmation_withdrawals" w
                        WHERE w."ActionId" = a."Id" AND w."AccountId" = {accountId})
                ON CONFLICT ("ActionId", "AccountId") DO NOTHING;
                """, cancellationToken)
            : await db.Database.ExecuteSqlAsync($"""
                INSERT INTO "report_action_confirmations" ("ActionId", "AccountId", "CreatedAt")
                SELECT "Id", {accountId}, {createdAt}
                FROM "report_actions"
                WHERE "Id" = {actionId} AND "ReportId" = {reportId}
                    AND "AuthorId" IS DISTINCT FROM {accountId}
                ON CONFLICT ("ActionId", "AccountId") DO NOTHING;
                """, cancellationToken);
        if (inserted == 1)
        {
            if (transaction is not null)
            {
                await PointsLedger.AwardAsync(db, actionId, null, pointsOptions!, cancellationToken);
                await transaction.CommitAsync(cancellationToken);
            }
            return ActionConfirmationResult.Confirmed;
        }

        var action = await db.ReportActions.AsNoTracking()
            .Where(candidate => candidate.Id == actionId && candidate.ReportId == reportId)
            .Select(candidate => new { candidate.AuthorId })
            .SingleOrDefaultAsync(cancellationToken);
        if (action is null)
            return ActionConfirmationResult.NotFound;
        if (action.AuthorId == accountId)
            return ActionConfirmationResult.OwnAction;
        if (!mode.ConfirmationWithdrawal || await db.ReportActionConfirmations.AnyAsync(confirmation =>
                confirmation.ActionId == actionId && confirmation.AccountId == accountId, cancellationToken))
            return ActionConfirmationResult.AlreadyConfirmed;
        return ActionConfirmationResult.Withdrawn;
    }

    public async Task<ConfirmationWithdrawalResult> WithdrawConfirmationAsync(
        Guid reportId, Guid actionId, Guid accountId, CancellationToken cancellationToken = default)
    {
        if (!mode.CanWithdrawConfirmation)
            throw new InvalidOperationException("Återtagande av bekräftelser är inte aktiverat.");
        RequireAccount(accountId);
        var historyId = Guid.NewGuid();
        var withdrawnAt = DateTimeOffset.UtcNow;
        var pointsOptions = mode.Points ? Points() : null;
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        // With points on, the withdrawal and the reversal of the points it awarded commit together.
        await using var transaction = pointsOptions is null ? null : await db.Database.BeginTransactionAsync(cancellationToken);
        if (transaction is not null)
            await PointsLedger.LockActionAsync(db, actionId, cancellationToken);
        // One statement moves the account's own active row into the history, so a concurrent repeat finds
        // nothing to move. Archived reports refuse the change like they refuse new actions; the archive table
        // is only named when its switch is on.
        var moved = mode.Archive
            ? await db.Database.ExecuteSqlAsync($"""
                WITH removed AS (
                    DELETE FROM "report_action_confirmations" c
                    USING "report_actions" a
                    WHERE c."ActionId" = a."Id" AND a."Id" = {actionId} AND a."ReportId" = {reportId}
                        AND c."AccountId" = {accountId}
                        AND NOT EXISTS (SELECT 1 FROM "report_archives" r WHERE r."ReportId" = a."ReportId")
                    RETURNING c."ActionId", c."AccountId", c."CreatedAt")
                INSERT INTO "report_action_confirmation_withdrawals"
                    ("Id", "ActionId", "AccountId", "ConfirmedAt", "WithdrawnAt")
                SELECT {historyId}, "ActionId", "AccountId", "CreatedAt", GREATEST("CreatedAt", {withdrawnAt})
                FROM removed;
                """, cancellationToken)
            : await db.Database.ExecuteSqlAsync($"""
                WITH removed AS (
                    DELETE FROM "report_action_confirmations" c
                    USING "report_actions" a
                    WHERE c."ActionId" = a."Id" AND a."Id" = {actionId} AND a."ReportId" = {reportId}
                        AND c."AccountId" = {accountId}
                    RETURNING c."ActionId", c."AccountId", c."CreatedAt")
                INSERT INTO "report_action_confirmation_withdrawals"
                    ("Id", "ActionId", "AccountId", "ConfirmedAt", "WithdrawnAt")
                SELECT {historyId}, "ActionId", "AccountId", "CreatedAt", GREATEST("CreatedAt", {withdrawnAt})
                FROM removed;
                """, cancellationToken);
        if (moved == 1)
        {
            if (transaction is not null)
            {
                // Takes back the award this confirmation gave and awards from another remaining confirmation, if any.
                if (await PointsLedger.ReverseAwardsAsync(db, actionId, accountId,
                        PointsReversalCause.ConfirmationWithdrawn, pointsOptions!, cancellationToken) > 0)
                    await PointsLedger.AwardAsync(db, actionId, accountId, pointsOptions!, cancellationToken);
                await transaction.CommitAsync(cancellationToken);
            }
            return ConfirmationWithdrawalResult.Withdrawn;
        }

        if (!await db.ReportActions.AnyAsync(action => action.Id == actionId && action.ReportId == reportId,
                cancellationToken))
            return ConfirmationWithdrawalResult.NotFound;
        if (mode.Archive
            && await db.ReportArchives.AnyAsync(archive => archive.ReportId == reportId, cancellationToken))
            return ConfirmationWithdrawalResult.Archived;
        return ConfirmationWithdrawalResult.NotConfirmed;
    }

    public async Task<bool> RemoveAsync(
        Guid reportId, Guid actionId, CancellationToken cancellationToken = default)
    {
        RequireWrite();
        var pointsOptions = mode.Points ? Points() : null;
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        if (pointsOptions is null)
            return await db.ReportActions.Where(action => action.Id == actionId && action.ReportId == reportId)
                .ExecuteDeleteAsync(cancellationToken) > 0;

        // Admin removal takes back the award in the same transaction. The ledger keeps the action id for history.
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        await PointsLedger.LockActionAsync(db, actionId, cancellationToken);
        var removed = await db.ReportActions.Where(action => action.Id == actionId && action.ReportId == reportId)
            .ExecuteDeleteAsync(cancellationToken) > 0;
        if (removed)
            await PointsLedger.ReverseAwardsAsync(db, actionId, null, PointsReversalCause.ActionRemoved, pointsOptions,
                cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return removed;
    }

    private PointsOptions Points()
        => points ?? throw new InvalidOperationException("Poänginställningar saknas.");

    private void RequireWorkflow()
    {
        if (!mode.WorkflowEnabled)
            throw new InvalidOperationException("Åtgärder är inte aktiverade.");
    }

    private void RequireWrite()
    {
        if (!mode.PublicWorkflow)
            throw new InvalidOperationException("Åtgärder är inte aktiverade för skrivning.");
    }

    private static void RequireAccount(Guid accountId)
    {
        if (accountId == Guid.Empty)
            throw new ArgumentException("Ett konto krävs.", nameof(accountId));
    }
}

using Microsoft.EntityFrameworkCore;
using VarPlats.Core;

namespace VarPlats.Web.Data;

public sealed class DatabaseReportActionStore(
    IDbContextFactory<ReportDbContext> factory, ReportingMode mode) : IReportActionStore
{
    public async Task<IReadOnlyList<ReportAction>> ListAsync(
        Guid reportId, Guid? viewerId, CancellationToken cancellationToken = default)
    {
        RequireWorkflow();
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        var hasViewer = viewerId.HasValue && viewerId.Value != Guid.Empty;
        var accountId = viewerId.GetValueOrDefault();
        return await db.ReportActions.AsNoTracking()
            .Where(action => action.ReportId == reportId)
            .OrderByDescending(action => action.CreatedAt)
            .ThenBy(action => action.Id)
            .Take(200)
            .Select(action => new ReportAction(
                action.Id, action.Description, action.CreatedAt,
                db.ReportActionConfirmations.Count(confirmation => confirmation.ActionId == action.Id),
                hasViewer && action.AuthorId == accountId,
                hasViewer && db.ReportActionConfirmations.Any(confirmation =>
                    confirmation.ActionId == action.Id && confirmation.AccountId == accountId)))
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
        var inserted = await db.Database.ExecuteSqlAsync($"""
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
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        var inserted = await db.Database.ExecuteSqlAsync($"""
            INSERT INTO "report_action_confirmations" ("ActionId", "AccountId", "CreatedAt")
            SELECT "Id", {accountId}, {createdAt}
            FROM "report_actions"
            WHERE "Id" = {actionId} AND "ReportId" = {reportId}
                AND "AuthorId" IS DISTINCT FROM {accountId}
            ON CONFLICT ("ActionId", "AccountId") DO NOTHING;
            """, cancellationToken);
        if (inserted == 1)
            return ActionConfirmationResult.Confirmed;

        var action = await db.ReportActions.AsNoTracking()
            .Where(candidate => candidate.Id == actionId && candidate.ReportId == reportId)
            .Select(candidate => new { candidate.AuthorId })
            .SingleOrDefaultAsync(cancellationToken);
        return action is null ? ActionConfirmationResult.NotFound
            : action.AuthorId == accountId ? ActionConfirmationResult.OwnAction
            : ActionConfirmationResult.AlreadyConfirmed;
    }

    public async Task<bool> RemoveAsync(
        Guid reportId, Guid actionId, CancellationToken cancellationToken = default)
    {
        RequireWrite();
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        return await db.ReportActions.Where(action => action.Id == actionId && action.ReportId == reportId)
            .ExecuteDeleteAsync(cancellationToken) > 0;
    }

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

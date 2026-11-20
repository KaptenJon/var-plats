using Microsoft.EntityFrameworkCore;
using VarPlats.Core;

namespace VarPlats.Web.Data;

/// <summary>
/// Ledger statements shared by the points, action and report stores so awards and reversals happen in the
/// caller's transaction. Callers only use these when <see cref="ReportingMode.Points"/> is on, because the
/// statements name points_events, which does not exist before the Points migration.
/// </summary>
internal static class PointsLedger
{
    /// <summary>Serialises ledger changes for one action, so two confirmations cannot both award.</summary>
    public static Task LockActionAsync(ReportDbContext db, Guid actionId, CancellationToken cancellationToken)
    {
        var key = $"points-action:{actionId}";
        return db.Database.ExecuteSqlAsync($"SELECT pg_advisory_xact_lock(hashtextextended({key}, 0))", cancellationToken);
    }

    /// <summary>Serialises one account's gifts so parallel requests cannot pass the balance or daily limit.</summary>
    public static Task LockAccountAsync(ReportDbContext db, Guid accountId, CancellationToken cancellationToken)
    {
        var key = $"points-account:{accountId}";
        return db.Database.ExecuteSqlAsync($"SELECT pg_advisory_xact_lock(hashtextextended({key}, 0))", cancellationToken);
    }

    /// <summary>
    /// Awards base points to the action's author from the oldest confirmation by another account, unless the
    /// action already has an unreversed award or that confirmation has awarded before. One award per action,
    /// however many accounts confirm it. Returns 1 when a row was written.
    /// </summary>
    public static Task<int> AwardAsync(ReportDbContext db, Guid actionId, Guid? excludedConfirmer,
        PointsOptions options, CancellationToken cancellationToken)
    {
        var eventId = Guid.NewGuid();
        var amount = options.ConfirmedActionPoints;
        var ruleVersion = options.RuleVersion;
        var now = DateTimeOffset.UtcNow;
        return db.Database.ExecuteSqlAsync($"""
            INSERT INTO "points_events" ("Id", "AccountId", "Amount", "Reason", "ActionId", "ConfirmerId",
                "TransferId", "ReversesEventId", "ReversalCause", "RuleVersion", "CreatedAt")
            SELECT {eventId}, a."AuthorId", {amount}, 0, a."Id", c."AccountId", NULL, NULL, NULL, {ruleVersion}, {now}
            FROM "report_actions" a
            JOIN "report_action_confirmations" c ON c."ActionId" = a."Id"
            WHERE a."Id" = {actionId} AND a."AuthorId" IS NOT NULL AND c."AccountId" <> a."AuthorId"
              AND ({excludedConfirmer}::uuid IS NULL OR c."AccountId" <> {excludedConfirmer})
              AND NOT EXISTS (
                  SELECT 1 FROM "points_events" e
                  WHERE e."ActionId" = a."Id" AND e."Reason" = 0
                    AND NOT EXISTS (SELECT 1 FROM "points_events" r WHERE r."ReversesEventId" = e."Id"))
              AND NOT EXISTS (
                  SELECT 1 FROM "points_events" p
                  WHERE p."ActionId" = a."Id" AND p."Reason" = 0 AND p."ConfirmerId" = c."AccountId")
            ORDER BY c."CreatedAt", c."AccountId"
            LIMIT 1
            ON CONFLICT DO NOTHING;
            """, cancellationToken);
    }

    /// <summary>
    /// Writes a compensating row for every unreversed award of the action, optionally only the one that came
    /// from a given confirming account. Gifts are not reversed. Returns the number of rows written.
    /// </summary>
    public static Task<int> ReverseAwardsAsync(ReportDbContext db, Guid actionId, Guid? confirmerId,
        PointsReversalCause cause, PointsOptions options, CancellationToken cancellationToken)
    {
        var causeValue = (int)cause;
        var ruleVersion = options.RuleVersion;
        var now = DateTimeOffset.UtcNow;
        return db.Database.ExecuteSqlAsync($"""
            INSERT INTO "points_events" ("Id", "AccountId", "Amount", "Reason", "ActionId", "ConfirmerId",
                "TransferId", "ReversesEventId", "ReversalCause", "RuleVersion", "CreatedAt")
            SELECT gen_random_uuid(), e."AccountId", -e."Amount", 1, e."ActionId", NULL, NULL, e."Id", {causeValue},
                {ruleVersion}, {now}
            FROM "points_events" e
            WHERE e."ActionId" = {actionId} AND e."Reason" = 0
              AND ({confirmerId}::uuid IS NULL OR e."ConfirmerId" = {confirmerId})
              AND NOT EXISTS (SELECT 1 FROM "points_events" r WHERE r."ReversesEventId" = e."Id")
            ON CONFLICT DO NOTHING;
            """, cancellationToken);
    }

    /// <summary>Reverses the awards of every action on a report, before the report and its actions are deleted.</summary>
    public static async Task<int> ReverseReportAsync(ReportDbContext db, Guid reportId, PointsOptions options,
        CancellationToken cancellationToken)
    {
        var actionIds = await db.ReportActions.AsNoTracking()
            .Where(action => action.ReportId == reportId)
            .OrderBy(action => action.Id)
            .Select(action => action.Id)
            .ToArrayAsync(cancellationToken);
        var reversed = 0;
        foreach (var actionId in actionIds)
        {
            await LockActionAsync(db, actionId, cancellationToken);
            reversed += await ReverseAwardsAsync(db, actionId, null, PointsReversalCause.ReportRemoved, options,
                cancellationToken);
        }
        return reversed;
    }
}

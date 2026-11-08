using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace VarPlats.Web.Data;

public static class IdentityAccountRetentionPolicy
{
    public static DateTimeOffset GetInactiveCutoff(DateTimeOffset now)
        => now.AddMonths(-12);

    public static bool IsInactive(IdentityAccountActivity activity, DateTimeOffset cutoff)
        => (activity.LastSignInAtUtc ?? activity.CreatedAtUtc) <= cutoff;
}

public sealed class IdentityAccountActivityStore(ReportDbContext db)
{
    public Task RecordSignInAsync(
        Guid userId, DateTimeOffset signedInAtUtc, CancellationToken cancellationToken = default)
        => db.Database.ExecuteSqlAsync($"""
            INSERT INTO "IdentityAccountActivity" ("UserId", "CreatedAtUtc", "LastSignInAtUtc")
            VALUES ({userId}, CURRENT_TIMESTAMP, {signedInAtUtc})
            ON CONFLICT ("UserId") DO UPDATE
            SET "LastSignInAtUtc" = EXCLUDED."LastSignInAtUtc";
            """, cancellationToken);

    public async Task<int> DeleteInactiveAccountsAsync(
        UserManager<IdentityUser<Guid>> userManager,
        DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        var cutoff = IdentityAccountRetentionPolicy.GetInactiveCutoff(now);
        var deleted = 0;
        while (true)
        {
            var userIds = await db.IdentityAccountActivities.AsNoTracking()
                .Where(activity => activity.LastSignInAtUtc != null
                    ? activity.LastSignInAtUtc <= cutoff
                    : activity.CreatedAtUtc <= cutoff)
                .OrderBy(activity => activity.CreatedAtUtc)
                .Select(activity => activity.UserId)
                .Take(100)
                .ToArrayAsync(cancellationToken);
            if (userIds.Length == 0)
                return deleted;

            var batchDeleted = 0;
            foreach (var userId in userIds)
            {
                await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
                var lockedRows = await db.IdentityAccountActivities
                    .FromSql($"""
                        SELECT "UserId", "CreatedAtUtc", "LastSignInAtUtc"
                        FROM "IdentityAccountActivity"
                        WHERE "UserId" = {userId}
                        FOR UPDATE
                        """)
                    .AsNoTracking()
                    .ToListAsync(cancellationToken);
                var activity = lockedRows.SingleOrDefault();
                if (activity is null || !IdentityAccountRetentionPolicy.IsInactive(activity, cutoff))
                {
                    await transaction.CommitAsync(cancellationToken);
                    continue;
                }

                var user = await userManager.FindByIdAsync(userId.ToString());
                if (user is null)
                    throw new InvalidOperationException("An account activity row has no matching Identity account.");
                var result = await userManager.DeleteAsync(user);
                if (!result.Succeeded)
                    throw new InvalidOperationException("An inactive Identity account could not be deleted.");

                await transaction.CommitAsync(cancellationToken);
                deleted++;
                batchDeleted++;
            }

            if (batchDeleted == 0)
                return deleted;
        }
    }
}

using System.Security.Claims;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using VarPlats.Core;
using VarPlats.Web;
using VarPlats.Web.Data;
using Xunit;

namespace VarPlats.Tests;

/// <summary>
/// Points ledger checks against the isolated PostGIS database, run by <see cref="ReportWorkflowDatabaseTests"/>
/// right after the Points migration. Uses its own synthetic report and accounts and removes them at the end.
/// </summary>
internal static class PointsDatabaseScenario
{
    public static async Task RunAsync(
        IDbContextFactory<ReportDbContext> contexts, UserManager<IdentityUser<Guid>> userManager)
    {
        var author = await CreateUserAsync(userManager, "synthetic-points-author");
        var second = await CreateUserAsync(userManager, "synthetic-points-second");
        var third = await CreateUserAsync(userManager, "synthetic-points-third");
        var authorAccount = Account(author, "Member");
        var secondAccount = Account(second, "Member");
        var thirdAccount = Account(third, "Member");
        var admin = Account(Guid.NewGuid(), "Admin");
        var reportId = Guid.NewGuid();
        await using (var db = contexts.CreateDbContext())
            await db.Database.ExecuteSqlAsync($"""
                INSERT INTO reports ("Id", "Title", "Description", "Category", "Location", "CreatedAt", "Status")
                VALUES ({reportId}, 'Syntetisk poängrapport', 'Ett påhittat exempel utan personuppgifter.', 0,
                    ST_SetSRID(ST_Point(11.97, 57.7), 4326)::geography, now(), 0);
                """);

        var options = new PointsOptions { ConfirmedActionPoints = 10, MaxGiftPerTransfer = 5, MaxGiftPerDay = 8 };
        var mode = new ReportingMode(false, false, true, true, false, true, true);
        var off = new ReportingMode(false, false, true, true, false, true);
        var actions = new ReportActionCommands(mode, new DatabaseReportActionStore(contexts, mode, options),
            accountAccess: SyntheticReportAccountAccess.Instance);
        var offActions = new ReportActionCommands(off, new DatabaseReportActionStore(contexts, off),
            accountAccess: SyntheticReportAccountAccess.Instance);
        var store = new DatabasePointsStore(contexts, mode, options);
        var points = new PointsCommands(mode, options, store, SyntheticReportAccountAccess.Instance);
        var archives = new DatabaseReportArchiveStore(contexts, mode);

        // Switch off: confirming touches no points table.
        var offAction = await AddActionAsync(actions, authorAccount, reportId, "Syntetisk åtgärd utan poäng.");
        Assert.Equal(ActionConfirmationResult.Confirmed, await offActions.ConfirmAsync(secondAccount, reportId, offAction));
        Assert.Equal(0, await CountAsync(contexts));

        // Base points: only another account's confirmation earns them, once per action.
        var cleaned = await AddActionAsync(actions, authorAccount, reportId, "Syntetisk städning av en plats.");
        Assert.Equal(ActionConfirmationResult.OwnAction, await actions.ConfirmAsync(authorAccount, reportId, cleaned));
        Assert.Equal(0, await CountAsync(contexts));
        Assert.Equal(ActionConfirmationResult.Confirmed, await actions.ConfirmAsync(secondAccount, reportId, cleaned));
        Assert.Equal(ActionConfirmationResult.Confirmed, await actions.ConfirmAsync(thirdAccount, reportId, cleaned));
        Assert.Equal(ActionConfirmationResult.AlreadyConfirmed, await actions.ConfirmAsync(thirdAccount, reportId, cleaned));
        var authorSummary = await points.GetOwnAsync(authorAccount);
        Assert.Equal(10, authorSummary.Balance);
        var award = Assert.Single(authorSummary.History);
        Assert.Equal(PointsReason.ActionConfirmed, award.Reason);
        Assert.Equal("poang-v1", award.RuleVersion);
        Assert.Equal(reportId, award.ReportId);
        await using (var db = contexts.CreateDbContext())
            Assert.Equal(second, (await db.PointsEvents.SingleAsync(item => item.Id == award.Id)).ConfirmerId);

        // Hearts: toggle, never for your own action, and never points.
        Assert.Equal(HeartResult.Added, await points.ToggleHeartAsync(secondAccount, reportId, cleaned));
        Assert.Equal(HeartResult.Removed, await points.ToggleHeartAsync(secondAccount, reportId, cleaned));
        Assert.Equal(HeartResult.Added, await points.ToggleHeartAsync(secondAccount, reportId, cleaned));
        Assert.Equal(HeartResult.OwnAction, await points.ToggleHeartAsync(authorAccount, reportId, cleaned));
        Assert.Equal(HeartResult.NotFound, await points.ToggleHeartAsync(secondAccount, Guid.NewGuid(), cleaned));
        Assert.Equal(10, (await points.GetOwnAsync(authorAccount)).Balance);

        // Gifts move earned points; an account without points cannot give, nor give to itself.
        Assert.Equal(GiftResult.InsufficientBalance, await points.GiftAsync(secondAccount, reportId, cleaned,
            new PointsGiftDraft { Amount = 1 }));
        Assert.Equal(GiftResult.OwnAction, await points.GiftAsync(authorAccount, reportId, cleaned,
            new PointsGiftDraft { Amount = 1 }));
        Assert.Equal(GiftResult.OwnAction, await store.GiftAsync(reportId, cleaned, author, 1));
        var secondsFix = await AddActionAsync(actions, secondAccount, reportId, "Syntetisk lagning av en bänk.");
        Assert.Equal(ActionConfirmationResult.Confirmed, await actions.ConfirmAsync(authorAccount, reportId, secondsFix));
        Assert.Equal(10, (await points.GetOwnAsync(secondAccount)).Balance);
        Assert.Equal(GiftResult.Sent, await points.GiftAsync(secondAccount, reportId, cleaned, new PointsGiftDraft { Amount = 5 }));
        Assert.Equal(GiftResult.OverDailyLimit, await points.GiftAsync(secondAccount, reportId, cleaned,
            new PointsGiftDraft { Amount = 5 }));
        Assert.Equal(GiftResult.Sent, await points.GiftAsync(secondAccount, reportId, cleaned, new PointsGiftDraft { Amount = 3 }));
        await Assert.ThrowsAsync<System.ComponentModel.DataAnnotations.ValidationException>(() =>
            points.GiftAsync(secondAccount, reportId, cleaned, new PointsGiftDraft { Amount = 6 }));
        Assert.Equal(GiftResult.OverTransferLimit, await store.GiftAsync(reportId, cleaned, second, 6));
        var secondSummary = await points.GetOwnAsync(secondAccount);
        Assert.Equal(2, secondSummary.Balance);
        Assert.Equal(8, secondSummary.GiftedToday);
        Assert.Equal(18, (await points.GetOwnAsync(authorAccount)).Balance);
        await using (var db = contexts.CreateDbContext())
        {
            var transfers = await db.PointsEvents.AsNoTracking().Where(item => item.TransferId != null)
                .GroupBy(item => item.TransferId).Select(group => group.Sum(item => item.Amount)).ToArrayAsync();
            Assert.Equal(2, transfers.Length);
            Assert.All(transfers, sum => Assert.Equal(0, sum));
            await db.PointsEvents.Where(item => item.AccountId == second && item.Reason == PointsReason.GiftSent)
                .ExecuteUpdateAsync(update => update.SetProperty(item => item.CreatedAt, DateTimeOffset.UtcNow.AddDays(-2)));
        }
        Assert.Equal(GiftResult.InsufficientBalance, await points.GiftAsync(secondAccount, reportId, cleaned,
            new PointsGiftDraft { Amount = 3 }));
        Assert.Equal(GiftResult.Sent, await points.GiftAsync(secondAccount, reportId, cleaned, new PointsGiftDraft { Amount = 2 }));
        Assert.Equal(0, (await points.GetOwnAsync(secondAccount)).Balance);

        var shown = (await points.ListForActionsAsync(secondAccount, [cleaned, secondsFix]))[cleaned];
        Assert.Equal(1, shown.Hearts);
        Assert.True(shown.ViewerHearted);
        Assert.Equal(20, shown.Points);

        // Reversal hook: withdrawing the awarding confirmation re-awards from the remaining one, only once.
        Assert.Equal(1, await store.ReverseConfirmationAsync(cleaned, second, PointsReversalCause.ConfirmationWithdrawn));
        Assert.Equal(0, await store.ReverseConfirmationAsync(cleaned, second, PointsReversalCause.ConfirmationWithdrawn));
        Assert.Equal(20, (await points.GetOwnAsync(authorAccount)).Balance);
        Assert.Equal(1, await store.ReverseConfirmationAsync(cleaned, third, PointsReversalCause.ConfirmationWithdrawn));
        Assert.Equal(10, (await points.GetOwnAsync(authorAccount)).Balance);
        var reversal = (await points.GetOwnAsync(authorAccount)).History
            .First(item => item.Reason == PointsReason.Reversal);
        Assert.Equal(PointsReversalCause.ConfirmationWithdrawn, reversal.Cause);
        Assert.Equal(-10, reversal.Amount);

        // Admin removal takes the award back and keeps the ledger history.
        Assert.True(await actions.RemoveAsync(admin, reportId, secondsFix));
        var afterRemoval = await points.GetOwnAsync(secondAccount);
        Assert.Equal(-10, afterRemoval.Balance);
        var removedReversal = afterRemoval.History.Single(item => item.Reason == PointsReason.Reversal);
        Assert.Equal(PointsReversalCause.ActionRemoved, removedReversal.Cause);
        Assert.Null(removedReversal.ReportId);
        Assert.Equal(GiftResult.InsufficientBalance, await points.GiftAsync(secondAccount, reportId, cleaned,
            new PointsGiftDraft { Amount = 1 }));

        // Archived reports refuse new hearts and gifts, but a heart can still be removed.
        var extra = await AddActionAsync(actions, thirdAccount, reportId, "Syntetisk extra åtgärd för arkivtest.");
        Assert.Equal(ActionConfirmationResult.Confirmed, await actions.ConfirmAsync(secondAccount, reportId, extra));
        Assert.True(await archives.ArchiveAsync(reportId, new ReportArchiveDraft()));
        Assert.Equal(HeartResult.Archived, await points.ToggleHeartAsync(thirdAccount, reportId, cleaned));
        Assert.Equal(GiftResult.Archived, await points.GiftAsync(thirdAccount, reportId, cleaned,
            new PointsGiftDraft { Amount = 1 }));
        Assert.Equal(HeartResult.Removed, await points.ToggleHeartAsync(secondAccount, reportId, cleaned));
        Assert.True(await archives.RestoreAsync(reportId));

        // The ledger rejects malformed and repeated rows.
        await using (var db = contexts.CreateDbContext())
        {
            var badGift = Guid.NewGuid();
            var transfer = Guid.NewGuid();
            var shape = await Assert.ThrowsAsync<PostgresException>(() => db.Database.ExecuteSqlAsync($"""
                INSERT INTO points_events ("Id", "AccountId", "Amount", "Reason", "ActionId", "TransferId", "RuleVersion", "CreatedAt")
                VALUES ({badGift}, {second}, 5, 2, {cleaned}, {transfer}, 'poang-v1', now());
                """));
            Assert.Equal(PostgresErrorCodes.CheckViolation, shape.SqlState);
            var original = await db.PointsEvents.AsNoTracking()
                .Where(item => item.AccountId == author && item.Reason == PointsReason.Reversal)
                .Select(item => item.ReversesEventId).FirstAsync();
            var again = Guid.NewGuid();
            var duplicate = await Assert.ThrowsAsync<PostgresException>(() => db.Database.ExecuteSqlAsync($"""
                INSERT INTO points_events ("Id", "AccountId", "Amount", "Reason", "ActionId", "ReversesEventId", "ReversalCause", "RuleVersion", "CreatedAt")
                VALUES ({again}, {author}, -10, 1, {cleaned}, {original}, 4, 'poang-v1', now());
                """));
            Assert.Equal(PostgresErrorCodes.UniqueViolation, duplicate.SqlState);
        }

        // Removing the report reverses what its actions still hold; deleting a confirming account keeps the award.
        await userManager.DeleteAsync((await userManager.FindByIdAsync(second.ToString()))!);
        await using (var db = contexts.CreateDbContext())
            Assert.Contains(await db.PointsEvents.AsNoTracking()
                .Where(item => item.ActionId == extra && item.Reason == PointsReason.ActionConfirmed)
                .Select(item => item.ConfirmerId).ToArrayAsync(), confirmer => confirmer is null);
        Assert.Equal(10, (await points.GetOwnAsync(thirdAccount)).Balance);
        Assert.True(await new DatabaseReportStore(contexts, mode, options).RemoveAsync(reportId));
        Assert.Equal(0, (await points.GetOwnAsync(thirdAccount)).Balance);
        Assert.Contains((await points.GetOwnAsync(thirdAccount)).History,
            item => item.Cause == PointsReversalCause.ReportRemoved);

        // Account deletion removes the account's own ledger rows and hearts.
        await userManager.DeleteAsync((await userManager.FindByIdAsync(author.ToString()))!);
        await userManager.DeleteAsync((await userManager.FindByIdAsync(third.ToString()))!);
        Assert.Equal(0, await CountAsync(contexts));
        await using (var db = contexts.CreateDbContext())
            Assert.False(await db.ActionHearts.AnyAsync());
    }

    private static async Task<Guid> CreateUserAsync(UserManager<IdentityUser<Guid>> userManager, string name)
    {
        var user = new IdentityUser<Guid> { Id = Guid.NewGuid(), UserName = name };
        Assert.True((await userManager.CreateAsync(user)).Succeeded);
        return user.Id;
    }

    private static async Task<Guid> AddActionAsync(
        ReportActionCommands actions, ClaimsPrincipal author, Guid reportId, string description)
    {
        Assert.True(await actions.AddAsync(author, reportId, new ReportActionDraft { Description = description }));
        var created = await actions.ListAsync(author, reportId);
        return created.Single(action => action.Description == description).Id;
    }

    private static async Task<int> CountAsync(IDbContextFactory<ReportDbContext> contexts)
    {
        await using var db = contexts.CreateDbContext();
        return await db.PointsEvents.CountAsync();
    }

    private static ClaimsPrincipal Account(Guid id, string role) => new(new ClaimsIdentity(
        [new Claim(ClaimTypes.NameIdentifier, id.ToString()), new Claim(ClaimTypes.Role, role)], "test"));
}

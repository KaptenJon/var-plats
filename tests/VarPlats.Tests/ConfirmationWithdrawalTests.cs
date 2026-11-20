using System.Security.Claims;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using VarPlats.Core;
using VarPlats.Web;
using VarPlats.Web.Data;
using Xunit;

namespace VarPlats.Tests;

public sealed class ConfirmationWithdrawalTests
{
    private static readonly ReportingMode Enabled = new(false, false, true, true, ConfirmationWithdrawalEnabled: true);

    [Fact]
    public void WithdrawalNeedsItsOwnSwitchAndTheWorkflow()
    {
        Assert.False(new ReportingMode(false, false, true, true).ConfirmationWithdrawal);
        Assert.False(new ReportingMode(false, false, false, false, ConfirmationWithdrawalEnabled: true).ConfirmationWithdrawal);
        Assert.False(new ReportingMode(true, false, false, true, ConfirmationWithdrawalEnabled: true).ConfirmationWithdrawal);
        Assert.False(new ReportingMode(false, true, false, true, ConfirmationWithdrawalEnabled: true).ConfirmationWithdrawal);
        var readOnly = new ReportingMode(false, false, false, true, ConfirmationWithdrawalEnabled: true);
        Assert.True(readOnly.ConfirmationWithdrawal);
        Assert.False(readOnly.CanWithdrawConfirmation);
        Assert.True(Enabled.CanWithdrawConfirmation);
    }

    [Theory]
    [InlineData(false, false, true, true, false)]
    [InlineData(false, false, false, true, true)]
    [InlineData(false, false, true, false, true)]
    public async Task WithdrawalIsRefusedWhenTheSwitchOrPublicWorkflowIsOff(
        bool demo, bool local, bool publicWrites, bool workflowEnabled, bool withdrawalEnabled)
    {
        var store = new ReportWorkflowTestStore();
        var (reportId, actionId, confirmer) = await ConfirmedActionAsync(store);
        var commands = new ReportActionCommands(
            new(demo, local, publicWrites, workflowEnabled, ConfirmationWithdrawalEnabled: withdrawalEnabled), store,
            accountAccess: SyntheticReportAccountAccess.Instance);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            commands.WithdrawConfirmationAsync(confirmer, reportId, actionId));
        Assert.Equal(1, Assert.Single(await store.ListAsync(reportId, null)).ConfirmationCount);
    }

    [Fact]
    public async Task WithdrawalRejectsAnonymousRecoveryMissingIdAndWrongRole()
    {
        var store = new ReportWorkflowTestStore();
        var (reportId, actionId, _) = await ConfirmedActionAsync(store);
        var commands = new ReportActionCommands(Enabled, store, accountAccess: SyntheticReportAccountAccess.Instance);
        var invalidUsers = new[]
        {
            new ClaimsPrincipal(new ClaimsIdentity()),
            Account("Member", authenticated: false),
            Account("Member", userId: Guid.Empty),
            Account("Other"),
            Account("Admin", recovery: true)
        };

        foreach (var user in invalidUsers)
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
                commands.WithdrawConfirmationAsync(user, reportId, actionId));
        var unverified = new ReportActionCommands(Enabled, store);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            unverified.WithdrawConfirmationAsync(Account("Admin"), reportId, actionId));
        Assert.Equal(1, Assert.Single(await store.ListAsync(reportId, null)).ConfirmationCount);
    }

    [Fact]
    public async Task OnlyTheConfirmingAccountCanWithdrawAndStatusIsRecomputed()
    {
        var store = new ReportWorkflowTestStore();
        var (reportId, actionId, confirmer) = await ConfirmedActionAsync(store);
        var commands = new ReportActionCommands(Enabled, store, accountAccess: SyntheticReportAccountAccess.Instance);
        var other = Account("Member");
        var admin = Account("Admin");

        // Neither another member nor an admin can withdraw someone else's confirmation.
        Assert.Equal(ConfirmationWithdrawalResult.NotConfirmed,
            await commands.WithdrawConfirmationAsync(other, reportId, actionId));
        Assert.Equal(ConfirmationWithdrawalResult.NotConfirmed,
            await commands.WithdrawConfirmationAsync(admin, reportId, actionId));
        Assert.Equal(ConfirmationWithdrawalResult.NotFound,
            await commands.WithdrawConfirmationAsync(confirmer, reportId, Guid.NewGuid()));
        Assert.Equal(ReportStatus.Confirmed, (await store.GetAsync(reportId))!.Status);

        Assert.Equal(ConfirmationWithdrawalResult.Withdrawn,
            await commands.WithdrawConfirmationAsync(confirmer, reportId, actionId));
        Assert.Equal(ConfirmationWithdrawalResult.NotConfirmed,
            await commands.WithdrawConfirmationAsync(confirmer, reportId, actionId));
        Assert.Equal(ReportStatus.ActionDocumented, (await store.GetAsync(reportId))!.Status);
        var action = Assert.Single(await commands.ListAsync(confirmer, reportId));
        Assert.Equal(0, action.ConfirmationCount);
        Assert.Equal(1, action.WithdrawnCount);
        Assert.False(action.HasConfirmed);
        Assert.True(action.HasWithdrawn);
        Assert.False(Assert.Single(await commands.ListAsync(other, reportId)).HasWithdrawn);

        // Re-confirming after a withdrawal is refused so the status cannot be flipped back and forth.
        Assert.Equal(ActionConfirmationResult.Withdrawn, await commands.ConfirmAsync(confirmer, reportId, actionId));
        Assert.Equal(ReportStatus.ActionDocumented, (await store.GetAsync(reportId))!.Status);
        Assert.Equal(ActionConfirmationResult.Confirmed, await commands.ConfirmAsync(other, reportId, actionId));
        Assert.Equal(ReportStatus.Confirmed, (await store.GetAsync(reportId))!.Status);
    }

    [Fact]
    public async Task WithdrawalCountsAgainstTheConfirmationRateLimit()
    {
        var store = new ReportWorkflowTestStore();
        var (reportId, actionId, confirmer) = await ConfirmedActionAsync(store);
        var limiter = WriteRateLimitTests.Limiter(new WriteRateLimitTests.ManualTime(),
            options => options.Confirmations = new() { PermitLimit = 1, WindowSeconds = 600 });
        var commands = new ReportActionCommands(Enabled, store, accountAccess: SyntheticReportAccountAccess.Instance,
            rateLimiter: limiter);

        Assert.Equal(ConfirmationWithdrawalResult.Withdrawn,
            await commands.WithdrawConfirmationAsync(confirmer, reportId, actionId));
        var exception = await Assert.ThrowsAsync<WriteRateLimitException>(() =>
            commands.WithdrawConfirmationAsync(confirmer, reportId, actionId));
        Assert.Equal(WriteKind.Confirmation, exception.Kind);
    }

    [Fact]
    public async Task WithdrawalDatabaseStoreRejectsDisabledModesBeforeConnecting()
    {
        var options = new DbContextOptionsBuilder<ReportDbContext>()
            .UseNpgsql("Host=localhost;Database=unused", postgres => postgres.UseNetTopologySuite()).Options;
        foreach (var mode in new ReportingMode[]
                 {
                     new(false, false, true, true),
                     new(false, false, false, true, ConfirmationWithdrawalEnabled: true)
                 })
        {
            var store = new DatabaseReportActionStore(new TestContextFactory(options), mode);
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                store.WithdrawConfirmationAsync(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid()));
        }
        var enabled = new DatabaseReportActionStore(new TestContextFactory(options), Enabled);
        await Assert.ThrowsAsync<ArgumentException>(() =>
            enabled.WithdrawConfirmationAsync(Guid.NewGuid(), Guid.NewGuid(), Guid.Empty));
    }

    [Fact]
    public void WithdrawalHistoryIsAnonymizedWithTheAccountAndRemovedWithTheAction()
    {
        using var db = new ReportDbContext(new DbContextOptionsBuilder<ReportDbContext>()
            .UseNpgsql("Host=localhost;Database=unused", postgres => postgres.UseNetTopologySuite()).Options);
        var history = db.Model.FindEntityType(typeof(ReportActionConfirmationWithdrawalEntity))!;
        Assert.Equal("report_action_confirmation_withdrawals", history.GetTableName());
        var account = Assert.Single(history.GetForeignKeys(),
            key => key.PrincipalEntityType.ClrType == typeof(IdentityUser<Guid>));
        Assert.True(account.Properties.Single().IsNullable);
        Assert.Equal(DeleteBehavior.SetNull, account.DeleteBehavior);
        Assert.Contains(history.GetForeignKeys(), key => key.PrincipalEntityType.ClrType == typeof(ReportActionEntity)
            && key.DeleteBehavior == DeleteBehavior.Cascade);
        Assert.Contains(history.GetIndexes(), index => !index.IsUnique
            && index.Properties.Select(property => property.Name).SequenceEqual(["ActionId", "AccountId"]));
    }

    private static async Task<(Guid ReportId, Guid ActionId, ClaimsPrincipal Confirmer)> ConfirmedActionAsync(
        ReportWorkflowTestStore store)
    {
        var reportId = (await store.ListAsync()).First().Id;
        var authorId = Guid.NewGuid();
        var confirmerId = Guid.NewGuid();
        Assert.True(await store.AddAsync(reportId, authorId,
            new ReportActionDraft { Description = "Syntetisk åtgärd utan personuppgifter." }));
        var action = Assert.Single(await store.ListAsync(reportId, null));
        Assert.Equal(ActionConfirmationResult.Confirmed, await store.ConfirmAsync(reportId, action.Id, confirmerId));
        return (reportId, action.Id, Account("Member", confirmerId));
    }

    private static ClaimsPrincipal Account(
        string role, Guid? userId = null, bool authenticated = true, bool recovery = false)
    {
        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, (userId ?? Guid.NewGuid()).ToString()),
            new(ClaimTypes.Role, role)
        };
        if (recovery) claims.Add(new Claim("recovery_only", "true"));
        return new ClaimsPrincipal(new ClaimsIdentity(claims, authenticated ? "test" : null));
    }

    private sealed class TestContextFactory(DbContextOptions<ReportDbContext> options) : IDbContextFactory<ReportDbContext>
    {
        public ReportDbContext CreateDbContext() => new(options);
    }
}

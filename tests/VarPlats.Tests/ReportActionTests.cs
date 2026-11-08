using System.ComponentModel.DataAnnotations;
using System.Security.Claims;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using VarPlats.Core;
using VarPlats.Web;
using VarPlats.Web.Data;
using Xunit;

namespace VarPlats.Tests;

public sealed class ReportActionTests
{
    [Theory]
    [InlineData("")]
    [InlineData("         ")]
    [InlineData("  nio teck  ")]
    public void ActionDescriptionsRequireEnoughNonWhitespaceText(string description)
        => Assert.Throws<ValidationException>(() =>
            new ReportActionDraft { Description = description }.ValidatedDescription());

    [Fact]
    public void ActionDescriptionIsBoundedAndTrimmed()
    {
        Assert.Equal("Syntetisk åtgärd", new ReportActionDraft
        {
            Description = "  Syntetisk åtgärd  "
        }.ValidatedDescription());
        Assert.Equal(1000, new ReportActionDraft { Description = new string('a', 1000) }
            .ValidatedDescription().Length);
        Assert.Throws<ValidationException>(() =>
            new ReportActionDraft { Description = new string('a', 1001) }.ValidatedDescription());
    }

    [Theory]
    [InlineData(false, false, false, false)]
    [InlineData(false, false, true, false)]
    [InlineData(false, false, false, true)]
    [InlineData(true, false, false, false)]
    [InlineData(false, true, false, false)]
    [InlineData(false, true, true, true)]
    public async Task ActionWritesRequireTheExplicitPublicWorkflow(
        bool demo, bool local, bool publicWrites, bool workflowEnabled)
    {
        var store = new ReportWorkflowTestStore();
        var commands = new ReportActionCommands(new(demo, local, publicWrites, workflowEnabled), store);
        var admin = Account("Admin");
        var reportId = (await store.ListAsync()).First().Id;

        await Assert.ThrowsAsync<InvalidOperationException>(() => commands.AddAsync(admin, reportId, ValidDraft()));
        await Assert.ThrowsAsync<InvalidOperationException>(() => commands.ConfirmAsync(admin, reportId, Guid.NewGuid()));
        await Assert.ThrowsAsync<InvalidOperationException>(() => commands.RemoveAsync(admin, reportId, Guid.NewGuid()));
        Assert.Empty(await store.ListAsync(reportId, null));
    }

    [Fact]
    public async Task DisabledWorkflowReadsDoNotNeedAnActionStore()
    {
        var commands = new ReportActionCommands(new(false, false, false));
        Assert.Empty(await commands.ListAsync(new ClaimsPrincipal(), Guid.NewGuid()));
    }

    [Fact]
    public async Task ActionWritesRejectAnonymousRecoveryMissingIdAndWrongRole()
    {
        var store = new ReportWorkflowTestStore();
        var commands = new ReportActionCommands(new(false, false, true, true), store,
            accountAccess: SyntheticReportAccountAccess.Instance);
        var reportId = (await store.ListAsync()).First().Id;
        var invalidUsers = new[]
        {
            new ClaimsPrincipal(new ClaimsIdentity()),
            Account("Member", authenticated: false),
            Account("Member", userId: Guid.Empty),
            Account("Other"),
            Account("Admin", recovery: true)
        };

        foreach (var user in invalidUsers)
        {
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => commands.AddAsync(user, reportId, ValidDraft()));
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => commands.ConfirmAsync(user, reportId, Guid.NewGuid()));
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => commands.RemoveAsync(user, reportId, Guid.NewGuid()));
        }
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            commands.RemoveAsync(Account("Member"), reportId, Guid.NewGuid()));
        Assert.Empty(await store.ListAsync(reportId, null));
    }

    [Fact]
    public async Task PublicActionsAreImmediateAndOnlyOtherAccountsCanConfirm()
    {
        var store = new ReportWorkflowTestStore();
        var mode = new ReportingMode(false, false, true, true);
        var reports = new ReportCommands(store, mode, store, accountAccess: SyntheticReportAccountAccess.Instance);
        var actions = new ReportActionCommands(mode, store, accountAccess: SyntheticReportAccountAccess.Instance);
        var reporter = Account("Member");
        var actor = Account("Member");
        var admin = Account("Admin");
        var submitted = await reports.AddAsync(reporter, ReportTests.ValidDraft());

        Assert.False(await actions.AddAsync(actor, submitted.Id, ValidDraft()));
        Assert.True(await reports.DecideAsync(admin, submitted.Id, ReportReviewStatus.Approved));
        Assert.True(await actions.AddAsync(actor, submitted.Id, ValidDraft()));
        var unconfirmed = Assert.Single(await actions.ListAsync(reporter, submitted.Id));
        Assert.Equal("Syntetisk åtgärd utan personuppgifter.", unconfirmed.Description);
        Assert.Equal(0, unconfirmed.ConfirmationCount);
        Assert.Equal(ReportStatus.ActionDocumented, (await store.GetAsync(submitted.Id))!.Status);
        Assert.Equal(ActionConfirmationResult.OwnAction, await actions.ConfirmAsync(actor, submitted.Id, unconfirmed.Id));
        Assert.Equal(ActionConfirmationResult.Confirmed, await actions.ConfirmAsync(reporter, submitted.Id, unconfirmed.Id));
        Assert.Equal(ActionConfirmationResult.AlreadyConfirmed, await actions.ConfirmAsync(reporter, submitted.Id, unconfirmed.Id));
        Assert.Equal(ActionConfirmationResult.Confirmed, await actions.ConfirmAsync(admin, submitted.Id, unconfirmed.Id));
        Assert.True(Assert.Single(await actions.ListAsync(reporter, submitted.Id)).HasConfirmed);
        Assert.Equal(2, Assert.Single(await actions.ListAsync(reporter, submitted.Id)).ConfirmationCount);
        Assert.Equal(ReportStatus.Confirmed, (await store.GetAsync(submitted.Id))!.Status);

        Assert.True(await actions.RemoveAsync(admin, submitted.Id, unconfirmed.Id));
        Assert.Empty(await actions.ListAsync(reporter, submitted.Id));
        Assert.Equal(ReportStatus.Reported, (await store.GetAsync(submitted.Id))!.Status);

        Assert.True(await actions.AddAsync(admin, submitted.Id, ValidDraft()));
        var adminAction = Assert.Single(await actions.ListAsync(actor, submitted.Id));
        Assert.Equal(ActionConfirmationResult.Confirmed, await actions.ConfirmAsync(actor, submitted.Id, adminAction.Id));
    }

    [Fact]
    public async Task RejectedSubmissionsCannotReceiveActionsAndValidationRunsServerSide()
    {
        var store = new ReportWorkflowTestStore();
        var mode = new ReportingMode(false, false, true, true);
        var reports = new ReportCommands(store, mode, store, accountAccess: SyntheticReportAccountAccess.Instance);
        var actions = new ReportActionCommands(mode, store, accountAccess: SyntheticReportAccountAccess.Instance);
        var member = Account("Member");
        var rejected = await reports.AddAsync(member, ReportTests.ValidDraft());
        Assert.True(await reports.DecideAsync(Account("Admin"), rejected.Id, ReportReviewStatus.Rejected));

        Assert.False(await actions.AddAsync(member, rejected.Id, ValidDraft()));
        var reportId = (await store.ListAsync()).First().Id;
        await Assert.ThrowsAsync<ValidationException>(() =>
            actions.AddAsync(member, reportId, new ReportActionDraft { Description = "   " }));
        Assert.Empty(await actions.ListAsync(member, rejected.Id));
    }

    [Fact]
    public async Task ActionDatabaseStoreRejectsDisabledModesBeforeConnecting()
    {
        var options = new DbContextOptionsBuilder<ReportDbContext>()
            .UseNpgsql("Host=localhost;Database=unused", postgres => postgres.UseNetTopologySuite()).Options;
        var store = new DatabaseReportActionStore(new TestContextFactory(options), new(false, false, false));

        await Assert.ThrowsAsync<InvalidOperationException>(() => store.ListAsync(Guid.NewGuid(), null));
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.AddAsync(Guid.NewGuid(), Guid.NewGuid(), ValidDraft()));
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.ConfirmAsync(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid()));
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.RemoveAsync(Guid.NewGuid(), Guid.NewGuid()));
    }

    [Fact]
    public void ActionModelAnonymizesAuthorsAndCascadesConfirmationsWithACompositeKey()
    {
        using var db = new ReportDbContext(new DbContextOptionsBuilder<ReportDbContext>()
            .UseNpgsql("Host=localhost;Database=unused", postgres => postgres.UseNetTopologySuite()).Options);
        var action = db.Model.FindEntityType(typeof(ReportActionEntity))!;
        var author = Assert.Single(action.GetForeignKeys(),
            key => key.PrincipalEntityType.ClrType == typeof(IdentityUser<Guid>));
        Assert.True(author.Properties.Single().IsNullable);
        Assert.Equal(DeleteBehavior.SetNull, author.DeleteBehavior);
        Assert.Contains(action.GetForeignKeys(), key => key.PrincipalEntityType.ClrType == typeof(ReportEntity)
            && key.DeleteBehavior == DeleteBehavior.Cascade);
        Assert.Equal(1000, action.FindProperty(nameof(ReportActionEntity.Description))!.GetMaxLength());

        var confirmation = db.Model.FindEntityType(typeof(ReportActionConfirmationEntity))!;
        Assert.Equal(new[] { "ActionId", "AccountId" }, confirmation.FindPrimaryKey()!.Properties.Select(property => property.Name));
        Assert.Contains(confirmation.GetForeignKeys(), key => key.PrincipalEntityType.ClrType == typeof(IdentityUser<Guid>)
            && key.DeleteBehavior == DeleteBehavior.Cascade);
        Assert.Contains(confirmation.GetForeignKeys(), key => key.PrincipalEntityType.ClrType == typeof(ReportActionEntity)
            && key.DeleteBehavior == DeleteBehavior.Cascade);
    }

    private static ReportActionDraft ValidDraft() => new()
    {
        Description = "  Syntetisk åtgärd utan personuppgifter.  "
    };

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

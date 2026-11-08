using System.ComponentModel.DataAnnotations;
using System.Security.Claims;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using VarPlats.Core;
using VarPlats.Web;
using VarPlats.Web.Data;
using Xunit;

namespace VarPlats.Tests;

public sealed class ReportTests
{
    [Theory]
    [InlineData(-86, 18)]
    [InlineData(86, 18)]
    [InlineData(59, -181)]
    [InlineData(59, 181)]
    [InlineData(double.NaN, 18)]
    [InlineData(59, double.PositiveInfinity)]
    public void InvalidCoordinatesAreRejected(double latitude, double longitude)
    {
        var draft = ValidDraft();
        draft.Latitude = latitude;
        draft.Longitude = longitude;
        Assert.Throws<ValidationException>(() => draft.ToReport());
    }

    [Fact]
    public void ReportListFilterCombinesCategoryWithCaseInsensitiveTitleAndDescriptionSearch()
    {
        var reports = new[]
        {
            new Report(Guid.NewGuid(), "Skräp vid gångvägen", "Förpackningar vid en gångväg.",
                ReportCategory.Litter, 59, 18, DateTimeOffset.UtcNow),
            new Report(Guid.NewGuid(), "En bänk behöver ses över", "Ansvarig fastighetsägare bör bedöma skadan.",
                ReportCategory.Broken, 59, 18, DateTimeOffset.UtcNow)
        };

        var matchingTitle = Assert.Single(ReportListFilter.Apply(reports, "Broken", "  BÄNK "));
        var matchingDescription = Assert.Single(ReportListFilter.Apply(reports, "", "FASTIGHETSÄGARE"));

        Assert.Equal(ReportCategory.Broken, matchingTitle.Category);
        Assert.Equal(ReportCategory.Broken, matchingDescription.Category);
        Assert.Empty(ReportListFilter.Apply(reports, "Litter", "bänk"));
    }

    [Theory]
    [InlineData("    ", "En verklig beskrivning")]
    [InlineData("Rubriken", "        ")]
    [InlineData("Kort", "Beskrivning till test")]
    public void InvalidTextIsRejected(string title, string description)
        => Assert.Throws<ValidationException>(() => new ReportDraft { Title = title, Description = description }.ToReport());

    [Fact]
    public void UnknownCategoryIsRejected()
    {
        var draft = ValidDraft();
        draft.Category = (ReportCategory)99;
        Assert.Throws<ValidationException>(() => draft.ToReport());
    }

    [Fact]
    public async Task DemoCreatesReportWithoutSharingBetweenScopes()
    {
        var first = new DemoReportStore();
        var second = new DemoReportStore();
        var report = await first.AddAsync(ValidDraft());
        Assert.Equal("Skräp på gångväg", report.Title);
        Assert.Equal(ReportStatus.Reported, report.Status);
        Assert.Equal(3, (await first.ListAsync()).Count);
        Assert.Equal(2, (await second.ListAsync()).Count);
    }

    [Fact]
    public async Task DatabaseWritesFailClosedBeforeOpeningConnection()
    {
        var options = new DbContextOptionsBuilder<ReportDbContext>()
            .UseNpgsql("Host=localhost;Database=unused", postgres => postgres.UseNetTopologySuite()).Options;
        var factory = new TestContextFactory(options);
        var store = new DatabaseReportStore(factory, new ReportingMode(false, false, false));
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.AddAsync(ValidDraft()));
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.RemoveAsync(Guid.NewGuid()));
        var publicStore = new DatabaseReportStore(factory, new ReportingMode(false, false, true, true));
        await Assert.ThrowsAsync<InvalidOperationException>(() => publicStore.AddAsync(ValidDraft()));
        var mixedStore = new DatabaseReportStore(factory, new ReportingMode(false, true, true, true));
        await Assert.ThrowsAsync<InvalidOperationException>(() => mixedStore.AddAsync(ValidDraft()));
    }

    [Fact]
    public async Task PublicWritesRequireOptInAndAnAuthenticatedAccountWithMemberOrAdminRole()
    {
        var store = new ReportWorkflowTestStore();
        var disabled = new ReportCommands(store, new ReportingMode(false, false, true), store);
        var commands = new ReportCommands(store, new ReportingMode(false, false, true, true), store,
            accountAccess: SyntheticReportAccountAccess.Instance);
        var anonymous = new ClaimsPrincipal(new ClaimsIdentity());
        var member = UserWithRole("Member");

        await Assert.ThrowsAsync<InvalidOperationException>(() => disabled.AddAsync(member, ValidDraft()));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => commands.AddAsync(anonymous, ValidDraft()));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            commands.AddAsync(UserWithRole("Member", authenticated: false), ValidDraft()));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            commands.AddAsync(UserWithRole("Member", userId: Guid.Empty), ValidDraft()));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            commands.AddAsync(new ClaimsPrincipal(new ClaimsIdentity(
                [new Claim(ClaimTypes.Role, "Member")], "test")), ValidDraft()));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            commands.AddAsync(UserWithRole("Other"), ValidDraft()));

        var report = await commands.AddAsync(member, ValidDraft());
        Assert.Equal("Skräp på gångväg", report.Title);
        Assert.DoesNotContain(await store.ListAsync(), item => item.Id == report.Id);
        Assert.Equal(ReportReviewStatus.Pending, Assert.Single(await commands.ListOwnAsync(member)).ReviewStatus);
        Assert.True(ReportCommands.CanReport(UserWithRole("Admin")));
    }

    [Fact]
    public async Task PublicWorkflowFailsClosedWithoutCurrentAccountVerification()
    {
        var store = new ReportWorkflowTestStore();
        var mode = new ReportingMode(false, false, true, true);
        var commands = new ReportCommands(store, mode, store);
        var actions = new ReportActionCommands(mode, store);
        var admin = UserWithRole("Admin");
        var reportId = (await store.ListAsync()).First().Id;

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => commands.AddAsync(admin, ValidDraft()));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => commands.ListOwnAsync(admin));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => commands.ListPendingAsync(admin));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            commands.DecideAsync(admin, reportId, ReportReviewStatus.Approved));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => commands.RemoveAsync(admin, reportId));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => actions.AddAsync(
            admin, reportId, new ReportActionDraft { Description = "Syntetisk åtgärd utan personuppgifter." }));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => actions.ConfirmAsync(admin, reportId, Guid.NewGuid()));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => actions.RemoveAsync(admin, reportId, Guid.NewGuid()));
    }

    [Fact]
    public async Task OwnStatusAndQueueRespectReviewAndRoleBoundaries()
    {
        var store = new ReportWorkflowTestStore();
        var commands = new ReportCommands(store, new ReportingMode(false, false, true, true), store,
            accountAccess: SyntheticReportAccountAccess.Instance);
        var firstMember = UserWithRole("Member");
        var secondMember = UserWithRole("Member");
        var admin = UserWithRole("Admin");
        var first = await commands.AddAsync(firstMember, ValidDraft());
        var second = await commands.AddAsync(secondMember, ValidDraft());

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => commands.ListPendingAsync(firstMember));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            commands.DecideAsync(firstMember, first.Id, ReportReviewStatus.Approved));
        Assert.Equal(2, (await commands.ListPendingAsync(admin)).Count);
        Assert.Equal(first.Id, Assert.Single(await commands.ListOwnAsync(firstMember)).Report.Id);
        Assert.Equal(second.Id, Assert.Single(await commands.ListOwnAsync(secondMember)).Report.Id);

        Assert.True(await commands.DecideAsync(admin, first.Id, ReportReviewStatus.Approved));
        Assert.True(await commands.DecideAsync(admin, second.Id, ReportReviewStatus.Rejected));
        Assert.False(await commands.DecideAsync(admin, first.Id, ReportReviewStatus.Rejected));
        Assert.False(await commands.DecideAsync(admin, second.Id, ReportReviewStatus.Approved));
        Assert.Empty(await commands.ListPendingAsync(admin));
        Assert.Equal(ReportReviewStatus.Approved, Assert.Single(await commands.ListOwnAsync(firstMember)).ReviewStatus);
        Assert.Equal(ReportReviewStatus.Rejected, Assert.Single(await commands.ListOwnAsync(secondMember)).ReviewStatus);
        Assert.Contains(await store.ListAsync(), item => item.Id == first.Id);
        Assert.DoesNotContain(await store.ListAsync(), item => item.Id == second.Id);
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            commands.DecideAsync(admin, Guid.NewGuid(), ReportReviewStatus.Pending));
    }

    [Fact]
    public async Task WorkflowReadAccessCanRemainAvailableWhenPublicWritingIsDisabled()
    {
        var store = new ReportWorkflowTestStore();
        var enabled = new ReportCommands(store, new ReportingMode(false, false, true, true), store,
            accountAccess: SyntheticReportAccountAccess.Instance);
        var readOnly = new ReportCommands(store, new ReportingMode(false, false, false, true), store,
            accountAccess: SyntheticReportAccountAccess.Instance);
        var member = UserWithRole("Member");
        var admin = UserWithRole("Admin");
        var report = await enabled.AddAsync(member, ValidDraft());

        Assert.Single(await readOnly.ListOwnAsync(member));
        Assert.Single(await readOnly.ListPendingAsync(admin));
        await Assert.ThrowsAsync<InvalidOperationException>(() => readOnly.AddAsync(member, ValidDraft()));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            readOnly.DecideAsync(admin, report.Id, ReportReviewStatus.Approved));
        Assert.DoesNotContain(await store.ListAsync(), item => item.Id == report.Id);
    }

    [Fact]
    public async Task OnlyAdminCanRemoveReports()
    {
        var store = new DemoReportStore();
        var commands = new ReportCommands(store, new ReportingMode(false, false, true, true),
            accountAccess: SyntheticReportAccountAccess.Instance);
        var reportId = (await store.ListAsync()).First().Id;

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            commands.RemoveAsync(UserWithRole("Member"), reportId));
        Assert.True(await commands.RemoveAsync(UserWithRole("Admin"), reportId));
        Assert.DoesNotContain(await store.ListAsync(), report => report.Id == reportId);
    }

    [Fact]
    public async Task RecoverySessionCannotReportOrModerate()
    {
        var store = new ReportWorkflowTestStore();
        var commands = new ReportCommands(store, new ReportingMode(false, false, true, true), store,
            accountAccess: SyntheticReportAccountAccess.Instance);
        var recoveryAdmin = new ClaimsPrincipal(new ClaimsIdentity(
        [
            new Claim(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString()),
            new Claim(ClaimTypes.Role, "Admin"),
            new Claim("recovery_only", "true")
        ], "test"));

        Assert.False(ReportCommands.CanReport(recoveryAdmin));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            commands.AddAsync(recoveryAdmin, ValidDraft()));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            commands.RemoveAsync(recoveryAdmin, Guid.NewGuid()));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => commands.ListOwnAsync(recoveryAdmin));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => commands.ListPendingAsync(recoveryAdmin));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            commands.DecideAsync(recoveryAdmin, Guid.NewGuid(), ReportReviewStatus.Approved));
    }

    [Fact]
    public void EfModelUsesGeographyAndSpatialIndex()
    {
        using var db = new ReportDbContext(new DbContextOptionsBuilder<ReportDbContext>()
            .UseNpgsql("Host=localhost;Database=unused", postgres => postgres.UseNetTopologySuite()).Options);
        var entity = db.Model.FindEntityType(typeof(ReportEntity))!;
        Assert.Equal("geography (point, 4326)", entity.FindProperty(nameof(ReportEntity.Location))!.GetColumnType());
        Assert.Contains(entity.GetIndexes(), index => index.GetMethod() == "gist");
        var passkeyEntity = db.Model.FindEntityType(typeof(IdentityUserPasskey<Guid>))!;
        Assert.Equal("AspNetUserPasskeys", passkeyEntity.GetTableName());
        Assert.Contains(passkeyEntity.GetComplexProperties(), property => property.Name == "Data");
        var activityEntity = db.Model.FindEntityType(typeof(IdentityAccountActivity))!;
        Assert.Equal("IdentityAccountActivity", activityEntity.GetTableName());
        Assert.Contains(activityEntity.GetForeignKeys(),
            foreignKey => foreignKey.DeleteBehavior == DeleteBehavior.Cascade);
        var submissionEntity = db.Model.FindEntityType(typeof(ReportSubmissionEntity))!;
        var authorForeignKey = Assert.Single(submissionEntity.GetForeignKeys(),
            foreignKey => foreignKey.PrincipalEntityType.ClrType == typeof(IdentityUser<Guid>));
        Assert.True(authorForeignKey.Properties.Single().IsNullable);
        Assert.Equal(DeleteBehavior.SetNull, authorForeignKey.DeleteBehavior);
        Assert.Contains(submissionEntity.GetForeignKeys(),
            foreignKey => foreignKey.PrincipalEntityType.ClrType == typeof(ReportEntity)
                && foreignKey.DeleteBehavior == DeleteBehavior.Cascade);
    }

    [Fact]
    public void IdentityRetentionUsesLastSignInAndIncludesTheTwelveMonthBoundary()
    {
        var now = new DateTimeOffset(2026, 11, 8, 12, 0, 0, TimeSpan.Zero);
        var cutoff = IdentityAccountRetentionPolicy.GetInactiveCutoff(now);
        var lastSignIn = new IdentityAccountActivity
        {
            CreatedAtUtc = cutoff.AddYears(-1),
            LastSignInAtUtc = cutoff
        };
        var recentlySignedIn = new IdentityAccountActivity
        {
            CreatedAtUtc = cutoff.AddYears(-1),
            LastSignInAtUtc = cutoff.AddTicks(1)
        };
        var neverSignedIn = new IdentityAccountActivity { CreatedAtUtc = cutoff };

        Assert.True(IdentityAccountRetentionPolicy.IsInactive(lastSignIn, cutoff));
        Assert.False(IdentityAccountRetentionPolicy.IsInactive(recentlySignedIn, cutoff));
        Assert.True(IdentityAccountRetentionPolicy.IsInactive(neverSignedIn, cutoff));
    }

    public static ReportDraft ValidDraft() => new()
    {
        Title = " Skräp på gångväg ",
        Description = "Syntetiska testuppgifter utan personuppgifter.",
        Latitude = 59.3293,
        Longitude = 18.0686
    };

    private static ClaimsPrincipal UserWithRole(string role, Guid? userId = null, bool authenticated = true)
        => new(new ClaimsIdentity(
        [
            new Claim(ClaimTypes.NameIdentifier, (userId ?? Guid.NewGuid()).ToString()),
            new Claim(ClaimTypes.Role, role)
        ], authenticated ? "test" : null));

    private sealed class TestContextFactory(DbContextOptions<ReportDbContext> options) : IDbContextFactory<ReportDbContext>
    {
        public ReportDbContext CreateDbContext() => new(options);
    }
}

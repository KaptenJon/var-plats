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
    }

    [Fact]
    public async Task PublicWritesRequireMemberOrAdmin()
    {
        var commands = new ReportCommands(new DemoReportStore(), new ReportingMode(false, false, true));
        var anonymous = new ClaimsPrincipal(new ClaimsIdentity());
        var member = UserWithRole("Member");

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => commands.AddAsync(anonymous, ValidDraft()));
        Assert.Equal("Skräp på gångväg",
            (await commands.AddAsync(member, ValidDraft())).Title);
        Assert.True(ReportCommands.CanReport(UserWithRole("Admin")));
    }

    [Fact]
    public async Task OnlyAdminCanRemoveReports()
    {
        var store = new DemoReportStore();
        var commands = new ReportCommands(store, new ReportingMode(false, false, true));
        var reportId = (await store.ListAsync()).First().Id;

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            commands.RemoveAsync(UserWithRole("Member"), reportId));
        Assert.True(await commands.RemoveAsync(UserWithRole("Admin"), reportId));
        Assert.DoesNotContain(await store.ListAsync(), report => report.Id == reportId);
    }

    [Fact]
    public async Task RecoverySessionCannotReportOrModerate()
    {
        var commands = new ReportCommands(new DemoReportStore(), new ReportingMode(false, false, true));
        var recoveryAdmin = new ClaimsPrincipal(new ClaimsIdentity(
        [
            new Claim(ClaimTypes.Role, "Admin"),
            new Claim("recovery_only", "true")
        ], "test"));

        Assert.False(ReportCommands.CanReport(recoveryAdmin));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            commands.AddAsync(recoveryAdmin, ValidDraft()));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            commands.RemoveAsync(recoveryAdmin, Guid.NewGuid()));
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

    private static ClaimsPrincipal UserWithRole(string role)
        => new(new ClaimsIdentity([new Claim(ClaimTypes.Role, role)], "test"));

    private sealed class TestContextFactory(DbContextOptions<ReportDbContext> options) : IDbContextFactory<ReportDbContext>
    {
        public ReportDbContext CreateDbContext() => new(options);
    }
}

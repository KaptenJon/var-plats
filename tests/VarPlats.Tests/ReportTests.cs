using System.ComponentModel.DataAnnotations;
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
        var store = new DatabaseReportStore(factory, new ReportingMode(false, false));
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.AddAsync(ValidDraft()));
    }

    [Fact]
    public void EfModelUsesGeographyAndSpatialIndex()
    {
        using var db = new ReportDbContext(new DbContextOptionsBuilder<ReportDbContext>()
            .UseNpgsql("Host=localhost;Database=unused", postgres => postgres.UseNetTopologySuite()).Options);
        var entity = db.Model.FindEntityType(typeof(ReportEntity))!;
        Assert.Equal("geography (point, 4326)", entity.FindProperty(nameof(ReportEntity.Location))!.GetColumnType());
        Assert.Contains(entity.GetIndexes(), index => index.GetMethod() == "gist");
    }

    public static ReportDraft ValidDraft() => new()
    {
        Title = " Skräp på gångväg ",
        Description = "Syntetiska testuppgifter utan personuppgifter.",
        Latitude = 59.3293,
        Longitude = 18.0686
    };

    private sealed class TestContextFactory(DbContextOptions<ReportDbContext> options) : IDbContextFactory<ReportDbContext>
    {
        public ReportDbContext CreateDbContext() => new(options);
    }
}

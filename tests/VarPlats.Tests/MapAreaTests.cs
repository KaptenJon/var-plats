using VarPlats.Core;
using Xunit;

namespace VarPlats.Tests;

public sealed class MapAreaTests
{
    [Theory]
    [InlineData(double.NaN, 18, 60, 19)]
    [InlineData(59, double.NegativeInfinity, 60, 19)]
    [InlineData(60, 18, 59, 19)]
    [InlineData(59, 19, 60, 18)]
    public void InvalidAreasAreRejected(double south, double west, double north, double east)
    {
        Assert.False(MapArea.TryCreate(south, west, north, east, out _, out var error));
        Assert.NotEmpty(error);
    }

    [Fact]
    public void LatitudeIsClampedToTheMapProjection()
    {
        Assert.True(MapArea.TryCreate(-90, 0, 90, 10, out var area, out _));
        Assert.Equal(-85, area.South);
        Assert.Equal(85, area.North);
    }

    [Fact]
    public void WrappedLongitudesAreNormalised()
    {
        // Leaflet reports longitudes outside ±180 after panning around the world.
        Assert.True(MapArea.TryCreate(59, 378, 60, 379, out var area, out _));
        Assert.Equal(18, area.West, 9);
        Assert.Equal(19, area.East, 9);
        Assert.True(area.Contains(59.5, 18.5));
        Assert.False(area.CrossesAntimeridian);
    }

    [Fact]
    public void AreaAcrossTheAntimeridianContainsBothSides()
    {
        Assert.True(MapArea.TryCreate(-20, 170, -10, 190, out var area, out _));
        Assert.True(area.CrossesAntimeridian);
        Assert.True(area.Contains(-15, 175));
        Assert.True(area.Contains(-15, -175));
        Assert.False(area.Contains(-15, -165));
        Assert.False(area.Contains(-15, 165));
    }

    [Fact]
    public void WorldWideAreaCoversAllLongitudes()
    {
        Assert.True(MapArea.TryCreate(-85, -400, 85, 400, out var area, out _));
        Assert.True(area.CoversAllLongitudes);
        Assert.True(area.Contains(0, 179.9));
        Assert.True(area.Contains(0, -179.9));
    }

    [Fact]
    public void SelectReturnsNewestMatchesAndFlagsTruncation()
    {
        var start = DateTimeOffset.Parse("2026-11-01T00:00:00Z");
        var inside = Enumerable.Range(0, MapArea.MaxResults + 5).Select(index => new Report(Guid.NewGuid(),
            "Syntetisk rapport", "Påhittat exempel utan personuppgifter.", ReportCategory.Litter,
            59.33, 18.06, start.AddMinutes(index)));
        var outside = new Report(Guid.NewGuid(), "Syntetisk rapport", "Påhittat exempel utan personuppgifter.",
            ReportCategory.Litter, 57.7, 11.97, start.AddDays(1));
        Assert.True(MapArea.TryCreate(59.3, 18, 59.4, 18.1, out var area, out _));

        var page = area.Select(inside.Append(outside));

        Assert.True(page.Truncated);
        Assert.Equal(MapArea.MaxResults, page.Reports.Count);
        Assert.DoesNotContain(page.Reports, report => report.Id == outside.Id);
        Assert.Equal(start.AddMinutes(MapArea.MaxResults + 4), page.Reports[0].CreatedAt);
    }

    [Fact]
    public async Task DemoStoreListsOnlyReportsInArea()
    {
        var store = new DemoReportStore();
        Assert.True(MapArea.TryCreate(59.33, 18.06, 59.34, 18.07, out var bench, out _));
        Assert.True(MapArea.TryCreate(57.6, 11.9, 57.8, 12.1, out var elsewhere, out _));

        var page = await store.ListInAreaAsync(bench);

        Assert.Equal("En bänk behöver ses över", Assert.Single(page.Reports).Title);
        Assert.False(page.Truncated);
        Assert.Empty((await store.ListInAreaAsync(elsewhere)).Reports);
    }
}

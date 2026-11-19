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

public sealed class MapViewTests
{
    [Fact]
    public void ValidQueryValuesAreParsedAndRounded()
    {
        Assert.True(MapView.TryParse("59.3325123", "18.0630987", "16", out var view));
        Assert.Equal(new MapView(59.33251, 18.0631, 16), view);
    }

    [Theory]
    [InlineData(null, "18", "14")]
    [InlineData("59", "18", null)]
    [InlineData("59,33", "18", "14")]
    [InlineData("NaN", "18", "14")]
    [InlineData("86", "18", "14")]
    [InlineData("59", "181", "14")]
    [InlineData("59", "18", "2")]
    [InlineData("59", "18", "20")]
    [InlineData("59", "18", "14.5")]
    [InlineData("59", "18", "-1")]
    public void InvalidQueryValuesFallBackToTheDefaultView(string? latitude, string? longitude, string? zoom)
    {
        Assert.False(MapView.TryParse(latitude, longitude, zoom, out var view));
        Assert.Equal(MapView.Default, view);
    }
}

public sealed class ReportProximityTests
{
    [Fact]
    public void OneDegreeOfLatitudeIsAboutOneHundredElevenKilometres()
        => Assert.Equal(111_195, ReportProximity.DistanceMeters(59, 18, 60, 18), 0);

    [Fact]
    public void SelectKeepsReportsWithinRadiusNearestFirst()
    {
        static Report At(double latitude, double longitude) => new(Guid.NewGuid(), "Syntetisk rapport",
            "Påhittat exempel utan personuppgifter.", ReportCategory.Litter, latitude, longitude, DateTimeOffset.UtcNow);
        var near = At(59.3296, 18.0686);     // ~33 m north
        var nearest = At(59.32935, 18.0686); // ~6 m north
        var outside = At(59.3300, 18.0686);  // ~78 m north
        var many = Enumerable.Range(0, 10).Select(_ => At(59.3294, 18.0686));

        var result = ReportProximity.Select([near, nearest, outside, .. many], 59.3293, 18.0686);

        Assert.Equal(ReportProximity.MaxResults, result.Count);
        Assert.DoesNotContain(result, item => item.Report.Id == outside.Id);
        Assert.True(result.Zip(result.Skip(1)).All(pair => pair.First.DistanceMeters <= pair.Second.DistanceMeters));
        Assert.Equal(nearest.Id, ReportProximity.Select([near, nearest, outside], 59.3293, 18.0686)[0].Report.Id);
    }

    [Theory]
    [InlineData(double.NaN, 18)]
    [InlineData(86, 18)]
    [InlineData(59, 181)]
    public void InvalidLocationsReturnNothing(double latitude, double longitude)
        => Assert.Empty(ReportProximity.Select([new Report(Guid.NewGuid(), "Syntetisk rapport",
            "Påhittat exempel utan personuppgifter.", ReportCategory.Litter, 59, 18, DateTimeOffset.UtcNow)],
            latitude, longitude));

    [Fact]
    public async Task DemoStoreFindsOnlyTheNearbySeedReport()
    {
        var store = new DemoReportStore();
        var nearby = Assert.Single(await store.ListNearAsync(59.3295, 18.0686));
        Assert.Equal("Skräp vid gångvägen", nearby.Report.Title);
        Assert.InRange(nearby.DistanceMeters, 20, 25);
        Assert.Empty(await store.ListNearAsync(59.34, 18.08));
    }
}

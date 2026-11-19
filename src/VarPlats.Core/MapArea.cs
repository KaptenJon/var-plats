namespace VarPlats.Core;

/// <summary>
/// A latitude/longitude rectangle, for example the visible part of the map.
/// Longitude is normalised so <see cref="West"/> lies in [-180, 180). <see cref="East"/> may exceed 180
/// when the area crosses the antimeridian. Latitude is limited to ±85 like the Web Mercator map.
/// </summary>
public sealed record MapArea
{
    public const int MaxResults = 200;
    public const double MaxLatitude = 85;

    private MapArea(double south, double west, double north, double east)
        => (South, West, North, East) = (south, west, north, east);

    public double South { get; }
    public double West { get; }
    public double North { get; }
    public double East { get; }
    public bool CoversAllLongitudes => East - West >= 360;
    public bool CrossesAntimeridian => !CoversAllLongitudes && East > 180;

    public static bool TryCreate(double south, double west, double north, double east,
        out MapArea area, out string error)
    {
        area = null!;
        if (!double.IsFinite(south) || !double.IsFinite(west) || !double.IsFinite(north) || !double.IsFinite(east))
        {
            error = "Kartområdet måste anges med giltiga tal.";
            return false;
        }
        if (south > north)
        {
            error = "Kartområdets södra gräns måste ligga söder om den norra.";
            return false;
        }
        if (west > east)
        {
            error = "Kartområdets västra gräns måste ligga väster om den östra.";
            return false;
        }

        south = Math.Clamp(south, -MaxLatitude, MaxLatitude);
        north = Math.Clamp(north, -MaxLatitude, MaxLatitude);
        if (east - west >= 360)
            (west, east) = (-180, 180);
        else
        {
            var span = east - west;
            west = NormalizeLongitude(west);
            east = west + span;
        }
        area = new MapArea(south, west, north, east);
        error = "";
        return true;
    }

    public bool Contains(double latitude, double longitude)
    {
        if (latitude < South || latitude > North) return false;
        if (CoversAllLongitudes) return true;
        return (longitude >= West && longitude <= East)
            || (CrossesAntimeridian && longitude + 360 <= East);
    }

    /// <summary>Newest reports inside the area, at most <see cref="MaxResults"/>.</summary>
    public ReportPage Select(IEnumerable<Report> reports)
    {
        var matches = reports.Where(report => Contains(report.Latitude, report.Longitude))
            .OrderByDescending(report => report.CreatedAt)
            .Take(MaxResults + 1)
            .ToArray();
        return ReportPage.FromLookahead(matches);
    }

    private static double NormalizeLongitude(double longitude)
    {
        var normalized = ((longitude + 180) % 360 + 360) % 360 - 180;
        return normalized;
    }
}

/// <summary>Reports for one query. <see cref="Truncated"/> is true when more matches exist than were returned.</summary>
public sealed record ReportPage(IReadOnlyList<Report> Reports, bool Truncated)
{
    /// <param name="newestFirst">Up to <see cref="MapArea.MaxResults"/> + 1 matches, newest first.</param>
    public static ReportPage FromLookahead(IReadOnlyList<Report> newestFirst)
        => newestFirst.Count > MapArea.MaxResults
            ? new(newestFirst.Take(MapArea.MaxResults).ToArray(), true)
            : new(newestFirst, false);
}

/// <summary>A published report close to a proposed location, used to spot duplicates before submitting.</summary>
public sealed record NearbyReport(Report Report, double DistanceMeters);

public static class ReportProximity
{
    public const double RadiusMeters = 50;
    public const int MaxResults = 5;
    private const double EarthRadiusMeters = 6_371_008.8;

    /// <summary>Great-circle distance; close enough to PostGIS geography distance at this radius.</summary>
    public static double DistanceMeters(double latitude1, double longitude1, double latitude2, double longitude2)
    {
        static double Radians(double degrees) => degrees * Math.PI / 180;
        var deltaLatitude = Radians(latitude2 - latitude1);
        var deltaLongitude = Radians(longitude2 - longitude1);
        var a = Math.Pow(Math.Sin(deltaLatitude / 2), 2)
            + Math.Cos(Radians(latitude1)) * Math.Cos(Radians(latitude2)) * Math.Pow(Math.Sin(deltaLongitude / 2), 2);
        return 2 * EarthRadiusMeters * Math.Asin(Math.Min(1, Math.Sqrt(a)));
    }

    public static bool IsValidLocation(double latitude, double longitude)
        => double.IsFinite(latitude) && double.IsFinite(longitude)
            && latitude is >= -MapArea.MaxLatitude and <= MapArea.MaxLatitude
            && longitude is >= -180 and <= 180;

    /// <summary>Closest reports within <see cref="RadiusMeters"/>, at most <see cref="MaxResults"/>.</summary>
    public static IReadOnlyList<NearbyReport> Select(IEnumerable<Report> reports, double latitude, double longitude)
    {
        if (!IsValidLocation(latitude, longitude)) return [];
        return reports
            .Select(report => new NearbyReport(report,
                DistanceMeters(latitude, longitude, report.Latitude, report.Longitude)))
            .Where(nearby => nearby.DistanceMeters <= RadiusMeters)
            .OrderBy(nearby => nearby.DistanceMeters)
            .ThenByDescending(nearby => nearby.Report.CreatedAt)
            .Take(MaxResults)
            .ToArray();
    }
}

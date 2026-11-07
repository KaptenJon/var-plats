using System.ComponentModel.DataAnnotations;

namespace VarPlats.Core;

public enum ReportCategory { Litter, Broken, Other }
public enum ReportStatus { Reported, ActionDocumented, Confirmed }

public sealed record Report(
    Guid Id, string Title, string Description, ReportCategory Category,
    double Latitude, double Longitude, DateTimeOffset CreatedAt,
    ReportStatus Status = ReportStatus.Reported);

public sealed class ReportDraft : IValidatableObject
{
    [Required(ErrorMessage = "Skriv en rubrik.")]
    [StringLength(100, MinimumLength = 5, ErrorMessage = "Rubriken ska ha 5–100 tecken.")]
    public string Title { get; set; } = "";

    [Required(ErrorMessage = "Beskriv vad du har sett.")]
    [StringLength(1000, MinimumLength = 10, ErrorMessage = "Beskrivningen ska ha 10–1000 tecken.")]
    public string Description { get; set; } = "";

    [EnumDataType(typeof(ReportCategory), ErrorMessage = "Välj en giltig kategori.")]
    public ReportCategory Category { get; set; } = ReportCategory.Litter;

    [Range(-85, 85, ErrorMessage = "Latitud måste vara mellan -85 och 85.")]
    public double Latitude { get; set; } = 59.3293;

    [Range(-180, 180, ErrorMessage = "Longitud måste vara mellan -180 och 180.")]
    public double Longitude { get; set; } = 18.0686;

    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (Title.Trim().Length < 5)
            yield return new("Rubriken ska innehålla minst fem synliga tecken.", [nameof(Title)]);
        if (Description.Trim().Length < 10)
            yield return new("Beskrivningen ska innehålla minst tio synliga tecken.", [nameof(Description)]);
        if (!double.IsFinite(Latitude) || !double.IsFinite(Longitude))
            yield return new("Välj en giltig plats på kartan.", [nameof(Latitude), nameof(Longitude)]);
    }

    public Report ToReport()
    {
        Validator.ValidateObject(this, new ValidationContext(this), validateAllProperties: true);
        return new(Guid.NewGuid(), Title.Trim(), Description.Trim(), Category,
            Latitude, Longitude, DateTimeOffset.UtcNow);
    }
}

public interface IReportStore
{
    Task<IReadOnlyList<Report>> ListAsync(CancellationToken cancellationToken = default);
    Task<Report> AddAsync(ReportDraft draft, CancellationToken cancellationToken = default);
    Task<bool> RemoveAsync(Guid id, CancellationToken cancellationToken = default);
}

public sealed class DemoReportStore : IReportStore
{
    private readonly List<Report> reports =
    [
        new(Guid.Parse("00000000-0000-0000-0000-000000000001"), "Skräp vid gångvägen",
            "Syntetiskt exempel: förpackningar vid en gångväg. Ingen verklig felanmälan.",
            ReportCategory.Litter, 59.3293, 18.0686, DateTimeOffset.Parse("2026-11-01T10:00:00Z")),
        new(Guid.Parse("00000000-0000-0000-0000-000000000002"), "En bänk behöver ses över",
            "Syntetiskt exempel: en skadad bänk. Låt ansvarig fastighetsägare bedöma åtgärden.",
            ReportCategory.Broken, 59.3325, 18.0630, DateTimeOffset.Parse("2026-11-01T11:00:00Z"))
    ];

    public Task<IReadOnlyList<Report>> ListAsync(CancellationToken cancellationToken = default)
        => Task.FromResult<IReadOnlyList<Report>>(reports.ToArray());

    public Task<Report> AddAsync(ReportDraft draft, CancellationToken cancellationToken = default)
    {
        var report = draft.ToReport();
        reports.Insert(0, report);
        return Task.FromResult(report);
    }

    public Task<bool> RemoveAsync(Guid id, CancellationToken cancellationToken = default)
        => Task.FromResult(reports.RemoveAll(report => report.Id == id) > 0);
}

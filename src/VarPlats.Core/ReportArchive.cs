using System.ComponentModel.DataAnnotations;

namespace VarPlats.Core;

public enum ArchiveReason { Resolved, Outdated, Duplicate, Other }

public enum ArchiveEventKind { Archived, Restored }

public sealed class ReportArchiveDraft : IValidatableObject
{
    public const int NoteMaxLength = 500;

    [EnumDataType(typeof(ArchiveReason), ErrorMessage = "Välj en giltig anledning.")]
    public ArchiveReason Reason { get; set; } = ArchiveReason.Resolved;

    [StringLength(NoteMaxLength, ErrorMessage = "Kommentaren får ha högst 500 tecken.")]
    public string? Note { get; set; }

    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (Reason == ArchiveReason.Other && string.IsNullOrWhiteSpace(Note))
            yield return new("Beskriv kort varför rapporten arkiveras när du väljer Annat.", [nameof(Note)]);
    }

    /// <summary>Validates and returns the reason with a trimmed note, or null when the note is blank.</summary>
    public (ArchiveReason Reason, string? Note) Validated()
    {
        Validator.ValidateObject(this, new ValidationContext(this), validateAllProperties: true);
        var note = Note?.Trim();
        return (Reason, string.IsNullOrEmpty(note) ? null : note);
    }
}

/// <summary>Current archive state of a report. Archived reports leave the map, list and API but keep their page.</summary>
public sealed record ReportArchive(Guid ReportId, ArchiveReason Reason, string? Note, DateTimeOffset ArchivedAt);

public sealed record ArchivedReport(Guid ReportId, string Title, ArchiveReason Reason, DateTimeOffset ArchivedAt);

/// <summary>History entry; the admin account is deliberately not stored.</summary>
public sealed record ReportArchiveEvent(
    ArchiveEventKind Kind, ArchiveReason? Reason, string? Note, DateTimeOffset CreatedAt);

public interface IReportArchiveStore
{
    Task<bool> ArchiveAsync(Guid reportId, ReportArchiveDraft draft, CancellationToken cancellationToken = default);
    Task<bool> RestoreAsync(Guid reportId, CancellationToken cancellationToken = default);
    Task<ReportArchive?> GetAsync(Guid reportId, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<ArchivedReport>> ListAsync(CancellationToken cancellationToken = default);
    Task<IReadOnlyList<ReportArchiveEvent>> HistoryAsync(Guid reportId, CancellationToken cancellationToken = default);
}

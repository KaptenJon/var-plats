using System.ComponentModel.DataAnnotations;

namespace VarPlats.Core;

public enum FlagReason { PersonalData, Offensive, Dangerous, Spam, WrongPlace, Other }

public enum FlagResult { Flagged, AlreadyFlagged, LimitReached, NotFound }

public static class ContentFlagLimits
{
    public const int NoteMaxLength = 500;
    public const int PerAccountPerDay = 20;
    public const int QueueSize = 200;
}

public sealed class ContentFlagDraft : IValidatableObject
{
    [EnumDataType(typeof(FlagReason), ErrorMessage = "Välj en giltig anledning.")]
    public FlagReason Reason { get; set; } = FlagReason.PersonalData;

    [StringLength(ContentFlagLimits.NoteMaxLength, ErrorMessage = "Kommentaren får ha högst 500 tecken.")]
    public string? Note { get; set; }

    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (Reason == FlagReason.Other && string.IsNullOrWhiteSpace(Note))
            yield return new("Beskriv kort vad som är fel när du väljer Annat.", [nameof(Note)]);
    }

    /// <summary>Validates and returns the reason with a trimmed note, or null when the note is blank.</summary>
    public (FlagReason Reason, string? Note) Validated()
    {
        Validator.ValidateObject(this, new ValidationContext(this), validateAllProperties: true);
        var note = Note?.Trim();
        return (Reason, string.IsNullOrEmpty(note) ? null : note);
    }
}

/// <summary>
/// Open flags for one item, grouped for the admin queue. <see cref="ActionId"/> is null when the report itself
/// is flagged. Flagging accounts are never exposed, only counts, reasons and notes. <see cref="Hidden"/> is only
/// set when hide-on-flag is switched on and enough flags come from accounts other than the author.
/// </summary>
public sealed record FlaggedContent(
    Guid ReportId, Guid? ActionId, string ReportTitle, string Text, int FlagCount,
    IReadOnlyList<FlagReason> Reasons, IReadOnlyList<string> Notes, DateTimeOffset FirstFlaggedAt,
    bool Hidden = false);

/// <summary>
/// What hide-on-flag currently hides on one report page. <see cref="ViewerIsReportAuthor"/> tells whether the
/// viewing account submitted the report; the author id itself is never returned.
/// </summary>
public sealed record HiddenContent(bool ReportHidden, bool ViewerIsReportAuthor, IReadOnlySet<Guid> HiddenActionIds)
{
    public static HiddenContent None { get; } = new(false, false, new HashSet<Guid>());
}

public interface IContentFlagStore
{
    Task<FlagResult> FlagAsync(Guid reportId, Guid? actionId, Guid accountId, ContentFlagDraft draft,
        CancellationToken cancellationToken = default);
    Task<IReadOnlyList<FlaggedContent>> ListOpenAsync(CancellationToken cancellationToken = default);
    Task<int> DismissAsync(Guid reportId, Guid? actionId, CancellationToken cancellationToken = default);

    /// <summary>Hidden state of a report and its action texts. Only available when hide-on-flag is switched on.</summary>
    Task<HiddenContent> GetHiddenAsync(Guid reportId, Guid? viewerId, CancellationToken cancellationToken = default);
}

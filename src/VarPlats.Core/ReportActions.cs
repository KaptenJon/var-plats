using System.ComponentModel.DataAnnotations;

namespace VarPlats.Core;

public sealed record ReportAction(
    Guid Id, string Description, DateTimeOffset CreatedAt, int ConfirmationCount,
    bool IsOwnAction, bool HasConfirmed);

public enum ActionConfirmationResult { Confirmed, AlreadyConfirmed, OwnAction, NotFound }

public sealed class ReportActionDraft : IValidatableObject
{
    [Required(ErrorMessage = "Beskriv åtgärden.")]
    [StringLength(1000, MinimumLength = 10, ErrorMessage = "Beskrivningen ska ha 10–1000 tecken.")]
    public string Description { get; set; } = "";

    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (Description?.Trim().Length < 10)
            yield return new("Beskrivningen ska innehålla minst tio synliga tecken.", [nameof(Description)]);
    }

    public string ValidatedDescription()
    {
        Validator.ValidateObject(this, new ValidationContext(this), validateAllProperties: true);
        return Description.Trim();
    }
}

public interface IReportActionStore
{
    Task<IReadOnlyList<ReportAction>> ListAsync(
        Guid reportId, Guid? viewerId, CancellationToken cancellationToken = default);
    Task<bool> AddAsync(
        Guid reportId, Guid authorId, ReportActionDraft draft, CancellationToken cancellationToken = default);
    Task<ActionConfirmationResult> ConfirmAsync(
        Guid reportId, Guid actionId, Guid accountId, CancellationToken cancellationToken = default);
    Task<bool> RemoveAsync(
        Guid reportId, Guid actionId, CancellationToken cancellationToken = default);
}

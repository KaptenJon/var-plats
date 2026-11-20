using System.ComponentModel.DataAnnotations;

namespace VarPlats.Core;

/// <summary>
/// A documented action. <see cref="WithdrawnCount"/> and <see cref="HasWithdrawn"/> are only filled in when
/// confirmation withdrawal is switched on; withdrawn confirmations never count towards the status.
/// </summary>
public sealed record ReportAction(
    Guid Id, string Description, DateTimeOffset CreatedAt, int ConfirmationCount,
    bool IsOwnAction, bool HasConfirmed, int WithdrawnCount = 0, bool HasWithdrawn = false);

public enum ActionConfirmationResult { Confirmed, AlreadyConfirmed, OwnAction, NotFound, Withdrawn }

public enum ConfirmationWithdrawalResult { Withdrawn, NotConfirmed, Archived, NotFound }

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

    /// <summary>
    /// Withdraws the account's own active confirmation and keeps a history row. Only available when
    /// confirmation withdrawal is switched on.
    /// </summary>
    Task<ConfirmationWithdrawalResult> WithdrawConfirmationAsync(
        Guid reportId, Guid actionId, Guid accountId, CancellationToken cancellationToken = default);
}

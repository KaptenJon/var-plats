using System.ComponentModel.DataAnnotations;

namespace VarPlats.Core;

/// <summary>Why a ledger row exists. The values are stored in the database and must not be renumbered.</summary>
public enum PointsReason
{
    /// <summary>Base points when another account confirms the action. At most one unreversed award per action.</summary>
    ActionConfirmed = 0,
    /// <summary>Compensating row for an earlier award; the original row is never changed or deleted.</summary>
    Reversal = 1,
    /// <summary>The giving side of a gift: negative, shares TransferId with <see cref="GiftReceived"/>.</summary>
    GiftSent = 2,
    /// <summary>The receiving side of a gift: positive, shares TransferId with <see cref="GiftSent"/>.</summary>
    GiftReceived = 3
}

/// <summary>Why an award was taken back. Stored in the database; do not renumber.</summary>
public enum PointsReversalCause
{
    ConfirmationWithdrawn = 0,
    ActionRemoved = 1,
    ReportRemoved = 2,
    ReportArchived = 3,
    AdminRevoked = 4
}

public enum HeartResult { Added, Removed, OwnAction, NotFound, Archived, Hidden }

public enum GiftResult { Sent, OwnAction, NoRecipient, InsufficientBalance, OverTransferLimit, OverDailyLimit, NotFound, Archived, Hidden }

/// <summary>
/// Amounts and limits for the points ledger. Every ledger row stores <see cref="RuleVersion"/>, so a change of
/// amounts or limits should come with a new rule version to keep old rows traceable.
/// </summary>
public sealed class PointsOptions
{
    public int ConfirmedActionPoints { get; set; } = 10;
    public int MaxGiftPerTransfer { get; set; } = 10;
    public int MaxGiftPerDay { get; set; } = 30;
    public string RuleVersion { get; set; } = "poang-v1";
    public bool GiftsEnabled { get; set; } = true;

    public const int RuleVersionMaxLength = 32;

    public void Validate()
    {
        if (ConfirmedActionPoints is < 1 or > 1000)
            throw new InvalidOperationException("Points:ConfirmedActionPoints måste vara 1–1000.");
        if (MaxGiftPerTransfer is < 1 or > 1000)
            throw new InvalidOperationException("Points:MaxGiftPerTransfer måste vara 1–1000.");
        if (MaxGiftPerDay < MaxGiftPerTransfer || MaxGiftPerDay > 10000)
            throw new InvalidOperationException("Points:MaxGiftPerDay måste vara minst MaxGiftPerTransfer och högst 10000.");
        if (string.IsNullOrWhiteSpace(RuleVersion) || RuleVersion.Length > RuleVersionMaxLength
            || RuleVersion.Trim() != RuleVersion)
            throw new InvalidOperationException("Points:RuleVersion måste vara 1–32 tecken utan inledande eller avslutande blanksteg.");
    }
}

public sealed class PointsGiftDraft
{
    [Range(1, 1000, ErrorMessage = "Ange ett antal poäng från 1 och uppåt.")]
    public int Amount { get; set; } = 1;

    /// <summary>Validates the amount against the configured per-gift limit.</summary>
    public int Validated(PointsOptions options)
    {
        Validator.ValidateObject(this, new ValidationContext(this), validateAllProperties: true);
        if (Amount > options.MaxGiftPerTransfer)
            throw new ValidationException($"Du kan skicka högst {options.MaxGiftPerTransfer} poäng åt gången.");
        return Amount;
    }
}

/// <summary>Hearts and points shown next to an action. Hearts never give points.</summary>
public sealed record ActionAppreciation(Guid ActionId, int Hearts, int Points, bool ViewerHearted);

/// <summary>One ledger row as the account itself sees it. Gifts never reveal the other account.</summary>
public sealed record PointsEvent(
    Guid Id, int Amount, PointsReason Reason, PointsReversalCause? Cause, Guid? ReportId, string RuleVersion,
    DateTimeOffset CreatedAt);

public sealed record PointsSummary(int Balance, int GiftedToday, IReadOnlyList<PointsEvent> History);

public interface IPointsStore
{
    Task<IReadOnlyDictionary<Guid, ActionAppreciation>> ListForActionsAsync(
        IReadOnlyCollection<Guid> actionIds, Guid? viewerId, CancellationToken cancellationToken = default);
    Task<HeartResult> ToggleHeartAsync(
        Guid reportId, Guid actionId, Guid accountId, CancellationToken cancellationToken = default);
    Task<GiftResult> GiftAsync(
        Guid reportId, Guid actionId, Guid giverId, int amount, CancellationToken cancellationToken = default);
    Task<PointsSummary> GetSummaryAsync(Guid accountId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Reversal hook for confirmation withdrawal: takes back the award that came from this confirmation and,
    /// if another account still confirms the action, awards from that confirmation instead.
    /// Returns the number of reversal rows written.
    /// </summary>
    Task<int> ReverseConfirmationAsync(Guid actionId, Guid confirmerId, PointsReversalCause cause,
        CancellationToken cancellationToken = default);

    /// <summary>Reversal hook for archiving or admin revocation: takes back every unreversed award for the action.</summary>
    Task<int> ReverseActionAsync(Guid actionId, PointsReversalCause cause, CancellationToken cancellationToken = default);
}

using System.Security.Claims;
using VarPlats.Core;

namespace VarPlats.Web;

/// <summary>
/// Hearts, gifts and the account's own points. Base points are only written by the confirmation flow
/// (<see cref="Data.PointsLedger"/>); nothing here creates points. There is deliberately no public ranking.
/// </summary>
public sealed class PointsCommands(
    ReportingMode mode, PointsOptions options, IPointsStore? store = null, IReportAccountAccess? accountAccess = null)
{
    public bool GiftsEnabled => mode.CanUsePoints && options.GiftsEnabled;

    public async Task<IReadOnlyDictionary<Guid, ActionAppreciation>> ListForActionsAsync(
        ClaimsPrincipal viewer, IReadOnlyCollection<Guid> actionIds, CancellationToken cancellationToken = default)
    {
        if (!mode.Points || actionIds.Count == 0)
            return new Dictionary<Guid, ActionAppreciation>();
        var rights = await VerifyAsync(viewer, cancellationToken);
        var viewerId = ReportCommands.CanReport(viewer) && rights.CanReport
            && rights.AccountId == ReportCommands.UserId(viewer) ? rights.AccountId : null;
        return await Store().ListForActionsAsync(actionIds, viewerId, cancellationToken);
    }

    public async Task<HeartResult> ToggleHeartAsync(
        ClaimsPrincipal user, Guid reportId, Guid actionId, CancellationToken cancellationToken = default)
    {
        var accountId = await RequireWriteAsync(user, cancellationToken);
        return await Store().ToggleHeartAsync(reportId, actionId, accountId, cancellationToken);
    }

    public async Task<GiftResult> GiftAsync(ClaimsPrincipal user, Guid reportId, Guid actionId, PointsGiftDraft draft,
        CancellationToken cancellationToken = default)
    {
        if (!options.GiftsEnabled)
            throw new InvalidOperationException("Poänggåvor är inte aktiverade.");
        var accountId = await RequireWriteAsync(user, cancellationToken);
        ArgumentNullException.ThrowIfNull(draft);
        var amount = draft.Validated(options);
        return await Store().GiftAsync(reportId, actionId, accountId, amount, cancellationToken);
    }

    public async Task<PointsSummary> GetOwnAsync(ClaimsPrincipal user, CancellationToken cancellationToken = default)
    {
        if (!mode.Points)
            throw new InvalidOperationException("Poäng är inte aktiverade.");
        var accountId = await RequireAccountAsync(user, cancellationToken);
        return await Store().GetSummaryAsync(accountId, cancellationToken);
    }

    private async Task<Guid> RequireWriteAsync(ClaimsPrincipal user, CancellationToken cancellationToken)
    {
        if (!mode.CanUsePoints)
            throw new InvalidOperationException("Poäng kan inte ändras när skrivningar är avstängda.");
        return await RequireAccountAsync(user, cancellationToken);
    }

    private async Task<Guid> RequireAccountAsync(ClaimsPrincipal user, CancellationToken cancellationToken)
    {
        var rights = await VerifyAsync(user, cancellationToken);
        if (!ReportCommands.CanReport(user) || !rights.CanReport || rights.AccountId != ReportCommands.UserId(user))
            throw new UnauthorizedAccessException("Ett behörigt konto krävs.");
        return rights.AccountId!.Value;
    }

    private Task<ReportAccountRights> VerifyAsync(ClaimsPrincipal user, CancellationToken cancellationToken)
        => accountAccess?.VerifyAsync(user, cancellationToken) ?? Task.FromResult(default(ReportAccountRights));

    private IPointsStore Store()
        => mode.Points && store is not null
            ? store
            : throw new InvalidOperationException("Poäng är inte aktiverade.");
}

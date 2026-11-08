using System.Security.Claims;
using VarPlats.Core;
using VarPlats.Web.Photos;

namespace VarPlats.Web;

public sealed class ReportActionCommands(
    ReportingMode mode, IReportActionStore? store = null, IPhotoCleanup? photos = null,
    IReportAccountAccess? accountAccess = null)
{
    public async Task<IReadOnlyList<ReportAction>> ListAsync(
        ClaimsPrincipal viewer, Guid reportId, CancellationToken cancellationToken = default)
    {
        if (!mode.WorkflowEnabled)
            return [];
        var rights = await VerifyAsync(viewer, cancellationToken);
        var viewerId = ReportCommands.CanReport(viewer) && rights.CanReport
            && rights.AccountId == ReportCommands.UserId(viewer) ? rights.AccountId : null;
        return await Store().ListAsync(reportId, viewerId, cancellationToken);
    }

    public async Task<bool> AddAsync(
        ClaimsPrincipal user, Guid reportId, ReportActionDraft draft, CancellationToken cancellationToken = default)
    {
        var accountId = await RequireWriteAsync(user, cancellationToken);
        ArgumentNullException.ThrowIfNull(draft);
        draft.ValidatedDescription();
        return await Store().AddAsync(reportId, accountId, draft, cancellationToken);
    }

    public async Task<ActionConfirmationResult> ConfirmAsync(
        ClaimsPrincipal user, Guid reportId, Guid actionId, CancellationToken cancellationToken = default)
    {
        var accountId = await RequireWriteAsync(user, cancellationToken);
        return await Store().ConfirmAsync(reportId, actionId, accountId, cancellationToken);
    }

    public async Task<bool> RemoveAsync(
        ClaimsPrincipal user, Guid reportId, Guid actionId, CancellationToken cancellationToken = default)
    {
        if (!mode.PublicWorkflow)
            throw new InvalidOperationException("Åtgärder är inte aktiverade för skrivning.");
        if (!ReportCommands.CanModerate(user) || !(await VerifyAsync(user, cancellationToken)).CanModerate)
            throw new UnauthorizedAccessException("Endast administratörer kan ta bort åtgärdsuppgifter.");
        var removed = await Store().RemoveAsync(reportId, actionId, cancellationToken);
        if (removed && photos is not null)
            await photos.CleanupAsync(cancellationToken);
        return removed;
    }

    private async Task<Guid> RequireWriteAsync(ClaimsPrincipal user, CancellationToken cancellationToken)
    {
        if (!mode.PublicWorkflow)
            throw new InvalidOperationException("Åtgärder är inte aktiverade för skrivning.");
        var rights = await VerifyAsync(user, cancellationToken);
        if (!ReportCommands.CanReport(user) || !rights.CanReport || rights.AccountId != ReportCommands.UserId(user))
            throw new UnauthorizedAccessException("Ett behörigt konto krävs.");
        return rights.AccountId!.Value;
    }

    private Task<ReportAccountRights> VerifyAsync(ClaimsPrincipal user, CancellationToken cancellationToken)
        => accountAccess?.VerifyAsync(user, cancellationToken) ?? Task.FromResult(default(ReportAccountRights));

    private IReportActionStore Store()
        => mode.WorkflowEnabled && store is not null
            ? store
            : throw new InvalidOperationException("Åtgärder är inte aktiverade.");
}

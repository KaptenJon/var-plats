using System.Security.Claims;
using VarPlats.Core;

namespace VarPlats.Web;

/// <summary>
/// Abuse flags on published reports and action texts. Flagging needs a verified member or admin account and
/// public writes; the queue and dismissal are admin only. Removing flagged content goes through the existing
/// report and action commands, whose deletes cascade to the flags.
/// </summary>
public sealed class ContentFlagCommands(
    ReportingMode mode, IContentFlagStore? store = null, IReportAccountAccess? accountAccess = null)
{
    public async Task<FlagResult> FlagAsync(ClaimsPrincipal user, Guid reportId, Guid? actionId,
        ContentFlagDraft draft, CancellationToken cancellationToken = default)
    {
        if (!mode.CanFlag)
            throw new InvalidOperationException("Anmälan av innehåll är inte aktiverad.");
        ArgumentNullException.ThrowIfNull(draft);
        var rights = await VerifyAsync(user, cancellationToken);
        if (!ReportCommands.CanReport(user) || !rights.CanReport || rights.AccountId != ReportCommands.UserId(user))
            throw new UnauthorizedAccessException("Ett behörigt konto krävs för att anmäla innehåll.");
        draft.Validated();
        return await Store().FlagAsync(reportId, actionId, rights.AccountId!.Value, draft, cancellationToken);
    }

    public async Task<IReadOnlyList<FlaggedContent>> ListOpenAsync(
        ClaimsPrincipal user, CancellationToken cancellationToken = default)
    {
        if (!mode.FlagQueue)
            throw new InvalidOperationException("Anmälan av innehåll är inte aktiverad.");
        await RequireModeratorAsync(user, cancellationToken);
        return await Store().ListOpenAsync(cancellationToken);
    }

    public async Task<int> DismissAsync(ClaimsPrincipal user, Guid reportId, Guid? actionId,
        CancellationToken cancellationToken = default)
    {
        if (!mode.CanFlag)
            throw new InvalidOperationException("Anmälningar kan inte hanteras när skrivningar är avstängda.");
        await RequireModeratorAsync(user, cancellationToken);
        return await Store().DismissAsync(reportId, actionId, cancellationToken);
    }

    private async Task RequireModeratorAsync(ClaimsPrincipal user, CancellationToken cancellationToken)
    {
        if (!ReportCommands.CanModerate(user) || !(await VerifyAsync(user, cancellationToken)).CanModerate)
            throw new UnauthorizedAccessException("Endast administratörer kan hantera anmälningar.");
    }

    private Task<ReportAccountRights> VerifyAsync(ClaimsPrincipal user, CancellationToken cancellationToken)
        => accountAccess?.VerifyAsync(user, cancellationToken) ?? Task.FromResult(default(ReportAccountRights));

    private IContentFlagStore Store()
        => store ?? throw new InvalidOperationException("Anmälan av innehåll är inte aktiverad.");
}

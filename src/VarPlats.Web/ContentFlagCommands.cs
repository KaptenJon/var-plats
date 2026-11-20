using System.Security.Claims;
using VarPlats.Core;

namespace VarPlats.Web;

/// <summary>
/// Hide-on-flag state for one viewer. <see cref="CanSeeHiddenReport"/> is true for admins and the report's
/// author; a hidden action text is shown to admins and to its own author (<see cref="ReportAction.IsOwnAction"/>).
/// </summary>
public sealed record ContentVisibility(
    bool ReportHidden, bool CanSeeHiddenReport, IReadOnlySet<Guid> HiddenActionIds, bool IsModerator)
{
    public static ContentVisibility Nothing { get; } = new(false, false, new HashSet<Guid>(), false);

    /// <summary>The report's own content (title, text, photo, actions) must not be shown to this viewer.</summary>
    public bool ReportWithheld => ReportHidden && !CanSeeHiddenReport;

    public bool IsHidden(ReportAction action) => HiddenActionIds.Contains(action.Id);

    /// <summary>The action text and photo must not be shown to this viewer.</summary>
    public bool ActionWithheld(ReportAction action) => IsHidden(action) && !IsModerator && !action.IsOwnAction;
}

/// <summary>
/// Abuse flags on published reports and action texts. Flagging needs a verified member or admin account and
/// public writes; the queue and dismissal are admin only. Removing flagged content goes through the existing
/// report and action commands, whose deletes cascade to the flags.
/// </summary>
public sealed class ContentFlagCommands(
    ReportingMode mode, IContentFlagStore? store = null, IReportAccountAccess? accountAccess = null,
    WriteRateLimiter? rateLimiter = null)
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
        rateLimiter?.Acquire(rights.AccountId!.Value, WriteKind.Flag);
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

    /// <summary>
    /// What the viewer may see of flag-hidden content on one report page. Hidden content stays readable for admins
    /// and for its own author; everyone else only gets a neutral note. Without hide-on-flag nothing is hidden and
    /// content_flags is not read.
    /// </summary>
    public async Task<ContentVisibility> GetVisibilityAsync(
        ClaimsPrincipal viewer, Guid reportId, CancellationToken cancellationToken = default)
    {
        if (!mode.HideOnFlag)
            return ContentVisibility.Nothing;
        var rights = await VerifyAsync(viewer, cancellationToken);
        var accountId = ReportCommands.CanReport(viewer) && rights.CanReport
            && rights.AccountId == ReportCommands.UserId(viewer) ? rights.AccountId : null;
        var moderator = accountId is not null && rights.CanModerate && ReportCommands.CanModerate(viewer);
        var hidden = await Store().GetHiddenAsync(reportId, accountId, cancellationToken);
        return new(hidden.ReportHidden, moderator || hidden.ViewerIsReportAuthor, hidden.HiddenActionIds, moderator);
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

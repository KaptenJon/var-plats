using System.Security.Claims;
using VarPlats.Core;

namespace VarPlats.Web;

/// <summary>
/// Admin archiving of published reports. Anyone can see whether a report is archived (its page stays readable);
/// archiving, restoring, the archive list and history require a verified admin.
/// </summary>
public sealed class ReportArchiveCommands(
    ReportingMode mode, IReportArchiveStore? store = null, IReportAccountAccess? accountAccess = null)
{
    public Task<ReportArchive?> GetAsync(Guid reportId, CancellationToken cancellationToken = default)
        => mode.Archive ? Store().GetAsync(reportId, cancellationToken) : Task.FromResult<ReportArchive?>(null);

    public async Task<bool> ArchiveAsync(ClaimsPrincipal user, Guid reportId, ReportArchiveDraft draft,
        CancellationToken cancellationToken = default)
    {
        RequireWrite();
        ArgumentNullException.ThrowIfNull(draft);
        await RequireModeratorAsync(user, cancellationToken);
        draft.Validated();
        return await Store().ArchiveAsync(reportId, draft, cancellationToken);
    }

    public async Task<bool> RestoreAsync(ClaimsPrincipal user, Guid reportId, CancellationToken cancellationToken = default)
    {
        RequireWrite();
        await RequireModeratorAsync(user, cancellationToken);
        return await Store().RestoreAsync(reportId, cancellationToken);
    }

    public async Task<IReadOnlyList<ArchivedReport>> ListAsync(
        ClaimsPrincipal user, CancellationToken cancellationToken = default)
    {
        RequireRead();
        await RequireModeratorAsync(user, cancellationToken);
        return await Store().ListAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<ReportArchiveEvent>> HistoryAsync(
        ClaimsPrincipal user, Guid reportId, CancellationToken cancellationToken = default)
    {
        RequireRead();
        await RequireModeratorAsync(user, cancellationToken);
        return await Store().HistoryAsync(reportId, cancellationToken);
    }

    private void RequireRead()
    {
        if (!mode.Archive)
            throw new InvalidOperationException("Arkivering är inte aktiverad.");
    }

    private void RequireWrite()
    {
        if (!mode.CanArchive)
            throw new InvalidOperationException("Arkivering kan inte ändras när skrivningar är avstängda.");
    }

    private async Task RequireModeratorAsync(ClaimsPrincipal user, CancellationToken cancellationToken)
    {
        var rights = accountAccess is null ? default : await accountAccess.VerifyAsync(user, cancellationToken);
        if (!ReportCommands.CanModerate(user) || !rights.CanModerate)
            throw new UnauthorizedAccessException("Endast administratörer kan arkivera rapporter.");
    }

    private IReportArchiveStore Store()
        => store ?? throw new InvalidOperationException("Arkivering är inte aktiverad.");
}

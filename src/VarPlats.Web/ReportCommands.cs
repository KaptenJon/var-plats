using System.Security.Claims;
using VarPlats.Core;
using VarPlats.Web.Photos;

namespace VarPlats.Web;

public sealed class ReportCommands(
    IReportStore store, ReportingMode mode, IReportWorkflowStore? workflow = null, IPhotoCleanup? photos = null,
    IReportAccountAccess? accountAccess = null)
{
    public async Task<Report> AddAsync(
        ClaimsPrincipal user, ReportDraft draft, CancellationToken cancellationToken = default)
    {
        if (!mode.CanWrite)
            throw new InvalidOperationException("Rapportskrivning är inte aktiverad.");
        if (mode.PublicWrites)
        {
            var rights = await VerifyAsync(user, cancellationToken);
            if (!CanReport(user) || !rights.CanReport || rights.AccountId != UserId(user))
                throw new UnauthorizedAccessException("Inloggning krävs för att rapportera.");
            return await Workflow().SubmitAsync(rights.AccountId!.Value, draft, cancellationToken);
        }
        return await store.AddAsync(draft, cancellationToken);
    }

    public async Task<bool> RemoveAsync(
        ClaimsPrincipal user, Guid id, CancellationToken cancellationToken = default)
    {
        if (!mode.CanWrite)
            throw new InvalidOperationException("Rapportskrivning är inte aktiverad.");
        if (!CanModerate(user) || (mode.PublicWorkflow && !(await VerifyAsync(user, cancellationToken)).CanModerate))
            throw new UnauthorizedAccessException("Endast administratörer kan ta bort rapporter.");
        var removed = await store.RemoveAsync(id, cancellationToken);
        if (removed && photos is not null)
            await photos.CleanupAsync(cancellationToken);
        return removed;
    }

    public async Task<IReadOnlyList<ReportSubmission>> ListOwnAsync(
        ClaimsPrincipal user, CancellationToken cancellationToken = default)
    {
        var workflowStore = Workflow();
        var rights = await VerifyAsync(user, cancellationToken);
        if (!CanReport(user) || !rights.CanReport || rights.AccountId != UserId(user))
            throw new UnauthorizedAccessException("Inloggning krävs för att visa egna rapporter.");
        return await workflowStore.ListOwnAsync(rights.AccountId!.Value, cancellationToken);
    }

    public async Task<IReadOnlyList<ReportSubmission>> ListPendingAsync(
        ClaimsPrincipal user, CancellationToken cancellationToken = default)
    {
        var workflowStore = Workflow();
        if (!CanModerate(user) || !(await VerifyAsync(user, cancellationToken)).CanModerate)
            throw new UnauthorizedAccessException("Endast administratörer kan visa granskningskön.");
        return await workflowStore.ListPendingAsync(cancellationToken);
    }

    public async Task<bool> DecideAsync(
        ClaimsPrincipal user, Guid id, ReportReviewStatus decision, CancellationToken cancellationToken = default)
    {
        if (!mode.PublicWorkflow)
            throw new InvalidOperationException("Rapportgranskning är inte aktiverad för skrivning.");
        if (!CanModerate(user) || !(await VerifyAsync(user, cancellationToken)).CanModerate)
            throw new UnauthorizedAccessException("Endast administratörer kan granska rapporter.");
        if (decision is not (ReportReviewStatus.Approved or ReportReviewStatus.Rejected))
            throw new ArgumentOutOfRangeException(nameof(decision));
        return await Workflow().DecideAsync(id, decision, cancellationToken);
    }

    public static bool CanReport(ClaimsPrincipal user)
        => IsAccountSession(user)
            && (user.IsInRole("Member") || user.IsInRole("Admin"));

    public static bool CanModerate(ClaimsPrincipal user)
        => IsAccountSession(user) && user.IsInRole("Admin");

    private static bool IsAccountSession(ClaimsPrincipal user)
        => user.Identity?.IsAuthenticated == true
            && !user.HasClaim("recovery_only", "true")
            && UserId(user) is not null;

    internal static Guid? UserId(ClaimsPrincipal user)
        => Guid.TryParse(user.FindFirstValue(ClaimTypes.NameIdentifier), out var id) && id != Guid.Empty
            ? id : null;

    private Task<ReportAccountRights> VerifyAsync(ClaimsPrincipal user, CancellationToken cancellationToken)
        => accountAccess?.VerifyAsync(user, cancellationToken) ?? Task.FromResult(default(ReportAccountRights));

    private IReportWorkflowStore Workflow()
        => mode.WorkflowEnabled && workflow is not null
            ? workflow
            : throw new InvalidOperationException("Rapportgranskning är inte aktiverad.");
}

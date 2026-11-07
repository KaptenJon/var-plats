using System.Security.Claims;
using VarPlats.Core;

namespace VarPlats.Web;

public sealed class ReportCommands(IReportStore store, ReportingMode mode)
{
    public Task<Report> AddAsync(
        ClaimsPrincipal user, ReportDraft draft, CancellationToken cancellationToken = default)
    {
        if (!mode.CanWrite)
            throw new InvalidOperationException("Rapportskrivning är inte aktiverad.");
        if (mode.PublicWrites && !CanReport(user))
            throw new UnauthorizedAccessException("Inloggning krävs för att rapportera.");
        return store.AddAsync(draft, cancellationToken);
    }

    public Task<bool> RemoveAsync(
        ClaimsPrincipal user, Guid id, CancellationToken cancellationToken = default)
    {
        if (!mode.CanWrite)
            throw new InvalidOperationException("Rapportskrivning är inte aktiverad.");
        if (!user.IsInRole("Admin") || user.HasClaim("recovery_only", "true"))
            throw new UnauthorizedAccessException("Endast administratörer kan ta bort rapporter.");
        return store.RemoveAsync(id, cancellationToken);
    }

    public static bool CanReport(ClaimsPrincipal user)
        => !user.HasClaim("recovery_only", "true")
            && (user.IsInRole("Member") || user.IsInRole("Admin"));
}

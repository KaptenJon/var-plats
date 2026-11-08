using System.Security.Claims;
using Microsoft.AspNetCore.Identity;

namespace VarPlats.Web;

public readonly record struct ReportAccountRights(Guid? AccountId, bool CanReport, bool CanModerate);

public interface IReportAccountAccess
{
    Task<ReportAccountRights> VerifyAsync(ClaimsPrincipal principal, CancellationToken cancellationToken = default);
}

public sealed class IdentityReportAccountAccess(IServiceScopeFactory scopeFactory) : IReportAccountAccess
{
    public async Task<ReportAccountRights> VerifyAsync(
        ClaimsPrincipal principal, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!ReportCommands.CanReport(principal))
            return default;

        await using var scope = scopeFactory.CreateAsyncScope();
        var signInManager = scope.ServiceProvider.GetRequiredService<SignInManager<IdentityUser<Guid>>>();
        var userManager = signInManager.UserManager;
        var account = await signInManager.ValidateSecurityStampAsync(principal);
        if (account is null || !await signInManager.CanSignInAsync(account)
            || await userManager.IsLockedOutAsync(account))
            return default;

        var admin = ReportCommands.CanModerate(principal)
            && await userManager.IsInRoleAsync(account, "Admin");
        var member = principal.IsInRole("Member") && await userManager.IsInRoleAsync(account, "Member");
        cancellationToken.ThrowIfCancellationRequested();
        return admin || member ? new(account.Id, true, admin) : default;
    }
}

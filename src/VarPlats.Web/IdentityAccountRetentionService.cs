using Microsoft.AspNetCore.Identity;
using VarPlats.Web.Data;

namespace VarPlats.Web;

public sealed class IdentityAccountRetentionService(
    IServiceScopeFactory scopeFactory,
    ILogger<IdentityAccountRetentionService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromHours(24));
        do
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            var store = scope.ServiceProvider.GetRequiredService<IdentityAccountActivityStore>();
            var userManager = scope.ServiceProvider.GetRequiredService<UserManager<IdentityUser<Guid>>>();
            var deleted = await store.DeleteInactiveAccountsAsync(
                userManager, DateTimeOffset.UtcNow, stoppingToken);
            if (deleted > 0)
                logger.LogInformation("Removed {AccountCount} inactive account(s).", deleted);
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}

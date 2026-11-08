using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Identity;

namespace VarPlats.Web;

public static class AccountDeletionEndpoint
{
    public static void MapAccountDeletionEndpoint(this WebApplication app)
    {
        app.MapPost("/auth/account/delete", async (
            HttpContext context,
            IAntiforgery antiforgery,
            UserManager<IdentityUser<Guid>> userManager,
            ILoggerFactory loggerFactory) =>
        {
            try
            {
                await antiforgery.ValidateRequestAsync(context);
            }
            catch (AntiforgeryValidationException)
            {
                return Results.BadRequest();
            }

            if (context.User.HasClaim("recovery_only", "true"))
                return Results.Forbid();

            var user = await userManager.GetUserAsync(context.User);
            if (user is null)
                return Results.Unauthorized();

            var result = await userManager.DeleteAsync(user);
            if (!result.Succeeded)
            {
                loggerFactory.CreateLogger("AccountDeletion").LogError(
                    "Identity account deletion failed with errors: {ErrorCodes}.",
                    string.Join(", ", result.Errors.Select(error => error.Code)));
                return Results.Problem("Kontot kunde inte tas bort. Kontakta operatören.");
            }

            await context.SignOutAsync(IdentityConstants.ApplicationScheme);
            await context.SignOutAsync(IdentityConstants.ExternalScheme);
            return Results.LocalRedirect("/login?deleted=1");
        }).RequireAuthorization();
    }
}

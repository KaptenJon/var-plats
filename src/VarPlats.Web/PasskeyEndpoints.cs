using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.WebUtilities;

namespace VarPlats.Web;

public static class PasskeyEndpoints
{
    public static void MapPasskeyEndpoints(this WebApplication app)
    {
        var routes = app.MapGroup("/auth/passkeys");
        routes.MapPost("/request-options", async (
            HttpContext context, IAntiforgery antiforgery,
            SignInManager<IdentityUser<Guid>> signInManager) =>
        {
            if (!await IsAntiforgeryValid(context, antiforgery))
                return Results.BadRequest();

            var options = await signInManager.MakePasskeyRequestOptionsAsync(null);
            return Results.Content(options, "application/json");
        }).AllowAnonymous();

        routes.MapPost("/sign-in", async (
            HttpContext context, IAntiforgery antiforgery,
            SignInManager<IdentityUser<Guid>> signInManager, PasskeyCredentialRequest input) =>
        {
            if (!await IsAntiforgeryValid(context, antiforgery))
                return Results.BadRequest();
            if (string.IsNullOrWhiteSpace(input.CredentialJson) || input.CredentialJson.Length > 65536)
                return Results.BadRequest();
            var result = await signInManager.PasskeySignInAsync(input.CredentialJson);
            if (result.Succeeded) return Results.NoContent();
            if (result.IsLockedOut) return Results.StatusCode(StatusCodes.Status423Locked);
            return Results.Unauthorized();
        }).AllowAnonymous();

        var managedRoutes = routes.MapGroup("").RequireAuthorization();
        managedRoutes.MapPost("/creation-options", async (
            HttpContext context, IAntiforgery antiforgery,
            SignInManager<IdentityUser<Guid>> signInManager,
            UserManager<IdentityUser<Guid>> userManager) =>
        {
            if (!await IsAntiforgeryValid(context, antiforgery))
                return Results.BadRequest();
            var user = await userManager.GetUserAsync(context.User);
            if (user is null) return Results.NotFound();
            if ((await userManager.GetPasskeysAsync(user)).Count >= 10)
                return Results.Conflict("An account can have at most ten passkeys.");
            var options = await signInManager.MakePasskeyCreationOptionsAsync(new PasskeyUserEntity
            {
                Id = await userManager.GetUserIdAsync(user),
                Name = "varplats-user",
                DisplayName = "Vår plats"
            });
            return Results.Content(options, "application/json");
        });

        managedRoutes.MapPost("/register", async (
            HttpContext context, IAntiforgery antiforgery,
            SignInManager<IdentityUser<Guid>> signInManager,
            UserManager<IdentityUser<Guid>> userManager,
            PasskeyCredentialRequest input) =>
        {
            if (!await IsAntiforgeryValid(context, antiforgery))
                return Results.BadRequest();
            var user = await userManager.GetUserAsync(context.User);
            if (user is null) return Results.NotFound();
            if ((await userManager.GetPasskeysAsync(user)).Count >= 10)
                return Results.Conflict("An account can have at most ten passkeys.");
            if (string.IsNullOrWhiteSpace(input.CredentialJson) || input.CredentialJson.Length > 65536)
                return Results.BadRequest();

            var attestation = await signInManager.PerformPasskeyAttestationAsync(input.CredentialJson);
            if (!attestation.Succeeded || attestation.Passkey is null)
                return Results.BadRequest("The passkey could not be verified.");
            var addResult = await userManager.AddOrUpdatePasskeyAsync(user, attestation.Passkey);
            if (!addResult.Succeeded)
                return Results.BadRequest("The passkey could not be saved.");

            if (!await userManager.IsInRoleAsync(user, "Member")
                && !await userManager.IsInRoleAsync(user, "Admin"))
            {
                var roleResult = await userManager.AddToRoleAsync(user, "Member");
                if (!roleResult.Succeeded)
                {
                    await userManager.RemovePasskeyAsync(user, attestation.Passkey.CredentialId);
                    return Results.Problem("The account role could not be initialized.");
                }
            }

            await signInManager.RefreshSignInAsync(user);
            return Results.NoContent();
        });

        managedRoutes.MapPost("/remove/{credentialId}", async (
            HttpContext context, IAntiforgery antiforgery,
            UserManager<IdentityUser<Guid>> userManager, string credentialId) =>
        {
            if (!await IsAntiforgeryValid(context, antiforgery))
                return Results.BadRequest();
            if (context.User.HasClaim("recovery_only", "true"))
                return Results.Forbid();
            var user = await userManager.GetUserAsync(context.User);
            if (user is null) return Results.NotFound();
            if (credentialId.Length > 1366) return Results.BadRequest();
            byte[] decodedId;
            try { decodedId = WebEncoders.Base64UrlDecode(credentialId); }
            catch (FormatException) { return Results.BadRequest(); }
            if (decodedId.Length is 0 or > 1023) return Results.BadRequest();

            var passkeys = await userManager.GetPasskeysAsync(user);
            var logins = await userManager.GetLoginsAsync(user);
            if (passkeys.Count <= 1 && logins.Count == 0 && !await userManager.HasPasswordAsync(user))
                return Results.Conflict("Add another sign-in method before removing this passkey.");
            var result = await userManager.RemovePasskeyAsync(user, decodedId);
            return result.Succeeded ? Results.NoContent() : Results.NotFound();
        });
    }

    private static async Task<bool> IsAntiforgeryValid(HttpContext context, IAntiforgery antiforgery)
    {
        try
        {
            await antiforgery.ValidateRequestAsync(context);
            return true;
        }
        catch (AntiforgeryValidationException)
        {
            return false;
        }
    }
    public sealed record PasskeyCredentialRequest(string? CredentialJson);
}

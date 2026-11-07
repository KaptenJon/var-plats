using System.Security.Claims;
using System.Text;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.AspNetCore.WebUtilities;

namespace VarPlats.Web;

public static class EmailAccountEndpoints
{
    public static void MapEmailAccountEndpoints(this WebApplication app, string publicBaseUrl)
    {
        app.MapPost("/auth/email/register", async (
            HttpContext context, IAntiforgery antiforgery,
            UserManager<IdentityUser<Guid>> userManager, MailjetEmailSender emailSender,
            ILoggerFactory loggerFactory) =>
        {
            if (!await IsAntiforgeryValid(context, antiforgery))
                return Results.BadRequest();
            var form = await context.Request.ReadFormAsync(context.RequestAborted);
            var email = form["email"].ToString().Trim();
            if (IsValidEmail(email))
            {
                var user = await userManager.FindByEmailAsync(email);
                if (user is null)
                {
                    user = new IdentityUser<Guid> { UserName = email, Email = email };
                    var createResult = await userManager.CreateAsync(user);
                    if (!createResult.Succeeded) user = null;
                }

                if (user is not null && !await userManager.IsEmailConfirmedAsync(user))
                {
                    var token = await userManager.GenerateEmailConfirmationTokenAsync(user);
                    var link = AccountLink(publicBaseUrl, "/auth/email/confirm", user.Id, token);
                    await TrySendAsync(emailSender, loggerFactory, email,
                        "Bekräfta din e-postadress – Vår plats",
                        "Bekräfta din e-postadress för att skapa ett konto.", link, context.RequestAborted);
                }
            }
            return Results.LocalRedirect("/login?registered=1");
        }).AllowAnonymous().RequireRateLimiting("account-email");

        app.MapPost("/auth/email/confirm", async (
            HttpContext context, IAntiforgery antiforgery,
            UserManager<IdentityUser<Guid>> userManager, SignInManager<IdentityUser<Guid>> signInManager) =>
        {
            if (!await IsAntiforgeryValid(context, antiforgery))
                return Results.BadRequest();
            var form = await context.Request.ReadFormAsync(context.RequestAborted);
            if (!Guid.TryParse(form["userId"], out var id))
                return Results.LocalRedirect("/login?error=verification");
            var user = await userManager.FindByIdAsync(id.ToString());
            if (user is null) return Results.LocalRedirect("/login?error=verification");
            if (!TryDecodeToken(form["token"].ToString(), out var token))
                return Results.LocalRedirect("/login?error=verification");
            var result = await userManager.ConfirmEmailAsync(user, token);
            if (!result.Succeeded) return Results.LocalRedirect("/login?error=verification");
            await signInManager.SignInAsync(user, isPersistent: false);
            return Results.LocalRedirect("/account/connections?verified=1");
        }).AllowAnonymous();

        app.MapPost("/auth/email/recovery-request", async (
            HttpContext context, IAntiforgery antiforgery,
            UserManager<IdentityUser<Guid>> userManager, MailjetEmailSender emailSender,
            ILoggerFactory loggerFactory) =>
        {
            if (!await IsAntiforgeryValid(context, antiforgery))
                return Results.BadRequest();
            var form = await context.Request.ReadFormAsync(context.RequestAborted);
            var email = form["email"].ToString().Trim();
            if (IsValidEmail(email))
            {
                var user = await userManager.FindByEmailAsync(email);
                if (user is not null && await userManager.IsEmailConfirmedAsync(user))
                {
                    var token = await userManager.GenerateUserTokenAsync(
                        user, TokenOptions.DefaultEmailProvider, "EmailRecovery");
                    var link = AccountLink(publicBaseUrl, "/recover", user.Id, token);
                    await TrySendAsync(emailSender, loggerFactory, email,
                        "Återställ inloggning – Vår plats",
                        "Använd länken för att återställa din inloggning. Om du inte begärde detta kan du ignorera meddelandet.",
                        link, context.RequestAborted);
                }
            }
            return Results.LocalRedirect("/recover?sent=1");
        }).AllowAnonymous().RequireRateLimiting("account-email");

        app.MapPost("/auth/email/recover", async (
            HttpContext context, IAntiforgery antiforgery,
            UserManager<IdentityUser<Guid>> userManager, SignInManager<IdentityUser<Guid>> signInManager) =>
        {
            if (!await IsAntiforgeryValid(context, antiforgery))
                return Results.BadRequest();
            var form = await context.Request.ReadFormAsync(context.RequestAborted);
            if (!Guid.TryParse(form["userId"], out var id))
                return Results.LocalRedirect("/recover?error=invalid");
            var user = await userManager.FindByIdAsync(id.ToString());
            if (!TryDecodeToken(form["token"].ToString(), out var token))
                return Results.LocalRedirect("/recover?error=invalid");
            if (user is null || !await userManager.IsEmailConfirmedAsync(user)
                || !await userManager.VerifyUserTokenAsync(
                    user, TokenOptions.DefaultEmailProvider, "EmailRecovery", token))
                return Results.LocalRedirect("/recover?error=invalid");

            await userManager.UpdateSecurityStampAsync(user);
            await signInManager.SignInWithClaimsAsync(user,
                new AuthenticationProperties
                {
                    IsPersistent = false,
                    ExpiresUtc = DateTimeOffset.UtcNow.AddMinutes(15)
                },
                [new Claim("recovery_only", "true")]);
            return Results.LocalRedirect("/account/connections?recovery=1");
        }).AllowAnonymous();
    }

    private static bool IsValidEmail(string email)
        => email.Length <= 254
            && new System.ComponentModel.DataAnnotations.EmailAddressAttribute().IsValid(email);

    private static string AccountLink(string publicBaseUrl, string path, Guid userId, string token)
    {
        var encodedToken = WebEncoders.Base64UrlEncode(Encoding.UTF8.GetBytes(token));
        return $"{publicBaseUrl.TrimEnd('/')}{path}?userId={userId:D}&token={UrlEncoder.Default.Encode(encodedToken)}";
    }

    private static bool TryDecodeToken(string encodedToken, out string token)
    {
        try
        {
            token = Encoding.UTF8.GetString(WebEncoders.Base64UrlDecode(encodedToken));
            return true;
        }
        catch (FormatException)
        {
            token = "";
            return false;
        }
    }

    private static async Task TrySendAsync(
        MailjetEmailSender emailSender, ILoggerFactory loggerFactory, string email,
        string subject, string message, string link, CancellationToken cancellationToken)
    {
        try
        {
            await emailSender.SendAccountLinkAsync(email, subject, message, link, cancellationToken);
        }
        catch (HttpRequestException)
        {
            loggerFactory.CreateLogger("AccountEmail").LogWarning(
                "Mail provider rejected an account email request.");
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            loggerFactory.CreateLogger("AccountEmail").LogWarning(
                "Mail provider timed out for an account email request.");
        }
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
}

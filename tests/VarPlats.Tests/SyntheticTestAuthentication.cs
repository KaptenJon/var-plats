using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using VarPlats.Web;

namespace VarPlats.Tests;

internal static class SyntheticTestAuthentication
{
    public static void AddSyntheticTestAuthentication(this IServiceCollection services)
    {
        services.AddSingleton<IReportAccountAccess>(SyntheticReportAccountAccess.Instance);
        services.AddAuthentication(options =>
        {
            options.DefaultScheme = "SyntheticTest";
            options.DefaultAuthenticateScheme = "SyntheticTest";
            options.DefaultChallengeScheme = "SyntheticTest";
        }).AddScheme<AuthenticationSchemeOptions, SyntheticTestHandler>("SyntheticTest", _ => { });
    }

    private sealed class SyntheticTestHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
        : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            var role = Request.Headers["X-Synthetic-Role"].ToString();
            if (string.IsNullOrEmpty(role))
                return Task.FromResult(AuthenticateResult.NoResult());
            var claims = new List<Claim>
            {
                new(ClaimTypes.Role, role),
                new(ClaimTypes.NameIdentifier, Request.Headers["X-Synthetic-Id"].ToString())
            };
            if (Request.Headers["X-Synthetic-Recovery"] == "true")
                claims.Add(new Claim("recovery_only", "true"));
            var principal = new ClaimsPrincipal(new ClaimsIdentity(claims, Scheme.Name));
            return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(principal, Scheme.Name)));
        }
    }
}

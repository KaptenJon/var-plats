using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OAuth;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using VarPlats.Web;
using Xunit;

namespace VarPlats.Tests;

public sealed class ExternalSignInTests
{
    [Theory]
    [InlineData(false, "email", "/login?error=email")]
    [InlineData(false, "provider", "/login?error=provider")]
    [InlineData(true, "email", "/account/connections?error=link")]
    public async Task KnownFailuresRedirectWithTheirReason(bool signedIn, string code, string expected)
    {
        var (http, context) = CreateFailure(signedIn, new ExternalSignInException(code, "Syntetisk orsak."));

        await ExternalSignInFailures.HandleAsync(context);

        Assert.Equal(expected, http.Response.Headers.Location.ToString());
        Assert.True(context.Result?.Handled);
    }

    [Fact]
    public async Task OtherRemoteFailuresUseTheGeneralError()
    {
        var (http, context) = CreateFailure(false, new InvalidOperationException("Correlation failed."));

        await ExternalSignInFailures.HandleAsync(context);

        Assert.Equal("/login?error=external", http.Response.Headers.Location.ToString());
        Assert.True(context.Result?.Handled);
    }

    [Fact]
    public void OnlyControlledMessagesAreLoggedVerbatim()
    {
        Assert.Equal("GitHub /user/emails svarade 403.",
            ExternalSignInFailures.Reason(new ExternalSignInException("email", "GitHub /user/emails svarade 403.")));
        Assert.Equal("token-anropet till leverantören misslyckades", ExternalSignInFailures.Reason(
            new InvalidOperationException("OAuth token endpoint failure: Body: {\"login\":\"syntetisk@example.test\"}")));
        var unknown = ExternalSignInFailures.Reason(new InvalidOperationException("syntetisk@example.test"));
        Assert.Equal("oväntat fel (InvalidOperationException)", unknown);
        Assert.DoesNotContain("example.test", unknown, StringComparison.Ordinal);
    }

    private static (DefaultHttpContext Http, RemoteFailureContext Context) CreateFailure(bool signedIn, Exception failure)
    {
        var http = new DefaultHttpContext
        {
            RequestServices = new ServiceCollection().AddLogging().BuildServiceProvider()
        };
        if (signedIn)
            http.User = new ClaimsPrincipal(new ClaimsIdentity(
                [new Claim(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString())], "Syntetisk"));
        var scheme = new AuthenticationScheme("GitHub", "GitHub", typeof(CookieAuthenticationHandler));
        return (http, new RemoteFailureContext(http, scheme, new OAuthOptions(), failure));
    }
}

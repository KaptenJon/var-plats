using Microsoft.AspNetCore.Authentication;

namespace VarPlats.Web;

/// <summary>
/// Stoppar en extern inloggning. <c>context.Fail</c> i <c>OnCreatingTicket</c> stoppar inte OAuth-hanteraren:
/// biljetten skapas ändå, utan identitet, och felet syns bara som ett tyst misslyckande. Ett undantag
/// går i stället till <see cref="ExternalSignInFailures.HandleAsync"/>.
/// </summary>
public sealed class ExternalSignInException(string errorCode, string reason) : Exception(reason)
{
    /// <summary>Felkod för inloggningssidan, till exempel <c>email</c> eller <c>provider</c>.</summary>
    public string ErrorCode { get; } = errorCode;
}

public static class ExternalSignInFailures
{
    public const string Email = "email";
    public const string Provider = "provider";
    public const string External = "external";

    /// <summary>
    /// Loggar orsaken utan personuppgifter och skickar användaren till en sida med ett begripligt fel,
    /// i stället för ett 500-svar.
    /// </summary>
    public static Task HandleAsync(RemoteFailureContext context)
    {
        var code = context.Failure is ExternalSignInException failure ? failure.ErrorCode : External;
        var reason = context.Failure?.Message ?? "okänd orsak";
        if (reason.Length > 300)
            reason = reason[..300];
        context.HttpContext.RequestServices.GetRequiredService<ILoggerFactory>()
            .CreateLogger("ExternalLogin")
            .LogWarning("Extern inloggning via {Scheme} misslyckades ({Code}): {Reason}",
                context.Scheme.Name, code, reason);
        context.Response.Redirect(RedirectFor(context.HttpContext.User.Identity?.IsAuthenticated == true, code));
        context.HandleResponse();
        return Task.CompletedTask;
    }

    /// <summary>Inloggade konton länkade en leverantör; övriga försökte logga in.</summary>
    public static string RedirectFor(bool signedIn, string code)
        => signedIn ? "/account/connections?error=link" : $"/login?error={Uri.EscapeDataString(code)}";
}

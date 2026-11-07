using System.Net;
using System.Net.Http.Headers;
using System.Globalization;
using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.Google;
using Microsoft.AspNetCore.Authentication.OAuth;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.EntityFrameworkCore;
using Radzen;
using VarPlats.Core;
using VarPlats.ServiceDefaults;
using VarPlats.Web;
using VarPlats.Web.Components;
using VarPlats.Web.Data;

var builder = WebApplication.CreateBuilder(args);
builder.AddServiceDefaults();
var demo = builder.Configuration.GetValue<bool>("Reporting:Demo");
var localWrites = builder.Configuration.GetValue<bool>("Reporting:LocalWrites");
var authenticationEnabled = builder.Configuration.GetValue<bool>("Authentication:Enabled");
if ((demo || localWrites) && !builder.Environment.IsDevelopment())
    throw new InvalidOperationException("Demo och lokala skrivningar är endast tillåtna i Development.");

var adminGitHubId = builder.Configuration["Authentication:AdminGitHubId"];
var trustedProxyValue = builder.Configuration["Authentication:TrustedProxyIp"];
IPAddress? trustedProxyIp = null;
if (!string.IsNullOrWhiteSpace(trustedProxyValue) && !IPAddress.TryParse(trustedProxyValue, out trustedProxyIp))
    throw new InvalidOperationException("Authentication:TrustedProxyIp must be a single IP address.");
if (authenticationEnabled)
{
    var requiredSettings = new[]
    {
        "Authentication:Google:ClientId",
        "Authentication:Google:ClientSecret",
        "Authentication:GitHub:ClientId",
        "Authentication:GitHub:ClientSecret"
    };
    if (requiredSettings.Any(key => string.IsNullOrWhiteSpace(builder.Configuration[key]))
        || !long.TryParse(adminGitHubId, NumberStyles.None, CultureInfo.InvariantCulture, out var parsedAdminId)
        || parsedAdminId <= 0 || trustedProxyIp is null)
        throw new InvalidOperationException(
            "Google/GitHub OAuth credentials, Authentication:AdminGitHubId, and Authentication:TrustedProxyIp are required.");
    adminGitHubId = parsedAdminId.ToString(CultureInfo.InvariantCulture);
}

builder.Services.AddSingleton(new ReportingMode(demo, localWrites));
builder.Services.Configure<ForwardedHeadersOptions>(options =>
{
    options.ForwardedHeaders = ForwardedHeaders.XForwardedProto;
    options.KnownProxies.Clear();
    options.KnownIPNetworks.Clear();
    options.KnownProxies.Add(IPAddress.Loopback);
    if (trustedProxyIp is not null && !trustedProxyIp.Equals(IPAddress.Loopback))
        options.KnownProxies.Add(trustedProxyIp);
    options.ForwardLimit = 1;
});
builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie(options =>
    {
        options.Cookie.Name = "__Host-varplats-auth";
        options.Cookie.HttpOnly = true;
        options.Cookie.IsEssential = true;
        options.Cookie.Path = "/";
        options.Cookie.SameSite = SameSiteMode.Lax;
        options.Cookie.SecurePolicy = CookieSecurePolicy.Always;
        options.SlidingExpiration = true;
        options.LoginPath = "/login";
    });
builder.Services.AddAuthorization();
builder.Services.AddCascadingAuthenticationState();
if (authenticationEnabled)
{
    builder.Services.AddAuthentication()
        .AddGoogle(options =>
        {
            options.ClientId = builder.Configuration["Authentication:Google:ClientId"]!;
            options.ClientSecret = builder.Configuration["Authentication:Google:ClientSecret"]!;
            options.UserInformationEndpoint = "https://openidconnect.googleapis.com/v1/userinfo";
            options.Scope.Remove("profile");
            options.ClaimActions.Clear();
            options.ClaimActions.MapJsonKey(ClaimTypes.NameIdentifier, "sub");
            options.Events.OnCreatingTicket = context =>
            {
                if (!context.User.TryGetProperty("email_verified", out var verified)
                    || verified.ValueKind != JsonValueKind.True
                    || !context.User.TryGetProperty("email", out var emailElement)
                    || string.IsNullOrWhiteSpace(emailElement.GetString()))
                {
                    context.Fail("A verified Google email address is required.");
                    return Task.CompletedTask;
                }

                var identity = context.Identity!;
                identity.AddClaim(new("email_verified", "true"));
                identity.AddClaim(new(ClaimTypes.Role, "Member"));
                return Task.CompletedTask;
            };
        })
        .AddOAuth("GitHub", options =>
        {
            options.ClientId = builder.Configuration["Authentication:GitHub:ClientId"]!;
            options.ClientSecret = builder.Configuration["Authentication:GitHub:ClientSecret"]!;
            options.CallbackPath = "/signin-github";
            options.AuthorizationEndpoint = "https://github.com/login/oauth/authorize";
            options.TokenEndpoint = "https://github.com/login/oauth/access_token";
            options.UserInformationEndpoint = "https://api.github.com/user";
            options.BackchannelHttpHandler = new GitHubUserAgentHandler();
            options.Scope.Add("read:user");
            options.Scope.Add("user:email");
            options.Events.OnCreatingTicket = async context =>
            {
                using var userRequest = new HttpRequestMessage(HttpMethod.Get, options.UserInformationEndpoint);
                userRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", context.AccessToken);
                userRequest.Headers.Add("X-GitHub-Api-Version", "2022-11-28");
                using var userResponse = await context.Backchannel.SendAsync(
                    userRequest, context.HttpContext.RequestAborted);
                if (!userResponse.IsSuccessStatusCode)
                {
                    context.Fail("GitHub account information could not be verified.");
                    return;
                }

                using var userDocument = await JsonDocument.ParseAsync(
                    await userResponse.Content.ReadAsStreamAsync(context.HttpContext.RequestAborted),
                    cancellationToken: context.HttpContext.RequestAborted);
                var user = userDocument.RootElement;
                if (!user.TryGetProperty("id", out var idElement)
                    || idElement.ValueKind != JsonValueKind.Number
                    || !idElement.TryGetInt64(out var verifiedGitHubId))
                {
                    context.Fail("GitHub returned an invalid account identifier.");
                    return;
                }

                using var request = new HttpRequestMessage(HttpMethod.Get, "https://api.github.com/user/emails");
                request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
                request.Headers.Add("X-GitHub-Api-Version", "2022-11-28");
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", context.AccessToken);
                using var response = await context.Backchannel.SendAsync(request, context.HttpContext.RequestAborted);
                if (!response.IsSuccessStatusCode)
                {
                    context.Fail("GitHub did not provide a verifiable email address.");
                    return;
                }

                using var document = await JsonDocument.ParseAsync(
                    await response.Content.ReadAsStreamAsync(context.HttpContext.RequestAborted),
                    cancellationToken: context.HttpContext.RequestAborted);
                var verifiedEmail = document.RootElement.EnumerateArray()
                    .Where(address => address.TryGetProperty("primary", out var primary)
                        && primary.ValueKind == JsonValueKind.True
                        && address.TryGetProperty("verified", out var verified)
                        && verified.ValueKind == JsonValueKind.True)
                    .Select(address => address.GetProperty("email").GetString())
                    .FirstOrDefault(email => !string.IsNullOrWhiteSpace(email));
                if (verifiedEmail is null)
                {
                    context.Fail("GitHub did not provide a verified primary email address.");
                    return;
                }

                var identity = context.Identity!;
                identity.AddClaim(new(ClaimTypes.NameIdentifier,
                    verifiedGitHubId.ToString(CultureInfo.InvariantCulture)));
                identity.AddClaim(new("email_verified", "true"));
                identity.AddClaim(new(ClaimTypes.Role,
                    verifiedGitHubId.ToString(CultureInfo.InvariantCulture) == adminGitHubId ? "Admin" : "Member"));
            };
        });
}
builder.Services.AddOptions<MapOptions>().BindConfiguration("Map")
    .Validate(options => Uri.TryCreate(options.TileUrl, UriKind.Absolute, out var uri)
        && uri.Scheme == "https" && !string.IsNullOrWhiteSpace(options.Attribution),
        "Map:TileUrl måste vara en HTTPS-URL och attribution måste anges.")
    .ValidateOnStart();
if (demo)
    builder.Services.AddScoped<IReportStore, DemoReportStore>();
else
{
    var connection = builder.Configuration.GetConnectionString("varplats")
        ?? throw new InvalidOperationException("ConnectionStrings:varplats krävs. Se README för lokal demo.");
    builder.Services.AddDbContextFactory<ReportDbContext>(options =>
        options.UseNpgsql(connection, postgres => postgres.UseNetTopologySuite()));
    builder.Services.AddScoped<IReportStore, DatabaseReportStore>();
    builder.Services.AddHealthChecks().AddDbContextCheck<ReportDbContext>();
}
builder.Services.AddRazorComponents().AddInteractiveServerComponents();
builder.Services.AddRadzenComponents();
builder.Services.AddProblemDetails();
builder.Services.AddExceptionHandler<PublicExceptionHandler>();
var app = builder.Build();
app.UseForwardedHeaders();
app.UseExceptionHandler();
if (!app.Environment.IsDevelopment())
    app.UseHsts();
app.Use(async (context, next) =>
{
    // Local write mode must not become an unauthenticated public preview.
    if ((demo || localWrites) && (context.Connection.RemoteIpAddress is not { } address || !IPAddress.IsLoopback(address)))
    {
        context.Response.StatusCode = StatusCodes.Status403Forbidden;
        return;
    }
    context.Response.Headers["X-Content-Type-Options"] = "nosniff";
    context.Response.Headers["Referrer-Policy"] = "strict-origin-when-cross-origin";
    context.Response.Headers["X-Frame-Options"] = "DENY";
    context.Response.Headers["Permissions-Policy"] = "camera=(), microphone=(), geolocation=()";
    await next();
});
app.UseAuthentication();
app.UseAuthorization();
app.UseAntiforgery();
app.MapStaticAssets();
app.MapDefaultEndpoints();
app.MapGet("/api/reports", async (IReportStore store, CancellationToken cancellationToken) =>
    Results.Ok(await store.ListAsync(cancellationToken)));
if (authenticationEnabled)
{
    app.MapPost("/auth/signin/google", async (HttpContext context, IAntiforgery antiforgery) =>
    {
        await antiforgery.ValidateRequestAsync(context);
        return Results.Challenge(new AuthenticationProperties { RedirectUri = "/" },
            [GoogleDefaults.AuthenticationScheme]);
    }).AllowAnonymous();
    app.MapPost("/auth/signin/github", async (HttpContext context, IAntiforgery antiforgery) =>
    {
        await antiforgery.ValidateRequestAsync(context);
        return Results.Challenge(new AuthenticationProperties { RedirectUri = "/" }, ["GitHub"]);
    }).AllowAnonymous();
    app.MapPost("/auth/logout", async (HttpContext context, IAntiforgery antiforgery) =>
    {
        await antiforgery.ValidateRequestAsync(context);
        await context.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
        return Results.LocalRedirect("/");
    }).RequireAuthorization();
}
app.MapRazorComponents<App>().AddInteractiveServerRenderMode();
app.Run();

public partial class Program;

using System.Net;
using System.Net.Http.Headers;
using System.Globalization;
using System.Security.Claims;
using System.Text.Json;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.Google;
using Microsoft.AspNetCore.Authentication.OAuth;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Radzen;
using VarPlats.Core;
using VarPlats.ServiceDefaults;
using VarPlats.Web;
using VarPlats.Web.Components;
using VarPlats.Web.Data;
using VarPlats.Web.Photos;

var builder = WebApplication.CreateBuilder(args);
builder.AddServiceDefaults();
var demo = builder.Configuration.GetValue<bool>("Reporting:Demo");
var localWrites = builder.Configuration.GetValue<bool>("Reporting:LocalWrites");
var publicWrites = builder.Configuration.GetValue<bool>("Reporting:PublicWrites");
var workflowEnabled = builder.Configuration.GetValue<bool>("Reporting:WorkflowEnabled");
var authenticationEnabled = builder.Configuration.GetValue<bool>("Authentication:Enabled");
if ((demo || localWrites) && !builder.Environment.IsDevelopment())
    throw new InvalidOperationException("Demo och lokala skrivningar är endast tillåtna i Development.");
if (publicWrites && (demo || localWrites))
    throw new InvalidOperationException("Publika skrivningar kan inte kombineras med demo eller lokala skrivningar.");
if (publicWrites && !authenticationEnabled)
    throw new InvalidOperationException("Reporting:PublicWrites kräver Authentication:Enabled.");
if (publicWrites && !workflowEnabled)
    throw new InvalidOperationException("Reporting:PublicWrites kräver uttryckligt Reporting:WorkflowEnabled.");
if (workflowEnabled && !authenticationEnabled)
    throw new InvalidOperationException("Reporting:WorkflowEnabled kräver Authentication:Enabled.");
if (workflowEnabled && (demo || localWrites))
    throw new InvalidOperationException("Rapportgranskning kan inte kombineras med demo eller lokala skrivningar.");
if (authenticationEnabled && demo)
    throw new InvalidOperationException("Autentisering kräver databasläge och kan inte användas med Reporting:Demo.");

var reportingMode = new ReportingMode(demo, localWrites, publicWrites, workflowEnabled);
var photoOptions = builder.Configuration.GetSection("Photos").Get<PhotoOptions>() ?? new PhotoOptions();
photoOptions.Validate(reportingMode, authenticationEnabled,
    builder.Environment.WebRootPath ?? Path.Combine(builder.Environment.ContentRootPath, "wwwroot"));

var adminGitHubId = builder.Configuration["Authentication:AdminGitHubId"];
var trustedProxyValue = builder.Configuration["Authentication:TrustedProxyIp"];
var passkeyServerDomain = builder.Configuration["Authentication:Passkeys:ServerDomain"];
var passkeyOrigins = builder.Configuration.GetSection("Authentication:Passkeys:Origins").Get<string[]>() ?? [];
var publicBaseUrl = builder.Configuration["Authentication:PublicBaseUrl"];
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
        "Authentication:GitHub:ClientSecret",
        "Authentication:Mailjet:ApiKey",
        "Authentication:Mailjet:ApiSecret",
        "Authentication:Mailjet:SenderEmail",
        "Authentication:Passkeys:ServerDomain",
        "Authentication:PublicBaseUrl"
    };
    if (requiredSettings.Any(key => string.IsNullOrWhiteSpace(builder.Configuration[key]))
        || !long.TryParse(adminGitHubId, NumberStyles.None, CultureInfo.InvariantCulture, out var parsedAdminId)
        || parsedAdminId <= 0 || trustedProxyIp is null)
        throw new InvalidOperationException(
            "Google/GitHub OAuth credentials, Authentication:AdminGitHubId, and Authentication:TrustedProxyIp are required.");
    adminGitHubId = parsedAdminId.ToString(CultureInfo.InvariantCulture);

    if (!Uri.TryCreate(publicBaseUrl, UriKind.Absolute, out var publicUri)
        || publicUri.Scheme != Uri.UriSchemeHttps
        || publicUri.AbsolutePath != "/"
        || !string.IsNullOrEmpty(publicUri.UserInfo)
        || !string.IsNullOrEmpty(publicUri.Query)
        || !string.IsNullOrEmpty(publicUri.Fragment)
        || !string.Equals(publicUri.Host, passkeyServerDomain, StringComparison.OrdinalIgnoreCase)
        || !passkeyOrigins.Contains(publicUri.GetLeftPart(UriPartial.Authority), StringComparer.OrdinalIgnoreCase)
        || passkeyOrigins.Any(origin => !IsAllowedPasskeyOrigin(origin, passkeyServerDomain!)))
        throw new InvalidOperationException(
            "Authentication:PublicBaseUrl and Passkeys settings must use HTTPS and an exact allowed origin.");
}

builder.Services.AddSingleton(reportingMode);
builder.Services.AddSingleton(photoOptions);
builder.Services.AddScoped<ReportCommands>();
builder.Services.AddScoped<ReportActionCommands>();
builder.Services.AddScoped<PhotoUploadCommands>();
if (photoOptions.Enabled)
{
    builder.Services.AddSingleton<IPhotoFileStorage, LinuxPhotoStorage>();
    builder.Services.AddSingleton<IPhotoMalwareScanner, ClamAvUnixScanner>();
    builder.Services.AddSingleton<PhotoProcessor>();
    builder.Services.AddScoped<DatabasePhotoWriter>();
    builder.Services.AddScoped<IPhotoWriter>(services => services.GetRequiredService<DatabasePhotoWriter>());
    builder.Services.AddScoped<IPhotoCleanup>(services => services.GetRequiredService<DatabasePhotoWriter>());
    builder.Services.AddScoped<IPhotoReader, DatabasePhotoReader>();
}
else
{
    builder.Services.AddSingleton<IPhotoReader, DisabledPhotoReader>();
    builder.Services.AddSingleton<IPhotoCleanup, DisabledPhotoCleanup>();
}
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
if (authenticationEnabled)
{
    builder.Services.AddAuthentication(options =>
    {
        options.DefaultScheme = IdentityConstants.ApplicationScheme;
        options.DefaultSignInScheme = IdentityConstants.ExternalScheme;
    }).AddIdentityCookies();
    builder.Services.ConfigureApplicationCookie(options =>
    {
        options.Cookie.Name = "__Host-varplats-auth";
        options.Cookie.HttpOnly = true;
        options.Cookie.IsEssential = true;
        options.Cookie.Path = "/";
        options.Cookie.SameSite = SameSiteMode.Lax;
        options.Cookie.SecurePolicy = CookieSecurePolicy.Always;
        options.SlidingExpiration = true;
        options.LoginPath = "/login";
        options.Events.OnSigningIn = async context =>
        {
            var principal = context.Principal;
            if (principal is null || principal.HasClaim("recovery_only", "true"))
                return;
            if (!Guid.TryParse(principal.FindFirstValue(ClaimTypes.NameIdentifier), out var userId))
                throw new InvalidOperationException("The authenticated account has no valid identifier.");
            var activityStore = context.HttpContext.RequestServices
                .GetRequiredService<IdentityAccountActivityStore>();
            await activityStore.RecordSignInAsync(
                userId, DateTimeOffset.UtcNow, context.HttpContext.RequestAborted);
        };
    });
    builder.Services.Configure<CookieAuthenticationOptions>(IdentityConstants.ExternalScheme, options =>
    {
        options.Cookie.Name = "__Host-varplats-external";
        options.Cookie.HttpOnly = true;
        options.Cookie.IsEssential = true;
        options.Cookie.Path = "/";
        options.Cookie.SameSite = SameSiteMode.Lax;
        options.Cookie.SecurePolicy = CookieSecurePolicy.Always;
        options.ExpireTimeSpan = TimeSpan.FromMinutes(10);
    });
    builder.Services.AddIdentityCore<IdentityUser<Guid>>(options =>
    {
        options.SignIn.RequireConfirmedEmail = true;
        options.User.RequireUniqueEmail = true;
        options.Stores.SchemaVersion = IdentitySchemaVersions.Version3;
        options.Lockout.AllowedForNewUsers = true;
    })
    .AddRoles<IdentityRole<Guid>>()
    .AddEntityFrameworkStores<ReportDbContext>()
    .AddSignInManager()
    .AddDefaultTokenProviders();
    builder.Services.AddScoped<IReportAccountAccess, IdentityReportAccountAccess>();
    builder.Services.Configure<DataProtectionTokenProviderOptions>(options =>
        options.TokenLifespan = TimeSpan.FromHours(1));
    builder.Services.Configure<IdentityPasskeyOptions>(options =>
    {
        options.ServerDomain = passkeyServerDomain!;
        options.ResidentKeyRequirement = "required";
        options.UserVerificationRequirement = "preferred";
        options.ValidateOrigin = context => ValueTask.FromResult(
            !context.CrossOrigin
            && passkeyOrigins.Contains(context.Origin, StringComparer.OrdinalIgnoreCase));
    });
    builder.Services.AddOptions<MailjetOptions>().BindConfiguration("Authentication:Mailjet")
        .Validate(options => options.SenderEmail.Contains('@', StringComparison.Ordinal)
            && options.SenderEmail.Length <= 254
            && !string.IsNullOrWhiteSpace(options.ApiKey)
            && !string.IsNullOrWhiteSpace(options.ApiSecret),
            "Mailjet credentials and a sender email are required.")
        .ValidateOnStart();
    builder.Services.AddHttpClient<MailjetEmailSender>(client => client.Timeout = TimeSpan.FromSeconds(10));
    builder.Services.AddScoped<IdentityAccountActivityStore>();
    if (!builder.Environment.IsDevelopment())
        builder.Services.AddHostedService<IdentityAccountRetentionService>();
}
else
{
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
}
builder.Services.AddAuthorization();
builder.Services.AddRateLimiter(options =>
{
    options.AddPolicy("account-email", context =>
        RateLimitPartition.GetFixedWindowLimiter(
            context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = 5,
                Window = TimeSpan.FromMinutes(15),
                QueueLimit = 0,
                AutoReplenishment = true
            }));
});
builder.Services.AddCascadingAuthenticationState();
if (authenticationEnabled)
{
    builder.Services.AddAuthentication()
        .AddGoogle(options =>
        {
            options.SignInScheme = IdentityConstants.ExternalScheme;
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
                identity.AddClaim(new(ClaimTypes.Email, emailElement.GetString()!));
                identity.AddClaim(new("email_verified", "true"));
                return Task.CompletedTask;
            };
        })
        .AddOAuth("GitHub", options =>
        {
            options.SignInScheme = IdentityConstants.ExternalScheme;
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
                identity.AddClaim(new(ClaimTypes.Email, verifiedEmail));
                identity.AddClaim(new("email_verified", "true"));
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
    builder.Services.AddScoped<IReportWorkflowStore, DatabaseReportWorkflowStore>();
    builder.Services.AddScoped<IReportActionStore, DatabaseReportActionStore>();
    builder.Services.AddHealthChecks().AddDbContextCheck<ReportDbContext>();
}
builder.Services.AddRazorComponents().AddInteractiveServerComponents();
builder.Services.AddRadzenComponents();
builder.Services.AddProblemDetails();
builder.Services.AddExceptionHandler<PublicExceptionHandler>();
var app = builder.Build();
if (photoOptions.Enabled)
    _ = app.Services.GetRequiredService<IPhotoFileStorage>();
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
    if (photoOptions.Enabled
        && (context.Request.Path.StartsWithSegments("/mina-rapporter")
            || context.Request.Path.StartsWithSegments("/moderering")
            || context.Request.Path.StartsWithSegments("/rapporter")))
    {
        context.Response.Headers.CacheControl = "private, no-store, max-age=0";
        context.Response.Headers.Pragma = "no-cache";
    }
    await next();
});
app.UseAuthentication();
app.UseAuthorization();
app.UseRateLimiter();
app.UseAntiforgery();
app.MapStaticAssets();
app.MapDefaultEndpoints();
app.MapGet("/api/reports", async (IReportStore store, CancellationToken cancellationToken) =>
    Results.Ok(await store.ListAsync(cancellationToken)));
if (photoOptions.Enabled)
    app.MapPhotoEndpoints();
if (authenticationEnabled)
{
    app.MapPost("/auth/signin/google", async (
        HttpContext context, IAntiforgery antiforgery, SignInManager<IdentityUser<Guid>> signInManager) =>
    {
        await antiforgery.ValidateRequestAsync(context);
        var properties = signInManager.ConfigureExternalAuthenticationProperties(
            GoogleDefaults.AuthenticationScheme, "/auth/external/callback");
        return Results.Challenge(properties, [GoogleDefaults.AuthenticationScheme]);
    }).AllowAnonymous();
    app.MapPost("/auth/signin/github", async (
        HttpContext context, IAntiforgery antiforgery, SignInManager<IdentityUser<Guid>> signInManager) =>
    {
        await antiforgery.ValidateRequestAsync(context);
        var properties = signInManager.ConfigureExternalAuthenticationProperties("GitHub", "/auth/external/callback");
        return Results.Challenge(properties, ["GitHub"]);
    }).AllowAnonymous();
    app.MapPost("/auth/link/{provider}", async (
        HttpContext context, IAntiforgery antiforgery, SignInManager<IdentityUser<Guid>> signInManager,
        string provider) =>
    {
        await antiforgery.ValidateRequestAsync(context);
        if (context.User.HasClaim("recovery_only", "true")) return Results.Forbid();
        if (!provider.Equals("google", StringComparison.OrdinalIgnoreCase)
            && !provider.Equals("github", StringComparison.OrdinalIgnoreCase))
            return Results.BadRequest();
        var scheme = ExternalScheme(provider);
        var userId = signInManager.UserManager.GetUserId(context.User);
        if (userId is null) return Results.Unauthorized();
        await context.SignOutAsync(IdentityConstants.ExternalScheme);
        var properties = signInManager.ConfigureExternalAuthenticationProperties(
            scheme, "/auth/external/callback", userId);
        return Results.Challenge(properties, [scheme]);
    }).RequireAuthorization();
    app.MapGet("/auth/external/callback", async (
        HttpContext context, SignInManager<IdentityUser<Guid>> signInManager,
        UserManager<IdentityUser<Guid>> userManager, ILoggerFactory loggerFactory) =>
    {
        var existingUser = await userManager.GetUserAsync(context.User);
        var externalInfo = await signInManager.GetExternalLoginInfoAsync(existingUser?.Id.ToString());
        if (externalInfo is null)
            return Results.LocalRedirect("/login?error=external");

        if (existingUser is not null)
        {
            if (context.User.HasClaim("recovery_only", "true"))
                return Results.LocalRedirect("/account/connections?error=link");
            var linkResult = await userManager.AddLoginAsync(existingUser, externalInfo);
            if (!linkResult.Succeeded)
                return Results.LocalRedirect("/account/connections?error=link");
            if (externalInfo.LoginProvider == "GitHub" && externalInfo.ProviderKey == adminGitHubId
                && !await userManager.IsInRoleAsync(existingUser, "Admin"))
            {
                var adminRoleResult = await userManager.AddToRoleAsync(existingUser, "Admin");
                if (!adminRoleResult.Succeeded)
                {
                    var unlinkResult = await userManager.RemoveLoginAsync(
                        existingUser, externalInfo.LoginProvider, externalInfo.ProviderKey);
                    loggerFactory.CreateLogger("ExternalLogin").LogError(
                        "Could not assign the configured administrator role to a linked GitHub account. Link rollback succeeded: {RollbackSucceeded}.",
                        unlinkResult.Succeeded);
                    return Results.Problem("Account setup could not be completed.");
                }
            }
            return Results.LocalRedirect("/account/connections?linked=1");
        }

        var loginResult = await signInManager.ExternalLoginSignInAsync(
            externalInfo.LoginProvider, externalInfo.ProviderKey, isPersistent: false, bypassTwoFactor: true);
        if (loginResult.Succeeded)
            return Results.LocalRedirect("/");
        if (loginResult.IsLockedOut)
            return Results.LocalRedirect("/login?error=locked");

        var email = externalInfo.Principal.FindFirstValue(ClaimTypes.Email);
        if (externalInfo.Principal.FindFirstValue("email_verified") != "true" || string.IsNullOrWhiteSpace(email))
            return Results.LocalRedirect("/login?error=email");
        if (await userManager.FindByEmailAsync(email) is not null)
            return Results.LocalRedirect("/login?link=required");

        var user = new IdentityUser<Guid> { UserName = email, Email = email, EmailConfirmed = true };
        var createResult = await userManager.CreateAsync(user);
        if (!createResult.Succeeded)
            return Results.LocalRedirect("/login?error=account");
        var addLoginResult = await userManager.AddLoginAsync(user, externalInfo);
        if (!addLoginResult.Succeeded)
        {
            await userManager.DeleteAsync(user);
            return Results.LocalRedirect("/login?error=link");
        }

        var role = externalInfo.LoginProvider == "GitHub" && externalInfo.ProviderKey == adminGitHubId
            ? "Admin" : "Member";
        var roleResult = await userManager.AddToRoleAsync(user, role);
        if (!roleResult.Succeeded)
        {
            await userManager.DeleteAsync(user);
            loggerFactory.CreateLogger("ExternalLogin").LogError(
                "Could not assign the initial application role to a new external account.");
            return Results.Problem("Account setup could not be completed.");
        }

        await signInManager.SignInAsync(user, isPersistent: false);
        return Results.LocalRedirect("/");
    }).AllowAnonymous();
    app.MapPost("/auth/logout", async (HttpContext context, IAntiforgery antiforgery) =>
    {
        await antiforgery.ValidateRequestAsync(context);
        await context.SignOutAsync(IdentityConstants.ApplicationScheme);
        await context.SignOutAsync(IdentityConstants.ExternalScheme);
        return Results.LocalRedirect("/");
    }).RequireAuthorization();
    app.MapEmailAccountEndpoints(publicBaseUrl!);
    app.MapPasskeyEndpoints();
    app.MapAccountDeletionEndpoint();
}
app.MapRazorComponents<App>().AddInteractiveServerRenderMode();
app.Run();

static bool IsAllowedPasskeyOrigin(string origin, string serverDomain)
{
    if (!Uri.TryCreate(origin, UriKind.Absolute, out var uri)
        || uri.AbsolutePath != "/"
        || !string.IsNullOrEmpty(uri.UserInfo)
        || !string.IsNullOrEmpty(uri.Query)
        || !string.IsNullOrEmpty(uri.Fragment))
        return false;
    return uri.Scheme == Uri.UriSchemeHttps
        && string.Equals(uri.Host, serverDomain, StringComparison.OrdinalIgnoreCase);
}

static string ExternalScheme(string provider)
    => provider.ToLowerInvariant() switch
    {
        "google" => GoogleDefaults.AuthenticationScheme,
        "github" => "GitHub",
        _ => throw new BadHttpRequestException("Unknown external identity provider.")
    };

public partial class Program;

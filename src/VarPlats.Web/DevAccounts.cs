using System.Net;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using VarPlats.Web.Data;

namespace VarPlats.Web;

/// <summary>Ett syntetiskt testkonto för lokal utveckling. Inga riktiga personuppgifter.</summary>
public sealed record DevAccount(string Key, string Email, string Label, string Description, string? Role, bool Locked);

/// <summary>
/// Lokala testkonton (<c>Authentication:DevAccounts</c>). Endast Development, endast loopback-klienter
/// och endast en databas på den egna datorn. Ersätter OAuth, e-post och passkeys lokalt.
/// </summary>
public static class DevAccounts
{
    public const string ConfigurationKey = "Authentication:DevAccounts";

    public static readonly IReadOnlyList<DevAccount> All =
    [
        new("admin", "admin@varplats.test", "Administratör",
            "Granskar inskick, flaggor och arkiv. Rollen Admin.", "Admin", false),
        new("medlem-a", "medlem-a@varplats.test", "Medlem A",
            "Rapporterar och dokumenterar åtgärder. Rollen Member.", "Member", false),
        new("medlem-b", "medlem-b@varplats.test", "Medlem B",
            "Bekräftar andras åtgärder, ger hjärtan och poäng. Rollen Member.", "Member", false),
        new("medlem-c", "medlem-c@varplats.test", "Medlem C",
            "Tredje medlem för flaggor och fler bekräftelser. Rollen Member.", "Member", false),
        new("utan-roll", "utan-roll@varplats.test", "Konto utan roll",
            "Inloggad men utan roll: ska bara kunna läsa.", null, false),
        new("sparrad", "sparrad@varplats.test", "Spärrat konto",
            "Medlem med aktiv spärr: inloggningen ska nekas.", "Member", true)
    ];

    public static void ValidateDatabase(string? connectionString)
    {
        var host = new NpgsqlConnectionStringBuilder(connectionString).Host;
        var loopback = host is not null && (host.Equals("localhost", StringComparison.OrdinalIgnoreCase)
            || (IPAddress.TryParse(host, out var address) && IPAddress.IsLoopback(address)));
        if (!loopback)
            throw new InvalidOperationException(
                "Authentication:DevAccounts kräver en databas på den egna datorn (localhost).");
    }

    /// <summary>
    /// Databaskommentaren som märker en engångsdatabas för lokal utveckling. Sätts av
    /// tools/local-accounts.ps1 (eller för hand); utan den rör läget varken schema eller konton.
    /// </summary>
    public const string LocalDatabaseMarker = "varplats-lokal-utveckling";

    private static readonly string[] ForwardingHeaders =
        ["Forwarded", "X-Forwarded-For", "X-Forwarded-Host", "X-Forwarded-Proto", "X-Real-IP"];

    /// <summary>
    /// Sant när förfrågan kommer direkt från den egna datorn: loopback i båda ändar och inga
    /// proxyhuvuden. En proxy på samma värd ansluter via loopback men lägger till sådana huvuden.
    /// </summary>
    public static bool IsDirectLocalRequest(HttpContext context)
        => context.Connection.RemoteIpAddress is { } remote && IPAddress.IsLoopback(remote)
            && (context.Connection.LocalIpAddress is null || IPAddress.IsLoopback(context.Connection.LocalIpAddress))
            && !ForwardingHeaders.Any(context.Request.Headers.ContainsKey);

    /// <summary>Kör EF-migreringarna mot den lokala databasen och skapar de syntetiska kontona.</summary>
    public static async Task PrepareAsync(IServiceProvider services, string connectionString,
        CancellationToken cancellationToken = default)
    {
        ValidateDatabase(connectionString);
        // Migreringarna är genererade utan appens Identity-inställningar (som anger 128 tecken för
        // nyckelkolumner). En kontext utan applikationstjänster bygger samma modell som migreringarna.
        var migrationOptions = new DbContextOptionsBuilder<ReportDbContext>()
            .UseNpgsql(connectionString, postgres => postgres.UseNetTopologySuite())
            .Options;
        await using (var db = new ReportDbContext(migrationOptions))
        {
            // Ett värdnamn räcker inte: en SSH-tunnel eller en lokal kopia av produktionen är också
            // localhost. Kräv att databasen uttryckligen är märkt som lokal engångsdatabas.
            var marker = await db.Database.SqlQuery<string?>(
                    $"SELECT shobj_description(oid, 'pg_database') AS \"Value\" FROM pg_database WHERE datname = current_database()")
                .SingleOrDefaultAsync(cancellationToken);
            if (marker != LocalDatabaseMarker)
                throw new InvalidOperationException(
                    "Authentication:DevAccounts rör bara en databas märkt som lokal utvecklingsdatabas. "
                    + "Använd tools/local-accounts.ps1, eller kör COMMENT ON DATABASE <namn> IS '"
                    + LocalDatabaseMarker + "' mot en tom engångsdatabas.");
            await db.Database.MigrateAsync(cancellationToken);
        }

        await using var scope = services.CreateAsyncScope();

        var users = scope.ServiceProvider.GetRequiredService<UserManager<IdentityUser<Guid>>>();
        foreach (var account in All)
        {
            var user = await users.FindByEmailAsync(account.Email);
            if (user is null)
            {
                user = new IdentityUser<Guid> { UserName = account.Email, Email = account.Email, EmailConfirmed = true };
                Require(await users.CreateAsync(user), account);
            }
            if (account.Role is not null && !await users.IsInRoleAsync(user, account.Role))
                Require(await users.AddToRoleAsync(user, account.Role), account);
            if (account.Locked)
            {
                Require(await users.SetLockoutEnabledAsync(user, true), account);
                Require(await users.SetLockoutEndDateAsync(user, DateTimeOffset.MaxValue), account);
            }
        }
    }

    public static void MapDevAccountEndpoints(this WebApplication app)
    {
        app.MapPost("/dev/signin", async (HttpContext context, IAntiforgery antiforgery,
            SignInManager<IdentityUser<Guid>> signInManager) =>
        {
            try
            {
                await antiforgery.ValidateRequestAsync(context);
            }
            catch (AntiforgeryValidationException)
            {
                return Results.BadRequest();
            }
            var form = await context.Request.ReadFormAsync(context.RequestAborted);
            var account = All.FirstOrDefault(item => item.Key == form["account"].ToString());
            if (account is null)
                return Results.BadRequest();
            var user = await signInManager.UserManager.FindByEmailAsync(account.Email);
            if (user is null)
                return Results.LocalRedirect("/login?error=account");
            if (await signInManager.UserManager.IsLockedOutAsync(user))
                return Results.LocalRedirect("/login?error=locked");
            await context.SignOutAsync(IdentityConstants.ExternalScheme);
            await signInManager.SignInAsync(user, isPersistent: false);
            return Results.LocalRedirect("/");
        }).AllowAnonymous();
    }

    private static void Require(IdentityResult result, DevAccount account)
    {
        if (!result.Succeeded)
            throw new InvalidOperationException($"Testkontot {account.Key} kunde inte förberedas.");
    }
}

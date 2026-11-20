using System.Security.Claims;
using Microsoft.AspNetCore.Identity;

namespace VarPlats.Web;

/// <summary>
/// Ägarstyrda administratörer via verifierad e-post (<c>Authentication:AdminEmails</c>, kommaseparerat).
/// Ett konto vars bekräftade e-postadress står i listan får rollen Admin vid inloggning, oavsett
/// inloggningsmetod. Rollen tas inte bort automatiskt om adressen senare tas bort ur listan.
/// </summary>
public sealed class AdminEmails
{
    public const string ConfigurationKey = "Authentication:AdminEmails";
    private readonly HashSet<string> addresses;

    private AdminEmails(HashSet<string> addresses) => this.addresses = addresses;

    public int Count => addresses.Count;

    /// <summary>Tolkar och validerar listan. Ogiltiga adresser stoppar uppstarten.</summary>
    public static AdminEmails Parse(string? value)
    {
        var addresses = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in (value ?? "").Split([',', ';'],
                     StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var at = item.IndexOf('@', StringComparison.Ordinal);
            if (item.Length > 254 || at <= 0 || at != item.LastIndexOf('@') || at == item.Length - 1
                || item.Any(char.IsWhiteSpace))
                throw new InvalidOperationException(
                    $"{ConfigurationKey} måste vara e-postadresser separerade med kommatecken.");
            addresses.Add(item);
        }
        return new AdminEmails(addresses);
    }

    /// <summary>Endast en bekräftad e-postadress räknas, aldrig en obekräftad registrering.</summary>
    public bool Matches(IdentityUser<Guid> user)
        => user.EmailConfirmed && !string.IsNullOrWhiteSpace(user.Email) && addresses.Contains(user.Email.Trim());

    /// <summary>
    /// Körs när inloggningskakan utfärdas. Ger kontot rollen Admin i databasen och i den kaka som
    /// skapas nu, så att behörigheten gäller direkt utan ny inloggning.
    /// </summary>
    public async Task PromoteAsync(ClaimsPrincipal principal, Guid userId, UserManager<IdentityUser<Guid>> users,
        ILogger logger)
    {
        if (Count == 0 || principal.Identity is not ClaimsIdentity identity)
            return;
        var user = await users.FindByIdAsync(userId.ToString());
        if (user is null || !Matches(user))
            return;
        if (!await users.IsInRoleAsync(user, "Admin"))
        {
            var result = await users.AddToRoleAsync(user, "Admin");
            if (!result.Succeeded)
            {
                logger.LogError("Adminrollen kunde inte ges till ett konto i Authentication:AdminEmails.");
                return;
            }
            logger.LogWarning("Ett konto i Authentication:AdminEmails fick rollen Admin.");
        }
        if (!principal.IsInRole("Admin"))
            identity.AddClaim(new Claim(identity.RoleClaimType, "Admin"));
    }
}

using System.Security.Claims;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

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
            if (!IsValidAddress(item))
                throw new InvalidOperationException(
                    $"{ConfigurationKey} måste vara e-postadresser separerade med kommatecken.");
            addresses.Add(item);
        }
        return new AdminEmails(addresses);
    }

    /// <summary>
    /// Strikt kontroll av en ren adress (ingen visningsdel): en lokal del och en domän med minst en
    /// punkt, utan tomma delar, inledande/avslutande punkter eller dubbla punkter. En felstavning ska
    /// stoppa uppstarten i stället för att tyst aldrig matcha något konto.
    /// </summary>
    public static bool IsValidAddress(string value)
    {
        if (value.Length > 254 || value.Any(c => char.IsWhiteSpace(c) || char.IsControl(c))
            || !System.Net.Mail.MailAddress.TryCreate(value, out var parsed)
            || !string.Equals(parsed.Address, value, StringComparison.Ordinal))
            return false;
        var at = value.IndexOf('@', StringComparison.Ordinal);
        if (at != value.LastIndexOf('@'))
            return false;
        var local = value[..at];
        var labels = value[(at + 1)..].Split('.');
        return local.Length is > 0 and <= 64 && !local.StartsWith('.') && !local.EndsWith('.')
            && !local.Contains("..", StringComparison.Ordinal)
            && labels.Length >= 2
            && labels.All(label => label.Length is > 0 and <= 63 && !label.StartsWith('-') && !label.EndsWith('-')
                && label.All(c => char.IsAsciiLetterOrDigit(c) || c == '-'));
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
            var added = false;
            try
            {
                added = (await users.AddToRoleAsync(user, "Admin")).Succeeded;
            }
            catch (DbUpdateException)
            {
                // En samtidig inloggning hann lägga in samma (UserId, RoleId); kontrolleras nedan.
            }
            // Idempotent: det som räknas är om rollen finns i databasen nu, inte vem som lade in den.
            if (!added && !await users.IsInRoleAsync(user, "Admin"))
            {
                logger.LogError("Adminrollen kunde inte ges till ett konto i Authentication:AdminEmails.");
                return;
            }
            if (added)
                logger.LogWarning("Ett konto i Authentication:AdminEmails fick rollen Admin.");
        }
        if (!principal.IsInRole("Admin"))
            identity.AddClaim(new Claim(identity.RoleClaimType, "Admin"));
    }
}

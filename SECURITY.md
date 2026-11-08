# Säkerhet

Detta är en **förhandsgrund**, inte en säkerhetsgranskad, fullständigt lanseringsklar tjänst.
Använd aldrig verkliga personuppgifter i demo eller test. Den ägargodkända
`pi-preview`-releasen tar emot kontouppgifter för inloggning; rapportering och
rapportförfattarlänkning är fortfarande avstängda.

## Rapportera privat

Använd repositoryts **Security → Report a vulnerability** om privat rapportering
är aktiverad. Om knappen saknas: kontakta ägaren via en redan etablerad privat
kanal och be om säker rapportväg; skapa högst en issue utan tekniska detaljer
för att efterfråga den. Publicera inte exploateringssteg, personuppgifter eller
hemligheter i publika issues. En permanent säkerhetskontakt måste utses före
lansering. Ingen utlovad svarstid eller bug bounty finns.

## Skydd i grunden

- Demo/lokala skrivningar stoppas i produktion. Publika skrivningar är avstängda
  som standard och vägras om autentisering inte är aktiverad.
- Publik rapportering kontrollerar medlems-/adminroll; borttagning kräver admin.
  Ingen publik rapport-POST-route finns, och databasen kräver separat manuell grant.
- Adminborttagning raderar rapporten permanent. Återställning och granskningshistorik
  saknas ännu, så funktionen är inte redo att öppnas för riktiga användare.
- Autentisering, e-postregistrering och passkeys är avstängda som standard
  utanför den ägargodkända `pi-preview`-releasen.
  Aktivering lagrar verifierad e-post, externa inloggnings-ID:n och passkey-data.
  Konton länkas bara uttryckligen under en inloggad session; e-postmatchning länkar
  aldrig automatiskt. E-poståterställning ger en engångslänk och en kortlivad,
  begränsad session som inte får rapportera, moderera eller länka leverantörer
  innan en ny passkey har lagts till.
- Konton lagras endast för inloggning, återställning och säkerhet. Senaste
  inloggningstid registreras; inaktiva konton raderas efter 12 månader och
  användare kan själva radera kontot.
- Den ägargodkända taggade releasen kör `deploy/identity-schema.sql` och den
  separata begränsade granten `deploy/pi-identity-role.sql` på Vår plats-databasen.
  Rapporttabellen och dess grants ändras inte; `deploy/pi-public-write-role.sql`
  körs inte.
- Lokal skrivning kräver Development och loopback. Aldrig en publik dev-proxy.
- Gemensam servervalidering, EF-parametrisering, textkodade Leaflet-popupfält
  och Blazors normala HTML-kodning. Inget användarinnehåll går till `MarkupString`.
- Antiforgery, host-begränsning, HSTS i produktion, icke-root webbcontainer.
- Inga uppladdningar, lösenordskonton, betalningar eller verkliga poäng finns.
- Kartattribution och referer bevaras; ingen tile-prefetch/offline-cache.
- PR-workflows får read-only-token, inga repository secrets och inget
  `pull_request_target` som exekverar extern kod.
- Release har skrivbehörighet endast för betrodd versions-tagg, inte PR-kod.

## Hotmodell och lanseringsgrind

Rapporter kan avslöja hemadress, rörelsemönster, ansikten och tredje person.
Plats är inte automatiskt ofarlig bara för att den visas på en karta.
Även med de nuvarande rollkontrollerna får publika skrivningar inte aktiveras
förrän integritets-/retentionsbeslut, rate limits, lagringskvoter, spam-/Sybil-
skydd, granskningskö, granskningshistorik och en återställningsplan är klara.
Innehåll måste kunna döljas/raderas snabbt. Ingen rapportörsidentitet sparas i
rapporttabellen.

Bildpipeline måste isolera filer, begränsa filstorlek/antal/dimensioner, avkoda
verklig filtyp, skanna och återkoda, ta bort EXIF/GPS, bedöma personuppgifter
och lagra utanför körbar webroot. Ingen publik uppladdning innan detta finns.

Poäng behöver självbekräftelseförbud, oberoende bekräftelse, historik, spärrar,
bedrägeribedömning och återkallelse. Organisationers områden och dataåtkomst
behöver separat behörighetsmodell. Betalning tillkommer först efter avtal/analys.

Driften behöver TLS, kända proxies, SELECT-only runtime-roll när identitet är av,
begränsad Identity-grant när autentisering uttryckligen aktiveras, krypterade backups,
hemlighetshantering, patchpolicy, lastprov, incidentplan och juridiska beslut.
Exceptionloggar kan innehålla infrastrukturdataläckor: kontrollera sink, åtkomst
och retention; aktivera aldrig EF sensitive-data logging i drift.

## Underhåll

Endast senaste testade repositoryrevision är utvecklingsmål; ingen stabil
supportgren finns. .NET 11 RC ska ersättas med stabil version när tillgänglig
och verifierad. Granska Dependabot-uppdateringar, NuGet-audit, CodeQL och
containerbyggen före versionstagg. Maskinanalys ersätter inte mänsklig granskning.

# Säkerhet

Detta är en **förhandsgrund**, inte en säkerhetsgranskad, fullständigt lanseringsklar tjänst.
Använd aldrig verkliga personuppgifter i demo eller test. Den ägargodkända
`pi-preview`-releasen tar emot kontouppgifter för inloggning.

## Ägarbeslut 2026-11-20: alla brytare i Pi-releasen

Ägaren beslutade 2026-11-20 att release-workflowen ska slå på alla brytare som
finns på `main`: `Reporting:WorkflowEnabled`, `Reporting:PublicWrites`,
`Reporting:FlagsEnabled`, `Reporting:ArchiveEnabled`,
`Reporting:RateLimits:Enabled` och `Photos:Enabled`. Det är det ägarbeslut som
punkterna nedan kräver före aktivering. Standardvärdena i koden, `compose.yml`
och `.env.example` är fortfarande `false`, och startkontrollerna i `Program.cs`
är oförändrade. Migreringar och grants tillämpas inte automatiskt; ägaren kör dem
manuellt efter backup, och `deploy-release.sh` vägrar byta webb om de saknas
(se [docs/operations.md](docs/operations.md#aktivera-alla-brytare-på-pi-ägarbeslut-2026-11-20)).
Där en punkt nedan säger att en funktion är avstängd eller inte tillämpad på Pi
gäller det tiden före detta beslut och fortfarande standardkonfigurationen.

Beslutet gör **inte** lanseringsgrinden nedan uppfylld. Öppna punkter som koden
inte löser:

- [ ] integritets- och retentionsbeslut för inskick, åtgärder, bilder, flaggor och arkiv;
- [ ] uppdaterade och godkända villkor och integritetsinformation för dessa flöden;
- [ ] bemannad granskningskö, även för direktpublicerade åtgärder och anmälningar;
- [ ] provad backup och återställning av databas och bildvolym tillsammans;
- [ ] provad clamd på Pi:n (ARM64): start, signaturer, minne och svar;
- [ ] lastprov på Pi:n med alla brytare på.

Adminborttagning är fortfarande permanent utan granskningshistorik, spam-/Sybil-
skydd över många konton saknas och API-gränsen blir en gemensam gräns bakom Caddy.

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
- Skyddade rapport- och åtgärdsoperationer samt åtkomst till opublicerade
  rapportbilder kontrollerar aktuell Identity-roll, säkerhetsstämpel och
  kontostatus i en ny databasscope. En gammal Blazor-session räcker inte efter
  rolländring, spärr, kontoborttagning eller återkallad säkerhetsstämpel.
- Kod för manuell förhandsgranskning är avstängd och dess migration
  `20261108154722_ReportReviewWorkflow` är inte tillämpad i förhandsversionen.
  Den lägger väntande/avvisade inskick i en separat tabell och kopplar dem till
  kontot; godkända rapporter kopieras till den befintliga publika rapporttabellen.
  Kontoborttagning tar bort kontolänken med `ON DELETE SET NULL`, även vid
  inaktivitetsrensning, men rensar inte personuppgifter som skrivits i fritext.
  Den publika läsningen behöver inte det nya schemat och visar inte väntande
  eller avvisade inskick även om granskningen senare stängs av. Nedmigrering
  skulle radera granskningsdata och kontolänkar; den kräver återställningsplan.
- Den separata migrationen `20261108162158_ReportActionsAndConfirmations` är
  inte tillämpad i förhandsversionen och körs inte automatiskt vid release.
  Om det autentiserade arbetsflödet aktiveras publiceras åtgärdstext direkt,
  utan förhandsgranskning; admin kan radera den och dess bekräftelser.
  Endast ett annat medlems-/administratörskonto kan bekräfta, högst en gång per
  åtgärd och konto genom databasens sammansatta primärnyckel. Detta verifierar
  varken en fysisk person, att platsen är åtgärdad eller att arbetet var säkert.
  Kontoborttagning sätter åtgärdens kontolänk till null och kaskadraderar
  kontots bekräftelser; fritexten behålls och kan fortfarande innehålla
  personuppgifter. Status härleds från aktuella rader, inte från ett sparat
  bekräftelsevärde. När arbetsflödet är avstängt läses inga åtgärdstabeller
  och den tidigare lagrade rapportstatusen visas. Nedmigrering raderar åtgärdsdata.
- Bildfunktionen och migrationen `20261108164953_ReportPhotos` är avstängda och
  inte tillämpade på Pi. En bild kan endast följa ett nytt rapportinskick eller
  en ny åtgärd. Rapportbilder är publika först när rapporten godkänts och
  fortfarande finns; ägaren och behörig admin kan se opublicerade bilder.
  Åtgärdsbilder följer den direkt publicerade texten och kräver både befintlig
  åtgärd och rapport. En databastrigger hindrar att en bild läggs till på ett
  redan granskat inskick och att bildmetadata ändras efter insättning.
  Godkännande av ett bildinsändande stoppas om bilden är otillgänglig eller
  bildfunktionen har stängts av efter inskicket; detta ersätter inte en
  administratörs faktiska granskning av bildens innehåll.
  Bildernas metadata har ingen kontonyckel. Kontoborttagning tar bort förälderns
  kontolänk men behåller bild och text; personuppgifter som syns i en bild eller
  finns i fritext rensas inte av detta.
- Anmälan av innehåll och migrationen `20261119061342_ContentFlags` är
  avstängda (`Reporting:FlagsEnabled=false`) och inte tillämpade på Pi.
  Inställningen kräver `Reporting:WorkflowEnabled`; att anmäla kräver dessutom
  publika skrivningar och ett verifierat medlems- eller administratörskonto.
  Bara publicerade rapporter och åtgärdstexter kan anmälas, inte väntande
  inskick. Ett unikt index (`NULLS NOT DISTINCT`) tillåter en anmälan per konto
  och objekt, och varje konto kan göra högst 20 anmälningar per dygn
  (serialiserat med ett transaktionslås per konto). Administratörer ser antal,
  anledningar och kommentarer men inte vilket konto som anmält. Avfärdade
  anmälningar behålls så att samma konto inte kan anmäla samma objekt igen.
  Borttagning av rapport eller åtgärd och kontoborttagning kaskadraderar
  anmälningarna. Kommentaren är fritext och kan innehålla personuppgifter
  trots varningen. Detta ersätter inte bemannad moderering, överklaganden
  eller skydd mot samordnade falska anmälningar.
- Arkivering och migrationen `20261119222822_ReportArchive` är avstängda
  (`Reporting:ArchiveEnabled=false`) och inte tillämpade på Pi. Inställningen
  kräver `Reporting:WorkflowEnabled`; att arkivera eller återställa kräver
  dessutom publika skrivningar och ett verifierat administratörskonto.
  Arkivstatus ligger i egna tabeller, så rapporttabellen och dess grants ändras
  inte. Arkiverade rapporter filtreras bort från karta, lista, läs-API och
  närliggande tips men rapportsidan kan fortfarande läsas, och nya åtgärder
  nekas i databasfrågan. Historiken sparar händelse, anledning, kommentar och
  tid men inte vilket administratörskonto som agerade. Kommentaren är publik
  fritext. När inställningen är av läses inga arkivtabeller.
- Skrivgränser (`Reporting:RateLimits:Enabled=false` som standard) kräver
  ingen migration. När de är på räknar en minnesbaserad begränsare per konto-id
  och kommando i ett fast tidsfönster: rapportinskick, åtgärdsuppgifter,
  bekräftelser, anmälningar och bilduppladdningar (en bild räknas både som bild
  och som rapport eller åtgärd). Kontrollen görs i kommandolagret som
  Blazor-komponenterna anropar direkt, efter behörighets- och
  valideringskontroll men före lagring och bildbehandling, så HTTP-middleware
  behövs inte för skrivningarna. Vid nådd gräns sparas ingenting och
  användaren får ett svenskt textmeddelande, inte bara en färg. Loggen anger
  bara kommandotyp, aldrig konto-id, e-post eller innehåll. `GET /api/reports`
  begränsas per klientadress med ASP.NET Core:s middleware och svarar `429`.
  Bakom Caddy blir det en gemensam gräns, eftersom `X-Forwarded-For` medvetet
  ignoreras. Avvisade anrop från den befintliga e-postgränsen svarar nu också
  `429` i stället för `503`. Räknarna försvinner vid omstart och delas inte
  mellan instanser; de är ett skydd mot översvämning från ett konto, inte mot
  många konton. Anmälningarnas dygnsgräns i databasen gäller fortfarande.
- Bildkoden kontrollerar byteantal, verklig JPEG/PNG, dimensioner och pixlar,
  avkodar/ritar om utan inmetadata och kräver ett rent svar från en privat lokal
  clamd-socket både före och efter omkodning. Timeout, skannerfel, lagringsfel
  eller avvisad bild får inte falla tillbaka till textsparning. Lagringen använder
  slumpnamn, privata Linux-kataloghandtag utan symlinkföljning, exklusiv atomisk
  placering, filhash, antals-/bytegräns, ledigt-utrymme-gräns och begränsad
  samtidighet. Inläsningen av den inkommande bildströmmen har en tidsgräns.
  Vid borttagning spärras läsning via databasen direkt; misslyckad
  filstädning lämnar en kvoträknad rest som städas vid ett senare försök.
  Bildsvar är `no-store` och har `nosniff`. Lokala scannerfel avslår uppladdning,
  men skanning garanterar inte att en fil är ofarlig.
- Adminborttagning raderar rapporten permanent. Återställning och granskningshistorik
  saknas ännu, så funktionen är inte redo att öppnas för riktiga användare.
- Autentisering, e-postregistrering och passkeys är avstängda som standard
  utanför den ägargodkända `pi-preview`-releasen.
  Aktivering lagrar verifierad e-post, externa inloggnings-ID:n och passkey-data.
  Konton länkas bara uttryckligen under en inloggad session; e-postmatchning länkar
  aldrig automatiskt. E-poståterställning ger en engångslänk och en kortlivad,
  begränsad session som inte får rapportera, moderera eller länka leverantörer
  innan en ny passkey har lagts till.
- I förhandsversionen används konton för inloggning, återställning och säkerhet.
  Om åtgärdsflödet aktiveras lagras även kontolänkarna ovan. Senaste
  inloggningstid registreras; inaktiva konton raderas efter 12 månader och
  användare kan själva radera kontot.
- Den ägargodkända taggade releasen kör `deploy/identity-schema.sql` och den
  separata begränsade granten `deploy/pi-identity-role.sql` på Vår plats-databasen.
  Den kör aldrig `deploy/pi-workflow-migrations.sql`, `deploy/pi-public-write-role.sql`
  eller `deploy/pi-workflow-grants.sql`; de körs manuellt av ägaren. Releasen gör
  bara en skrivskyddad kontroll av att de finns. `pi-workflow-grants.sql` ger
  `varplats_reader` endast de rättigheter koden använder per tabell (ingen
  UPDATE/DELETE på bilder, bekräftelser eller arkivhändelser).
- Lokal skrivning kräver Development och loopback. Aldrig en publik dev-proxy.
- Gemensam servervalidering, EF-parametrisering, textkodade Leaflet-popupfält
  och Blazors normala HTML-kodning. Inget användarinnehåll går till `MarkupString`.
- Antiforgery, host-begränsning, HSTS i produktion, icke-root webbcontainer.
- Publik bilduppladdning är avstängd som standard men påslagen i Pi-releasen
  efter ägarbeslutet 2026-11-20. clamd körs då lokalt som UID 1654 utan
  nätverksport, med skrivskyddat rotfilsystem och en ARM64-image pinnad med
  digest. Lösenordskonton, betalningar och verkliga poäng finns inte.
- Kartattribution och referer bevaras; ingen tile-prefetch/offline-cache.
- PR-workflows får read-only-token, inga repository secrets och inget
  `pull_request_target` som exekverar extern kod.
- Release har skrivbehörighet endast för betrodd versions-tagg, inte PR-kod.

## Hotmodell och lanseringsgrind

Rapporter kan avslöja hemadress, rörelsemönster, ansikten och tredje person.
Plats är inte automatiskt ofarlig bara för att den visas på en karta.
Även med de nuvarande rollkontrollerna får publika skrivningar inte aktiveras
förrän integritets-/retentionsbeslut, aktiverade och avvägda rate limits, lagringskvoter, spam-/Sybil-
skydd, bemannad granskningskö, snabb hantering av direktpublicerade åtgärder,
granskningshistorik och en återställningsplan är klara.
Innehåll måste kunna döljas/raderas snabbt. Ingen rapportörsidentitet sparas i
den publika rapporttabellen; den inaktiva granskningskoden lagrar däremot
kontolänk i sin separata tabell. Före aktivering måste kontosida, villkor och
integritetsinformation uppdateras och godkännas för detta flöde.

Bildkoden är inte ett lanseringsgodkännande: den kan inte upptäcka människor,
ansikten eller personuppgifter i pixlar. Före aktivering krävs separat granskning
och prov med riktig clamd och dess uppdaterade signaturer, Linux/ARM64 och Pi:ns
resursgränser, dedikerad kvotbegränsad lagring och verifierad backup/återställning,
samt bemannad snabb borttagning även av direktpublicerade åtgärdsbilder.
Bildmoderering, driftsatt missbruksrapportering, rate limits, eventuell diskrensning efter
driftavbrott samt juridisk information och retentionsrutiner måste lösas och
godkännas. Ingen publik uppladdning innan alla lanseringskrav är uppfyllda.

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

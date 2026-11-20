# Vår plats

En mobilanpassad, kartcentrerad grund för gemensam omsorg om offentliga miljöer.
**Prototyp – inte lanseringsklar.** Projektets egen kod omfattas av MIT-licensen
i `LICENSE`; tredjepartskomponenter har separata villkor.

## Prova lokalt

Installera SDK **11.0.100**, exakt enligt `global.json` (.NET 11, stabil release).
EF Core-, Identity- och Npgsql-paketen är fortfarande RC 3; se docs/versions.md.
SDK:n finns för Windows x64 och Linux ARM64 i Microsofts release-metadata.

```powershell
dotnet restore --locked-mode
dotnet build -c Release --no-restore
dotnet test -c Release --no-build
dotnet run --project src/VarPlats.Web --launch-profile demo
```

Öppna <http://localhost:5180>. Kartan börjar i Stockholm; det är inte din position.
Rapportlistan innehåller två **syntetiska exempel**. Lägg till en rapport genom att
välja en plats på kartan eller ange koordinater. Lokal demo är anslutningsbunden
och försvinner när anslutningen avslutas; inget publiceras. Ingen Docker krävs.
Endast loopback-klienter tillåts. Exponera aldrig utvecklingsläget via publik proxy.

## Med databas och Aspire

Du behöver en körande Docker-kompatibel container-runtime och betrott lokalt
HTTPS-certifikat (`dotnet dev-certs https --trust`).

```powershell
dotnet run --project src/VarPlats.AppHost
```

AppHost startar **en webbprocess** och PostgreSQL/PostGIS, skapar utvecklingsdatabasen
`varplats` och håller anslutningshemligheten utanför källkoden. Dashboardens URL och
inloggning skrivs i terminalen; exponera den inte offentligt.
Init-SQL körs bara för en **ny** datavolym. Befintliga volymer uppgraderas inte
automatiskt. Rapportering sparar här i lokal utvecklingsdatabas.

## Alla funktioner lokalt med testkonton

För att prova granskning, åtgärder, bekräftelser, återtagande, poäng, hjärtan,
gåvor, anmälningar, arkivering och skrivgränser med olika roller finns
startprofilen `konton`. Den slår på alla brytare utom bilder och ersätter OAuth,
e-post och passkeys med **syntetiska testkonton** (`Authentication:DevAccounts`).
Kräver Docker men inget HTTPS-certifikat:

```powershell
./tools/local-accounts.ps1
```

Skriptet bygger PostGIS-imagen, startar containern `varplats-local-db` på
`127.0.0.1:55432` och kör webben på <http://localhost:5180>. Vid start körs
EF-migreringarna mot den lokala databasen och testkontona skapas. `-Reset`
börjar om med tom databas, `-DatabaseOnly` startar bara databasen. Lösenordet
till databasen slumpas när containern skapas och sparas inte i repot.

Webben migrerar och skapar konton **bara** i en databas som uttryckligen är märkt
som lokal engångsdatabas (`COMMENT ON DATABASE … IS 'varplats-lokal-utveckling'`);
skriptet sätter märkningen. Ett `localhost`-namn räcker inte, eftersom en SSH-tunnel
eller en kopia av produktionen också kan nås via `localhost`. Med Aspire
(`dotnet run --project src/VarPlats.AppHost -- --VarPlats:WebLaunchProfile=konton`)
märker du utvecklingsdatabasen en gång för hand innan profilen används.

Logga in via **Logga in** och välj konto:

| Konto | Roll | Används till |
|---|---|---|
| Administratör (`admin@varplats.test`) | Admin | Granskningskö, anmälningar, arkiv |
| Medlem A, B, C (`medlem-a@varplats.test` …) | Member | Rapporter, åtgärder, bekräftelser, poäng |
| Konto utan roll (`utan-roll@varplats.test`) | – | Inloggad men får bara läsa |
| Spärrat konto (`sparrad@varplats.test`) | Member, spärrad | Inloggningen nekas |

Testkonton är bara tillåtna i Development och mot en databas på `localhost`;
annars stoppar appen. Förfrågningar som inte kommer direkt från den egna datorn
(annan adress eller proxyhuvuden som `X-Forwarded-For`) nekas, så läget fungerar
inte bakom en omvänd proxy. Bilder kräver Linux, `procfs` och
clamd och kan inte provas med detta skript på Windows. Poäng finns först när ett
annat konto bekräftar en åtgärd, så gåvor provas enklast med två medlemmar.

## Vad ingår?

| Fungerar nu (publik rapportering är avstängd) | Inte implementerat ännu |
|---|---|
| Leaflet-karta, kategorifilter och tillgänglig rapportlista | Aktiverad rapportkö, överklaganden och missbruksskydd |
| Validering och rapportering i isolerad lokal demo | Aktiverad bildhantering och färdiga modereringsverktyg |
| Google/GitHub-inloggning med verifierad e-post, passkey/FIDO2 och uttrycklig kontolänkning; publik registrering aktiveras i den godkända Pi-releasen | Aktiverad publik åtgärdsrapportering och missbruksskydd |
| Kod för åtgärdsuppgifter och bekräftelser från andra konton, avstängd som standard men påslagen i Pi-releasen | Tvister och skydd mot samordnad manipulation |
| Kod för att ta tillbaka en egen bekräftelse med sparad historik, avstängd bakom `Reporting:ConfirmationWithdrawalEnabled` men påslagen i Pi-releasen | Överklagande av bekräftelser |
| Kod för att anmäla rapporter och åtgärdstexter till administratörer, avstängd bakom `Reporting:FlagsEnabled` | Överklaganden och skydd mot falska anmälningar |
| Kod för direktpublicering av rapporter utan förhandsgranskning, avstängd bakom `Reporting:DirectPublish` (inte påslagen i releasen) | Granskning i efterhand med svarstid |
| Kod som döljer anmält innehåll direkt i väntan på granskning, avstängd bakom `Reporting:HideOnFlag` (inte påslagen i releasen) | Överklagande av dolt innehåll och skydd mot många konton som anmäler tillsammans |
| Kod för att arkivera och återställa rapporter, avstängd bakom `Reporting:ArchiveEnabled` | Sammanslagning av dubbletter |
| Skrivgränser per konto och gräns för läs-API:t, avstängda bakom `Reporting:RateLimits:Enabled` | Delade gränser mellan flera instanser och skydd mot många konton (Sybil) |
| Kod för poäng vid bekräftade åtgärder, hjärtan och poänggåvor, avstängd bakom `Reporting:PointsEnabled` men påslagen i Pi-releasen | Bedrägeribedömning, spärrar och administratörsåterkallelse av poäng |
| EF Core/PostGIS-lagring i lokalt utvecklingsläge | Organisationsbevakning |
| Läs-API med kartvyfilter, livs-/beredskapskontroller, OTLP vid konfiguration | Betalning, native-app och offlinekartor |
| Installerbart webb/PWA-skal; kräver nätanslutning | |

Skrivgränser (`Reporting:RateLimits`) är avstängda som standard. När
`Enabled` är `true` begränsas varje konto per kommando och tidsfönster:
rapportinskick, åtgärdsuppgifter, bekräftelser, anmälningar och
bilduppladdningar (`PermitLimit` och `WindowSeconds` under `Reports`,
`Actions`, `Confirmations`, `Flags` och `Photos`). `Api` begränsar
`GET /api/reports` per klientadress och svarar `429` med `Retry-After`.
Standardvärdena står i `appsettings.json`; ogiltiga värden stoppar uppstarten
även när funktionen är av. Gränserna gäller i en process och nollställs vid
omstart. Bakom Caddy ser appen bara proxyns adress, eftersom `X-Forwarded-For`
inte litas på, så API-gränsen blir där i praktiken en gemensam gräns för alla
klienter.

Produktion är **endast läsning som standard** och kräver databas.
`Reporting:WorkflowEnabled` och `Reporting:PublicWrites` är båda avstängda som
standard; publika skrivningar kräver uttrycklig aktivering av båda,
autentisering och separat granskad databasbehörighet. Befintliga rapporter kan
läsas utan det nya schemat. Demo eller lokala skrivningar i Production stoppar uppstarten.

**Ägarbeslut 2026-11-20:** den taggade Pi-releasen slår på alla brytare som finns
på `main` – granskning, publika skrivningar, åtgärder och bekräftelser, återtagande
av bekräftelser (`Reporting:ConfirmationWithdrawalEnabled`), anmälningar,
arkivering, poäng, hjärtan och gåvor (`Reporting:PointsEnabled`), skrivgränser och bilder.
Poängen använder kodens standardvärden (10 per bekräftad åtgärd, högst 10 per
gåva och 30 per dygn); ägaren har inte beslutat egna belopp eller gränser. `deploy/compose.yml` och `.env.example`
behåller `false`, så en vanlig Compose-körning är fortfarande skrivskyddad.
Migreringarna tillämpas aldrig automatiskt: ägaren tar backup och kör
`deploy/pi-workflow-migrations.sql` och de begränsade grantfilerna manuellt, och
releasen avbryter innan webben byts om de saknas. Ordningen och rollback står i
[docs/operations.md](docs/operations.md#aktivera-alla-brytare-på-pi-ägarbeslut-2026-11-20).
Beslutet gör **inte** lanseringskraven klara: integritets- och retentionsbeslut,
villkor och integritetsinformation, bemannad granskningskö, prov av backup och
återställning, prov av clamd på ARM64 och lastprov på Pi:n är fortfarande öppna.
Organisationsbevakning blir inte officiell kommunal felanmälan.

**Ägarbeslut om modereringsmodellen (2026-11-20, kod finns men är avstängd):**
ägaren vill inte att varje rapport ska kräva godkännande, men anmält innehåll
ska försvinna direkt och gå till en moderator. Två egna brytare, båda `false`
som standard och **inte** påslagna i `release.yml` (det är ett separat ägarsteg):

- `Reporting:DirectPublish` kräver `Reporting:WorkflowEnabled` och
  `Reporting:PublicWrites`. Ett inloggat medlemskontos rapport publiceras i
  samma transaktion som inskicket, precis som ett godkännande; en bild går
  fortfarande genom hela bildkedjan med ClamAV och kontrolleras mot sin hash
  innan rapporten publiceras. `/moderering` fungerar som förut för det som
  fortfarande väntar, och administratörer kan ta bort rapporter. Det betyder
  att **ogranskat innehåll, även bilder, är publikt tills någon anmäler det**.
- `Reporting:HideOnFlag` kräver `Reporting:FlagsEnabled`. Så länge minst
  `HideOnFlag:MinimumFlags` (standard 1, tillåtet 1–50) öppna anmälningar från
  andra konton än upphovskontot finns döljs rapporten eller åtgärdstexten från
  kartan, listan, `GET /api/reports`, närliggande rapporter och delade
  `?rapport=`-länkar för alla, och på rapportsidan visas bara texten
  ”Dolt i väntan på granskning” för alla utom administratörer och
  upphovskontot. Döljandet räknas fram vid läsning ur `content_flags`; ingen
  ny tabell eller migration behövs. I granskningskön återställer moderatorn
  (anmälningarna avfärdas och innehållet visas igen) eller tar bort innehållet.
  Dolt innehåll tar inte emot nya åtgärder, bekräftelser, hjärtan eller gåvor
  men behåller sina poänghändelser; borttagning återför poängen som tidigare.

Risk: med standardvärdet 1 kan ett enda konto dölja innehåll. Skydden är att
upphovskontots egna anmälningar inte räknas, att samma konto bara kan anmäla
ett objekt en gång (även efter återställning), dygnsgränsen på 20 anmälningar
per konto och skrivgränsen för anmälningar. Många samordnade konton kan
fortfarande dölja mycket tills en moderator hinner återställa det; en bemannad
kö är därför ett krav innan brytarna slås på.

Om arbetsflödet senare aktiveras kan ett behörigt konto välja att publicera en
åtgärdsbeskrivning direkt på en publicerad rapport. Den är obekräftad tills ett
annat behörigt konto bekräftar den. Varken fysisk identitet, plats eller säkerhet
verifieras av tjänsten. Administratörer kan ta bort skadlig åtgärdstext.
Kontoborttagning anonymiserar sparade åtgärder och tar bort kontots bekräftelser,
men rensar inte personuppgifter som skrivits i fritext. Rapportstatus räknas om
från de åtgärder och bekräftelser som finns kvar. När arbetsflödet är avstängt
döljs åtgärderna och den tidigare lagrade rapportstatusen visas.

Om även `Reporting:ConfirmationWithdrawalEnabled` (av som standard) aktiveras kan
ett konto ta tillbaka sin egen bekräftelse med knappen "Ta tillbaka bekräftelse"
och ett bekräftelsesteg. Statusen räknas då om utan den. Att bekräftelsen tagits
tillbaka sparas i historiken och visas som ett antal på åtgärden, utan konto.
Samma konto kan inte bekräfta uppgiften igen, så att statusen inte kan växlas
fram och tillbaka. Arkiverade rapporter tar inte emot återtaganden. Migrationen
`ConfirmationWithdrawals` tillämpas inte automatiskt; i Pi-releasen ingår den i
`deploy/pi-workflow-migrations.sql` som ägaren kör manuellt.

### Bilder – avstängda som standard, påslagna i Pi-releasen

`Photos:Enabled` är `false` som standard och behöver då varken bildtabell,
skrivbar bildvolym eller skanner. Bildläsning kräver uttryckligt aktiverad
autentisering och rapportgranskning; uppladdning kräver dessutom de befintliga
publika skrivgrindarna och ett behörigt konto. Pi-releasen sätter
`PHOTOS_ENABLED=true` (ägarbeslut 2026-11-20) och startar då en lokal clamd
(Compose-profilen `photos`). Migrationen `ReportPhotos` och dess grants tillämpas
manuellt efter backup; clamd är ännu inte provad på Pi:n.

Med funktionen påslagen kan en bild följa med ett nytt inskick eller
en ny åtgärd. Rapportbilden följer granskningen; ägaren och administratören kan
se den före publicering. Godkännande med bild stoppas om bilden inte kan
kontrolleras eller bildfunktionen har stängts av. Åtgärdsbilden visas direkt med
åtgärdstexten. Kontoborttagning
tar bort kontolänken men sparar bild och innehåll. Bilder får inte innehålla
människor, ansikten eller personuppgifter; rensning av metadata tar inte bort
synliga personer eller uppgifter.

Bildkoden accepterar högst 5 MiB JPEG/PNG, kontrollerar verkliga bytes och
dimensioner, skannar både indata och omkodad bild via en privat lokal clamd-socket
och avvisar fel eller uteblivet svar. Slumpnamngivna filer lagras utanför webroot
i en särskilt konfigurerad privat Linux-katalog. Antal, diskutrymme, ledigt
utrymme, uppladdningstid och samtidighet begränsas; övergivna filer städas eller räknas mot kvoten.
Detta ersätter inte mänsklig innehållsgranskning, isolerad diskkvot, aktuella
virusdefinitioner, backup eller verifiering på målplattformen. Se SECURITY.md.

Databasintegrationstestet för granskning och åtgärder kräver en **tom, isolerad**
PostgreSQL/PostGIS-databas på loopback med namnet `varplats_workflow_test`.
Ange anslutningen i `VARPLATS_REPORT_TEST_DB` och kör
`dotnet test -c Release --no-build --filter FullyQualifiedName~ReportWorkflowDatabaseTests`.
Testet skapar tabeller och raderar testkonton; använd aldrig en riktig databas.
Med Docker kan `bash tests/report-workflow-db.test.sh` i stället skapa och ta bort
en separat testcontainer och köra hela den redan byggda testsviten. Samma test
körs på både x64 och ARM64 i CI.

## Teknik och drift

- [Arkitektur och roadmap](docs/architecture.md)
- [Raspberry Pi, backup och drift](docs/operations.md)
- [Verifierade versioner och källor](docs/versions.md)
- [GitHub-kontroller och reviewbegränsningar](docs/github.md)
- [Säkerhetsmodell och återstående lanseringskrav](SECURITY.md)
- [Bidra och utveckla](CONTRIBUTING.md)
- [Villkor](docs/legal/terms.md), [integritet](docs/legal/privacy.md)
  och [innehållsregler](docs/legal/content-draft.md). Villkor och
  integritetsinformation är operatörsgodkända men inte externt juridiskt granskade
  och är ännu inte uppdaterade för rapportering, bilder och anmälningar, som
  Pi-releasen slår på efter ägarbeslutet 2026-11-20.

Karttiles hämtas direkt från OpenStreetMap med synlig attribution och vanlig
webbläsarcache. Ingen serviceworker cachelagrar, förhämtar eller hämtar karttiles.
OSM:s community-tjänst har ingen SLA och är inte obegränsad gratis hosting.
`Map__TileUrl` och `Map__Attribution` kan ändras utan ombyggnad; granska alltid
leverantörens villkor, integritet och attribution innan byte.

## Öppna beslut

Licens- och källkodsinventering för containerdistribution, logg- och
backuprotation på värden, Pi-modell/RAM/64-bitars OS, modereringsansvar, säker
drift av bildhantering, poängregler (belopp och gränser; releasen använder
kodens standardvärden) och tile-kapacitet. Ingen betaltjänst har
aktiverats.

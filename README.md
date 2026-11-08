# Vår plats

En mobilanpassad, kartcentrerad grund för gemensam omsorg om offentliga miljöer.
**Prototyp – inte lanseringsklar.** Publikt repository betyder inte att koden har
en open source-licens; kodlicens är ett öppet beslut.

## Prova lokalt

Installera SDK **11.0.100-rc.3.26522.148**, exakt enligt `global.json`.
.NET 11 är fortfarande RC, inte stabil, vid verifieringen 2026-11-03.
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

## Vad ingår?

| Fungerar nu (publik rapportering är avstängd) | Inte implementerat ännu |
|---|---|
| Leaflet-karta, kategorifilter och tillgänglig rapportlista | Aktiverad rapportkö, överklaganden och missbruksskydd |
| Validering och rapportering i isolerad lokal demo | Aktiverad bildhantering och färdiga modereringsverktyg |
| Google/GitHub-inloggning med verifierad e-post, passkey/FIDO2 och uttrycklig kontolänkning; publik registrering aktiveras i den godkända Pi-releasen | Aktiverad publik åtgärdsrapportering och missbruksskydd |
| Kod för åtgärdsuppgifter och bekräftelser från andra konton, avstängd i förhandsversionen | Tvister, återtagande och skydd mot samordnad manipulation |
| EF Core/PostGIS-lagring i lokalt utvecklingsläge | Poäng och organisationsbevakning |
| Läs-API, livs-/beredskapskontroller, OTLP vid konfiguration | Betalning, native-app och offlinekartor |
| Installerbart webb/PWA-skal; kräver nätanslutning | |

Produktion är **endast läsning som standard** och kräver databas. Kod för
manuell rapportgranskning finns, men migrationsschemat är inte installerat i
förhandsversionen. `Reporting:WorkflowEnabled` och `Reporting:PublicWrites`
är båda avstängda som standard; publika skrivningar kräver uttrycklig aktivering
av båda, autentisering och separat granskad databasbehörighet. Befintliga
rapporter kan läsas utan det nya schemat. Den separata migrationen för åtgärder
och bekräftelser tillämpas inte på Pi eller automatiskt av releaseflödet.
Identitetsschemat och dess begränsade runtime-grant körs
endast i den taggade, ägargodkända release-workflowen; rapporttabellens grant
ändras inte. Innan rapportering kan aktiveras behöver bland annat kontosidan
och integritetsinformationen uppdateras, och retentionsbeslut och övriga
lanseringskrav uppdateras och granskas. Demo eller lokala skrivningar i Production stoppar uppstarten.
Organisationsbevakning blir inte officiell kommunal felanmälan.

Om arbetsflödet senare aktiveras kan ett behörigt konto välja att publicera en
åtgärdsbeskrivning direkt på en publicerad rapport. Den är obekräftad tills ett
annat behörigt konto bekräftar den. Varken fysisk identitet, plats eller säkerhet
verifieras av tjänsten. Administratörer kan ta bort skadlig åtgärdstext.
Kontoborttagning anonymiserar sparade åtgärder och tar bort kontots bekräftelser,
men rensar inte personuppgifter som skrivits i fritext. Rapportstatus räknas om
från de åtgärder och bekräftelser som finns kvar. När arbetsflödet är avstängt
döljs åtgärderna och den tidigare lagrade rapportstatusen visas.

### Bilder – kod finns, uppladdning är avstängd

`Photos:Enabled` är `false` som standard. Den gamla förhandsvisningen behöver
varken bildtabell, skrivbar bildvolym eller skanner. Bildläsning kräver uttryckligt
aktiverad autentisering och rapportgranskning; uppladdning kräver dessutom de
befintliga publika skrivgrindarna och ett behörigt konto. Ingen release, grant
eller Pi-installation ändras för bilderna. Migrationen `ReportPhotos` ska inte
tillämpas utan separat granskning, säkerhetskopiering och ägargodkännande.

Om funktionen någon gång godkänns kan en bild följa med ett nytt inskick eller
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
  integritetsinformation är operatörsgodkända men inte externt juridiskt granskade;
  innehållsregler gäller inte för publik rapportering, som är avstängd.

Karttiles hämtas direkt från OpenStreetMap med synlig attribution och vanlig
webbläsarcache. Ingen serviceworker cachelagrar, förhämtar eller hämtar karttiles.
OSM:s community-tjänst har ingen SLA och är inte obegränsad gratis hosting.
`Map__TileUrl` och `Map__Attribution` kan ändras utan ombyggnad; granska alltid
leverantörens villkor, integritet och attribution innan byte.

## Öppna beslut

Kodlicens, extern juridisk granskning, logg- och backuprotation på värden,
Pi-modell/RAM/64-bitars OS, modereringsansvar, säker drift av bildhantering, poängregler och
tile-kapacitet. Ingen betaltjänst har aktiverats.

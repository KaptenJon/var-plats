# Raspberry Pi och drift

**Inte ett godkännande för publik lansering.** Läs SECURITY.md och juridiska utkast
först. Ägaren beslutade 2026-11-20 att release-workflowen ska slå på alla
funktionsbrytare som finns på `main` (se
[Aktivera alla brytare på Pi](#aktivera-alla-brytare-på-pi-ägarbeslut-2026-11-20)).
Beslutet gör inte lanseringskraven klara; de öppna punkterna står i
[checklistan](#öppna-lanseringskrav-som-koden-inte-löser). Organisationsbevakning är inte öppnad.

## Villkorade hårdvarukrav

Planera för Raspberry Pi 4 eller 5 med **64-bitars ARMv8 CPU och 64-bitars
Linux (`aarch64`)**, modern Docker Engine + Compose v2. Raspberry Pi OS 64-bit
eller en av .NET-versionens stödda Debian/Ubuntu-versioner måste verifieras mot
den faktiska RC/stabila supportmatrisen. Ingen kompatibilitetsutfästelse för
Pi 1/Zero/32-bitars OS. Modell, RAM och OS är fortfarande okända.
För .NET 11 är Linux ARM64-minimum fortsatt armv8.0-a; ReadyToRun använder nu
LSE där tillgängligt och kan falla tillbaka till JIT på äldre Linux-CPU:er.

**4 GB RAM är en första rekommendation, inte ett uppmätt minimikrav.**
Föredra 8 GB för databasen och SSD framför SD-kort. Bygg images på CI/utvecklardator,
inte på en minnesbegränsad Pi. Säker strömförsörjning, kylning, OS-patchning och
extern backup behövs. Mät Blazor-kretsar, RAM, lagring och svarstider under last.
Aspire/dashboard ska inte driftsättas på Pi.

## Första lokala driftprovet

`deploy/compose.yml` är ARM64-inriktad och binder webbporten enbart till loopback.
Databasen har ingen publicerad port. `postgis/postgis:17-3.5` annonserade bara
amd64 vid kontrollen; därför byggs PostGIS från PostgreSQL + dess Debian-paket,
inte genom ett felaktigt ARM64-antagande. PostGIS-paketet måste ha samma
PostgreSQL-majorversion som basimagen.

`WEB_PORT` kan exempelvis sättas till `8010`. `WEB_BIND_ADDRESS` är som standard
`127.0.0.1`; för ett tillfälligt läsprov på ett betrott LAN kan den sättas till
Pi:ns LAN-adress. Lägg till `pi` och LAN-adressen i `PUBLIC_HOST`, separerade med
semikolon. Detta ger HTTP, inte TLS. Öppna inte porten mot internet innan kraven
för publik läsdrift nedan är uppfyllda. Ändra inte andra webbappar eller deras proxy.

Från reporoten på Pi, med POSIX-shell:

```sh
cp deploy/.env.example deploy/.env
mkdir -p deploy/secrets
chmod 700 deploy/secrets
umask 077
openssl rand -hex 32 > deploy/secrets/db_password
```

Sätt samma lokala lösenord i `DATABASE_CONNECTION` i den oincheckade
`deploy/.env`. Använd ett långt slumpvärde som inte behöver connection-string-
escaping, exempelvis hexvärdet ovan. Kör inte hemligheterna genom issues eller
terminalkommandon som publiceras. `.env` och `secrets/` ignoreras av Git.
`TILE_URL` måste också anges i `.env`, som i exemplet. URL-mallens klammerparenteser
ska inte ligga i Compose-interpolationens standardvärde: äldre Compose v2 kan
då skapa en felaktig tile-URL även när miljövariabeln är satt.

```sh
docker compose --env-file deploy/.env -f deploy/compose.yml config --quiet
docker compose --env-file deploy/.env -f deploy/compose.yml up -d --build
curl --fail http://localhost:8080/health/live
curl --fail http://localhost:8080/health/ready
```

Byt `WEB_IMAGE` och `DB_IMAGE` i `.env` till release-imagernas **digests** och använd
`up -d --no-build` när en granskad version har publicerats.
Databasen initieras endast vid en tom datakatalog. PostgreSQL 18 använder
`/var/lib/postgresql/18/docker` som standard för `PGDATA`; därför monteras
Compose-volymen på `/var/lib/postgresql`. PostgreSQL 17 använde
`/var/lib/postgresql/data`. Att byta image-majorversion uppgraderar inte klustret:
en gammal volym innehåller PostgreSQL 17-data under `data/`, medan PostgreSQL 18
kan initiera ett nytt kluster under `18/docker`. Gör en separat, granskad
majoruppgradering med verifierad backup och `pg_dump`/`pg_restore` eller en
validerad `pg_upgrade`-procedur till en ny datavolym. Verifiera rapportantal,
koordinater och PostGIS innan trafik flyttas. Behåll den gamla volymen tills
återställning och drift är verifierade; radera eller återanvänd den aldrig som
uppgraderingsmetod.

Anslut via lokal SSH-tunnel vid prov. Före publik läsning krävs domän,
`PUBLIC_HOST`, TLS-reverse-proxy, brandvägg och övervakning. Proxyn ligger utanför
den här grunden; tillåt inte godtyckliga forwarded headers. Stöd för proxybaserad
identitet/klient-IP måste konfigureras med kända proxies innan sådant används.
Blazor kräver WebSocket-proxying och långlivade anslutningar.

## Hemligheter och lägsta behörighet

Compose-secret håller DB-lösenord borta från imagen, men anslutningssträngen i
webbprocessens miljö kan läsas av Docker-administratörer. Begränsa värdåtkomst.
Byt till en granskad secret-store/file-konfiguration när driften kräver det.
Initialt är databasrollen schemaägare för enkel lokal uppstart; skapa en separat
runtime-roll med endast SELECT före publik läsdrift. Publik skrivning är en
separat opt-in; databasen ska försvara gränsen även om appkonfigurationen ändras.
Begränsa schemaägaren till migrationer.
För publika skrivningar finns `deploy/pi-public-write-role.sql`, som ger
runtime-rollen INSERT/DELETE på rapporttabellen, och `deploy/pi-workflow-grants.sql`,
som ger de minsta behörigheter varje ny tabell behöver. Båda körs manuellt enligt
ordningen nedan; release-workflowen kör dem aldrig, men `deploy-release.sh`
avbryter innan webben byts om de saknas.

Webb kör som icke-root, med read-only rotfilsystem, tmpfs, borttagna capabilities
och beständig Data Protection-volym. Kryptering av volym/backup och nycklar är
driftansvar. Kontrollera volymrättigheter innan första riktiga uppstart.

### Bildlagring och clamd

Standard `Photos:Enabled=false` (Compose och `.env.example`) kräver ingen
bildtabell, bildvolym eller skanner. Release-workflowen sätter `PHOTOS_ENABLED=true`
(ägarbeslut 2026-11-20). Då gör `deploy-release.sh`, före bytet av webbcontainer:

1. ger volymerna `varplats_photos` och `varplats_scanner-socket` ägare UID 1654 och
   läge `0700`, och signaturvolymen `varplats_clamav-signatures` ägare UID 1654;
2. startar Compose-tjänsten `clamav` (profil `photos`) och väntar högst 15 minuter
   på att den blir frisk. Första starten laddar ned signaturer.

`clamav` kör `clamav/clamav-debian:1.5` pinnad till dess `linux/arm64`-digest
(den vanliga `clamav/clamav`-imagen saknar ARM64). Den kör som UID 1654 med
skrivskyddat rotfilsystem, utan capabilities och utan nätverksport: clamd lyssnar
bara på `/run/varplats/scanner/clamd.sock` (`deploy/clamd.conf`, läge `0600`) och
freshclam uppdaterar signaturerna (`deploy/freshclam.conf`). Webben monterar
`/var/lib/varplats/photos` och socketkatalogen; `Photos:StoragePath` och
`Photos:ScannerSocketPath` sätts i `deploy/compose.yml`. `pi.override.yml`
begränsar clamd till 1,5 GiB RAM eftersom hela signaturdatabasen ligger i minnet;
det och tjänstens start på ARM64 är **inte provat på Pi:n** ännu.

Kraven nedan gäller fortfarande. För driftprovet krävs 64-bitars Linux med `procfs`, en
dedikerad lokal lagringskatalog **utanför webroot**, ägd av webbprocessens UID
och med rättighet `0700`. Inga katalogkomponenter får vara symlänkar eller
skrivbara för grupp/andra; lagringen får inte delas med andra appar eller vara
åtkomlig via en annan webroot-montering. Konfigurera absoluta
`Photos:StoragePath` och `Photos:ScannerSocketPath`. Socketens separata katalog
ska också ägas av webbprocessens UID och ha `0700`; clamd-socketen ska ägas av
samma UID och vara privat. Skannern är endast lokal och måste svara på
`zINSTREAM` med aktuella signaturer och tillåta minst 5 MiB per skanning.
Saknad socket, timeout och alla andra fel stoppar uppladdningen.

Standardgränserna är 1 000 filer, 512 MiB bilddata, minst 512 MiB ledigt
utrymme, en samtidig uppladdning per webbprocess och 30 sekunders inläsningstid;
de kan begränsas i `Photos`.
Använd dessutom en separat, hårt kvotbegränsad filvolym: appens kontroll av
ledigt utrymme kan inte skydda andra processer mot samtidiga diskskrivningar.
Flera webbprocesser delar databaslås för lagringskvoten men har varsin
minnes-/skanningsgräns och måste kapacitetsprovas. Bevaka fel och rester efter
avbruten filstädning, och testa återställning av databas och bildvolym
tillsammans. `no-store` begränsar cache, men redan mottagna kopior kan inte
återkallas. Testa verklig clamd, filsystem, inbyggda bildavkodare och resursbruk
på den avsedda ARM64-miljön före någon aktivering.

## GitHub-deployment till en separat Pi-installation

Webbimagen byggs på en GitHub-hostad ARM64-runner i det privata repositoryt
`KaptenJon/var-plats-deploy`, efter godkänd CI för den aktuella `main`-revisionen.
Databasimagen byggs inte om för denna release. Jobbet `deploy-pi` kör endast för
annoterade versionstaggar som verifierats tillhöra `main`, använder environment
`pi-preview` och runner-label `varplats-deploy`. Skapa environment med ägaren
som obligatorisk granskare och tillåt endast versionstaggar innan runnern
registreras. Kör aldrig CI, fork-PR eller checkout av PR-kod på Pi:n.
En self-hosted runner i ett publikt repo är trots detta en risk: workflowkod kan
ändras, och Docker-medlemskap ger i praktiken administratörsåtkomst till värden.
Ägaren måste godkänna risken; använd en separat deploymentvärd för stark isolering.

Releasejobbet läser `GHCR_PULL_TOKEN`, `GOOGLE_CLIENT_ID`, `GOOGLE_CLIENT_SECRET`,
`OAUTH_GITHUB_CLIENT_ID`, `OAUTH_GITHUB_CLIENT_SECRET`, `MAILJET_API_KEY`,
`MAILJET_API_SECRET` och `MAILJET_SENDER_EMAIL` som secrets från environment
`pi-preview`. GitHub-namnen för GitHub OAuth mappas till Compose-inställningarna
`GITHUB_CLIENT_ID` och `GITHUB_CLIENT_SECRET`. Autentiseringsvärdena skickas till
webbcontainern vid varje godkänd deploy och skrivs inte till Git eller deployens `.env`; Docker-
administratörer på Pi:n kan dock läsa containerns miljö. `GHCR_PULL_TOKEN` är en
separat klassisk GitHub-token med `read:packages` för att hämta den privata
webbimagen; den skickas inte till webbcontainern. Den kan läsa privata paket som
kontot har tillgång till och måste roteras före utgång. Begränsa därför runner-
och värdåtkomst. Den godkända release-workflowen aktiverar autentisering och,
efter ägarbeslutet 2026-11-20, `REPORTING_WORKFLOW_ENABLED`,
`REPORTING_PUBLIC_WRITES`, `REPORTING_FLAGS_ENABLED`, `REPORTING_ARCHIVE_ENABLED`,
`REPORTING_CONFIRMATION_WITHDRAWAL_ENABLED`, `REPORTING_POINTS_ENABLED` (med
`POINTS_*`-värdena), `RATE_LIMITS_ENABLED` och `PHOTOS_ENABLED`. Värdena skickas till containern vid
varje deploy och skrivs inte till `.env`; en manuell `docker compose up` utan dem
startar alltså webben skrivskyddad. Den läser Caddys exakta IPv4-gateway
från det befintliga Vår plats-webbcontainerns nätverk; den gissar inte proxy-IP.

Installationen under `/home/kapten/apps/varplats` innehåller `deploy/compose.yml`,
`deploy/pi.override.yml`, SQL-init, `deploy/pi-runtime-role.sql` och en skyddad
`deploy/.env`. Releasejobbet checkar endast ut en verifierad tagg på `main`
efter ARM64-bygge och `pi-preview`-ägargodkännande. Från den godkända taggen
uppdaterar det endast Vår plats-installationens Compose-filer och
`bin/deploy-release.sh`, applicerar de granskade Identity-SQL-filerna och
driftsätter webbcontainern. Det ändrar inte andra Compose-projekt, Caddy eller
värdtjänster och checkar inte ut PR-kod.
Runnern installeras separat under `/home/kapten/actions-runner-varplats` med
GitHubs officiella `svc.sh` som systemtjänst för `kapten`. Inga befintliga
systemtjänster, Docker-projekt, proxyregler eller brandväggsregler ska ändras.
Runnern kräver utgående HTTPS/443 till GitHub och GHCR, inte inkommande SSH.
Med bilder påslagna behöver Pi:n också utgående HTTPS till Docker Hub (clamd-imagen)
och `database.clamav.net` (signaturer).

För en **ny, tom installation** skapas ett slumpat schemaägarlösenord i
`deploy/secrets/db_password` och ett separat hexlösenord i
`deploy/secrets/runtime_password`. Katalogen ska vara `700`; `.env` och
runtime-lösenordet `600`. DB-secret måste vara läsbar av containerns postgres-
användare, exempelvis `444` innanför den skyddade katalogen. `.env` ska använda
`Username=varplats_reader` och runtime-lösenordet i `DATABASE_CONNECTION`.
SQL-initkatalogen måste vara läsbar i containern (`755`, SQL-filer `644`), även
om kopiering/extrahering skett med restriktiv umask. Installationsroten förblir
`700`, så andra värdanvändare får inte åtkomst genom dessa filrättigheter.
En ny installation kräver separat granskad provisionering. Den aktuella
release-workflowen kräver en befintlig databas och webbcontainer.

Taggens annotationsrad ska vara den privata webbavbildningens exakta GHCR-referens med
SHA-256-digest. Före ändringar på Pi:n hämtas imagen och dess
`org.opencontainers.image.revision` jämförs med den verifierade källrevisionen;
etiketten är inte en kryptografisk attestering. GHCR-token används enbart i en
tillfällig Docker-konfiguration, inte i användarens befintliga registry-login.
`pi.override.yml` anger gränser för CPU, RAM, processantal och loggstorlek;
minnesgränsen kräver stöd i kärnan. Senare deployment ändrar **endast
webbcontainern**; databasen förblir
pinnad. Den godkända autentiseringsreleasen kör den idempotenta
`deploy/identity-schema.sql` och den begränsade `deploy/pi-identity-role.sql`
som `varplats`-ägaren innan webbcontainern uppdateras. Detta påverkar bara
Identity/passkeytabeller och deras runtime-grant, inte rapporttabellen, dess
data eller rapportbehörigheter. Övriga schema- och databasimageändringar kräver
separat granskning och backup. Misslyckad readiness eller auth-smoketest rullar
tillbaka webbimagen utan att stoppa databasen eller andra appar. När autentisering
är aktiverad kontrolleras att inloggningsalternativen visas och att en auth-route
är mappad. Den tidigare webbimagen måste finnas lokalt före en uppdatering.
De gamla publika digestarna har tagits bort från GHCR och kan inte användas för
återställning om de lokala kopiorna försvinner; ingen verifierad backup finns.

För en ny förhandsrelease: kontrollera att CI för aktuell `main` har lyckats,
starta `Build private ARM64 image` i det privata repositoryt med hela
`main`-commitens SHA och kontrollera att bygget lyckats och GHCR-paketet är
privat. Kopiera digesten från byggjobbets sammanfattning och skapa en annoterad
versionstagg på samma commit, med exakt
`ghcr.io/kaptenjon/var-plats-deploy-web@sha256:<digest>` som taggmeddelande.
Pusha taggen och godkänn därefter `pi-preview` först när releasekontrollerna
har lyckats. Bygget använder privata GitHub Actions-minuter. Licens- och
källkodsgranskningen är fortfarande öppen; denna förhandsrelease gör inte
bilderna klara för offentlig distribution.

Verifiera `docker info` för stöd för minnes-cgroups: denna Pi rapporterade
`MemoryLimit=false` 2026-11-08 och ignorerar därmed `mem_limit`. Pi-override
begränsar även .NET:s GC-heap till 256 MiB och sänker
PostgreSQL:s anslutnings-/minnesinställningar, men detta är inte en hård gräns
för processernas totala RAM. Att aktivera saknade kernel-cgroups kan kräva
bootkonfiguration och omstart; gör inte detta utan en separat underhållsplan
för alla appar som delar värden.

## HTTPS via befintlig Caddy på egen port

På den delade Pi:n terminerar befintlig systemtjänst Caddy TLS. Webbcontainern
binds endast till `127.0.0.1:8080` (`WEB_BIND_ADDRESS=127.0.0.1`, `WEB_PORT=8080`)
och domänen läggs till i `PUBLIC_HOST`. Caddy får ett **nytt, separat** site-block
på port 8010; befintliga block ändras inte:

```caddyfile
larbornhome.asuscomm.com:8010 {
    reverse_proxy 127.0.0.1:8080
}
```

Samma värdnamn återanvänder Caddys befintliga certifikat. Validera med
`caddy validate` och ladda om med `systemctl reload caddy`, inte restart; en
ogiltig konfiguration avvisas och den gamla fortsätter köra. Routern behöver
vidarebefordra TCP 8010 till Pi:n. LAN-klienter som använder domännamnet kräver
NAT-loopback i routern; LAN-IP fungerar inte med TLS eftersom certifikatet gäller
domänen. När autentiseringsändringen är driftsatt litar appen på
`X-Forwarded-Proto` från loopback och, när auth aktiveras, en enda exakt
`Authentication:TrustedProxyIp` för Caddy. `X-Forwarded-For`,
`X-Forwarded-Host` och breda proxynät ignoreras. Webbcontainerns port ska fortsatt
vara loopback-only. Det gör att HTTPS-schemat används för HSTS och OAuth-callbacks
utan att godtyckliga klienter kan förfalska proxyheaders.

### Extern inloggning

Google- och GitHub-inloggning är avstängd som standard utanför den
ägargodkända `pi-preview`-releasen. Aktivering kräver OAuth-client ID och client
secret för båda leverantörerna, ägarens stabila numeriska
`Authentication:AdminGitHubId` och den exakta IP-adress som containern ser för
Caddy under `Authentication:TrustedProxyIp`. Läs gatewayadressen från just
webbcontainerns Docker-nät; gissa inte ett helt privat nätverk. Lägg
hemligheterna i värdens skyddade konfiguration/secret-store, aldrig i Git, issues
eller Actions-loggar. GitHub behöver `user:email`-scope för att verifiera en
primär e-postadress; inloggning avvisas om leverantören inte bekräftar e-posten.
Den verifierade e-postadressen sparas i kontodatabasen men inte i
autentiseringscookien.
Adminrollen ges på två sätt, båda styrda av ägaren:

- exakt det konfigurerade GitHub-ID:t (`Authentication:AdminGitHubId`);
- ett konto vars **bekräftade** e-postadress står i `Authentication:AdminEmails`
  (kommaseparerat, oavsett skiftläge). Rollen ges när kontot loggar in, med
  Google, GitHub, passkey eller verifierad e-post, och gäller direkt. Releasen läser listan från
  secret `ADMIN_EMAILS` (`gh secret set ADMIN_EMAILS`), så adresserna står aldrig i
  Git. Obekräftade registreringar räknas inte. Rollen tas inte bort automatiskt om
  en adress tas bort ur listan; ta i så fall bort raden i `AspNetUserRoles` för hand.

Ingen annan inloggning ger adminbehörighet, och "första kontot" blir aldrig admin
automatiskt. Google- och GitHub-konton länkas endast explicit från en
inloggad session; samma e-postadress länkar aldrig automatiskt. Callback-URL:erna är
`https://larbornhome.asuscomm.com:8010/signin-google` och
`https://larbornhome.asuscomm.com:8010/signin-github`.

Passkey/FIDO2 och e-postbekräftelse använder samma exakta HTTPS-origin som
`Authentication:PublicBaseUrl`, `Passkeys:ServerDomain` och
`Passkeys:Origins`. Mailjet API-nyckel, API-hemlighet och avsändaradress ska ligga
i värdens skyddade secret-store; de ska aldrig läggas i repository eller skickas
i chatt. Återställningslänkar är engångslänkar; sessionen begränsas till att lägga
till en ny passkey tills den är registrerad. Passkey-inloggningen kräver
upptäckbara (resident) inloggningsuppgifter så att e-postadress inte behöver
skickas och konton inte kan upptäckas genom begäran om WebAuthn-alternativ.
Säkerhetsnyckeln måste därför stödja resident credentials.

Identity-tabellerna innehåller e-postadress, leverantörs-ID:n och passkeydata.
Den godkända release-workflowen kör Identity-schemat och den Identity-begränsade
runtime-granten efter taggverifiering och environment-ägargodkännande. Schemat
lägger även till kontoskapande-/senaste-inloggningstidpunkt; konton tas bort när
de varit inaktiva i 12 månader, med kontroll varje dygn. Självradering finns på
sidan Inloggningsmetoder. `deploy/pi-public-write-role.sql` och
`deploy/pi-workflow-grants.sql` är separata spärrar som ägaren kör manuellt.

Operatörens lagringsregel är högst 12 månader för krypterade säkerhetskopior.
Backupmålet ligger utanför denna release-workflow: konfigurera och kontrollera
dess automatiska utgång separat. Vid begäran om radering ska uppgifter tas bort
från den aktiva databasen utan onödigt dröjsmål; användaren får svar inom en
månad, med högst två månaders förlängning för komplexa ärenden och besked inom
den första månaden. Säkerhetskopior kan innehålla raderade uppgifter tills
rotationen löper ut.

Alla brytare är `false` i Compose-exemplet och i `.env.example`. Release-workflowen
sätter dem till `true`; det kräver fungerande OAuth-konfiguration och de manuella
databasstegen nedan.

## Aktivera alla brytare på Pi (ägarbeslut 2026-11-20)

Ägaren beslutade 2026-11-20 att Pi-releasen ska slå på alla brytare som finns på
`main`: `Reporting:WorkflowEnabled`, `Reporting:PublicWrites`,
`Reporting:FlagsEnabled`, `Reporting:ArchiveEnabled`,
`Reporting:ConfirmationWithdrawalEnabled` (tillagd efter att #36 mergats),
`Reporting:PointsEnabled` (tillagd efter att #37 mergats),
`Reporting:RateLimits:Enabled` och `Photos:Enabled` (utöver den redan påslagna
autentiseringen). Startkontrollerna i `Program.cs` är oförändrade.

Poängvärdena i releasen är kodens standardvärden, nu uttryckligen satta i
`release.yml`: 10 poäng per bekräftad åtgärd (`POINTS_CONFIRMED_ACTION_POINTS`),
högst 10 poäng per gåva (`POINTS_MAX_GIFT_PER_TRANSFER`), högst 30 poäng i gåvor
per konto och dygn (`POINTS_MAX_GIFT_PER_DAY`), regelversion `poang-v1`
(`POINTS_RULE_VERSION`) och gåvor påslagna (`POINTS_GIFTS_ENABLED`). Ägaren har
beslutat att poäng ska vara på men **har inte fattat ett eget beslut om belopp
och gränser**; de är en öppen punkt. Ändra värdena i `release.yml` före taggen om
ägaren vill annat, och höj då `POINTS_RULE_VERSION` så att nya rader går att
skilja från gamla. `deploy-release.sh` avvisar ogiltiga värden innan något ändras.

EF-migreringar tillämpas **aldrig** automatiskt, varken vid uppstart eller av
release-workflowen. `deploy-release.sh` kontrollerar i stället skrivskyddat
(`BEGIN READ ONLY`) mot `__EFMigrationsHistory` och `has_table_privilege` att de
migreringar och grants som de påslagna brytarna kräver finns. Saknas något
avbryts deployen med ett svenskt felmeddelande innan webben byts, och den gamla
versionen fortsätter köra. Gör därför stegen nedan **i denna ordning före** den
första taggen med brytarna påslagna.

Filerna i ordning, alla granskade i samma PR som brytarna:

| Fil | Innehåll |
|---|---|
| `deploy/pi-workflow-migrations.sql` | Idempotent `dotnet ef migrations script --idempotent` från `20261108035738_IdentityAccountActivity` (Pi:ns nuvarande nivå via `identity-schema.sql`) till `20261120090000_Points`: granskning, åtgärder/bekräftelser, bilder (med triggrar), flaggor, arkiv, historik för återtagna bekräftelser samt poänghändelser och hjärtan. Ett test jämför filen med EF-migreringarna. |
| `deploy/pi-public-write-role.sql` | INSERT/DELETE på `reports` för `varplats_reader`. |
| `deploy/pi-workflow-grants.sql` | Minsta behörigheter per ny tabell för `varplats_reader` (se kommentarer i filen). Ingen UPDATE eller DELETE där koden inte behöver det; bildmetadata är oföränderlig. |

CI kör migreringsskriptet två gånger och sedan båda grantfilerna mot en tom
PostGIS-databas med Pi:ns bas- och Identity-schema, och kontrollerar varje
behörighet. Det ersätter inte ett prov mot en kopia av Pi:ns riktiga data.

Stegen körs på Pi:n som `kapten` i `/home/kapten/apps/varplats`. Hämta de tre
SQL-filerna från **samma granskade commit** som den kommande taggen till en
privat katalog, till exempel `~/varplats-sql/`, och jämför dem med PR:en.

```sh
cd /home/kapten/apps/varplats
compose=(docker compose --project-name varplats --env-file deploy/.env
  -f deploy/compose.yml -f deploy/pi.override.yml)
```

1. **Backup.** Ta en färsk databasbackup och kontrollera att den går att läsa.
   Lägg den krypterad utanför Pi:n enligt backupavsnittet nedan.

   ```sh
   umask 077; mkdir -p ~/varplats-backup
   backup=~/varplats-backup/fore-brytare-$(date +%F).backup
   "${compose[@]}" exec -T db pg_dump -U varplats -d varplats -Fc > "$backup"
   "${compose[@]}" exec -T db pg_restore --list < "$backup" | head
   ```

   Prova helst en återställning i en separat, tom testdatabas innan steg 2.
2. **Migreringar.** Kör skriptet som schemaägaren. Varje migrering körs i en
   egen transaktion och hoppas över om den redan finns i `__EFMigrationsHistory`.
   Vid fel stoppar `ON_ERROR_STOP` skriptet; tidigare klara migreringar ligger
   kvar och skriptet kan köras om efter rättning.

   ```sh
   "${compose[@]}" exec -T db psql -U varplats -d varplats -v ON_ERROR_STOP=1 \
     < ~/varplats-sql/pi-workflow-migrations.sql
   "${compose[@]}" exec -T db psql -U varplats -d varplats \
     -c 'SELECT "MigrationId" FROM "__EFMigrationsHistory" ORDER BY 1;'
   ```

   Listan ska sluta med `20261120090000_Points` (tio rader totalt).
   Den nuvarande webben fungerar vidare: med brytarna av läses inga nya tabeller.
3. **Grants.** Kör skrivgranten och sedan arbetsflödesgranten.

   ```sh
   "${compose[@]}" exec -T db psql -U varplats -d varplats -v ON_ERROR_STOP=1 \
     < ~/varplats-sql/pi-public-write-role.sql
   "${compose[@]}" exec -T db psql -U varplats -d varplats -v ON_ERROR_STOP=1 \
     < ~/varplats-sql/pi-workflow-grants.sql
   ```
4. **Deploy.** Skapa den annoterade versionstaggen på `main` enligt det vanliga
   flödet och godkänn `pi-preview`. Releasejobbet installerar `clamd.conf` och
   `freshclam.conf`, kör förkontrollen, förbereder bildvolymerna, startar
   `clamav` och byter sedan webben. Första starten av clamd kan ta många minuter;
   jobbets tidsgräns är 30 minuter.
5. **Röktest** med syntetiska data, aldrig riktiga personuppgifter:
   - `/health/ready` svarar och `"${compose[@]}" --profile photos ps` visar
     `clamav` som `healthy`;
   - logga in, skicka en syntetisk rapport med en syntetisk bild och godkänn
     den som admin; kontrollera att den syns på kartan och i listan;
   - lägg en åtgärd från ett konto och bekräfta den från ett annat; ta tillbaka
     bekräftelsen och kontrollera att samma konto inte kan bekräfta igen; flagga
     åtgärden och avfärda flaggan som admin;
   - kontrollera att åtgärdens författare fick poäng en gång vid bekräftelsen och
     att de togs tillbaka när bekräftelsen togs tillbaka; ge och ta bort ett
     hjärta; skicka en gåva inom gränsen och kontrollera att en för stor gåva
     nekas; öppna `/mina-poang`;
   - arkivera och återställ rapporten;
   - skicka många inskick i följd och kontrollera att skrivgränsen ger ett
     svenskt meddelande;
   - ta bort testinnehållet som admin efteråt och läs loggarna utan att kopiera
     innehåll från dem.
6. **Rollback.**
   - *Snabbt, på Pi:n:* starta webben utan releasens miljö. Då är alla brytare
     `false` och autentiseringen av, alltså skrivskyddat läge. Ange föregående
     digest i `WEB_IMAGE` i `deploy/.env` om även koden ska tillbaka, och stoppa
     skannern.

     ```sh
     "${compose[@]}" up -d --no-build --no-deps web
     "${compose[@]}" --profile photos stop clamav
     ```
   - *Varaktigt:* sätt brytarna till `"false"` i `.github/workflows/release.yml`
     via en granskad PR och tagga en ny release. Kvarvarande tabeller och grants
     påverkar inte webben när brytarna är av.
   - *Data:* återställ backupen från steg 1 bara om data skadats, eftersom allt som
     skrivits efter backupen försvinner. Stoppa webben först, återställ till en
     tom databas enligt backupavsnittet och kontrollera rapportantal. Ta även
     backup av volymen `varplats_photos` tillsammans med databasen; bildfiler
     och bildrader måste höra ihop.

### Öppna lanseringskrav som koden inte löser

Brytarna är på, men följande är **inte klart** och är fortfarande ägarens ansvar
innan riktiga användare bjuds in:

- [ ] Integritets- och retentionsbeslut för inskick, åtgärder, bilder, flaggor,
      arkivhistorik, historik över återtagna bekräftelser och poänghändelser, inklusive fritext som kan innehålla personuppgifter.
- [ ] Villkor och integritetsinformation uppdaterade och godkända för rapportering,
      åtgärder, bilder och anmälningar.
- [ ] Bemannad granskningskö med svarstid, även för direktpublicerade åtgärder och
      anmälningar.
- [ ] Prov av backup och återställning (databas och bildvolym tillsammans) i en
      separat, tom miljö.
- [ ] Prov av clamd på Pi:n (ARM64): start, signaturuppdatering, minne och svar
      på `zINSTREAM`.
- [ ] Lastprov på Pi:n med alla brytare på (Blazor-kretsar, RAM, clamd, databas).
- [ ] Ägarbeslut om poängbelopp och gränser (releasen använder kodens
      standardvärden), samt spärrar, bedrägeribedömning och ett
      administratörsgränssnitt för att återkalla poäng.

Kända begränsningar som följer med: API-gränsen för `GET /api/reports` blir en
gemensam gräns bakom Caddy, skrivgränserna är per process och nollställs vid
omstart, och spam-/Sybil-skydd över många konton saknas.

## Backup, återställning och rollback

Daglig krypterad backup till en annan enhet, med fastställd retention och
övervakad framgång. Exempel utan att skriva lösenord på kommandoraden:

```sh
docker compose --env-file deploy/.env -f deploy/compose.yml exec -T db \
  pg_dump -U varplats -d varplats -Fc > varplats.backup
```

Komplettera med säkert arkiv för konfiguration, secrets och Data Protection-
nycklar. Lägg aldrig backup i Git. Prova regelbundet `pg_restore` i en **separat,
tom testdatabas**, kontrollera rapportantal, koordinater och PostGIS-extension.
Bestäm RPO/RTO innan riktiga data tas emot.

Rollback: välj föregående digest och återstarta webb, förutsatt bakåtkompatibelt
schema. Schemaåterställning kan förlora senare data och kräver medvetet beslut.
Övervaka `/health/ready`, inte bara processen. Loggar får inte fånga innehåll,
koordinater, hemligheter eller fulla formulär; begränsa retention och åtkomst.

## Begränsningar i verifieringen

Windows-build och test är inte ett ARM64-driftprov. Lokal Docker startades för
verifiering och x64-images/databas/API kunde byggas och provas. CI innehåller
ARM64 container- och PostGIS-prov. Se aktuella CI-resultat,
inte ett antagande om att workflow-filen i sig betyder godkänt resultat.

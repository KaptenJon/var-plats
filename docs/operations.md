# Raspberry Pi och drift

**Inte ett godkännande för publik lansering.** Läs SECURITY.md och juridiska utkast
först. Publika skrivningar och organisationsbevakning är inte öppnade.

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
För godkända publika skrivningar finns `deploy/pi-public-write-role.sql`, som
ger runtime-rollen INSERT/DELETE på rapporttabellen. Kör den manuellt först när
integritets- och retentionsbeslut är klara och funktionen uttryckligen ska öppnas;
den nuvarande Pi-deployen förblir skrivskyddad.

Webb kör som icke-root, med read-only rotfilsystem, tmpfs, borttagna capabilities
och beständig Data Protection-volym. Kryptering av volym/backup och nycklar är
driftansvar. Kontrollera volymrättigheter innan första riktiga uppstart.

### Framtida bildlagring – inte aktiverad i Pi-releasen

Bildkoden är opt-in och aktiverar inte bildtjänster i Compose eller release och
ändrar inga grants eller resurser på Pi. Standard
`Photos:Enabled=false` kräver ingen bildtabell, bildvolym eller skanner. Skapa
inte dessa resurser och tillämpa inte `20261108164953_ReportPhotos` i den
nuvarande förhandsversionen. Aktivering kräver separat ägarbeslut, granskade
begränsade databasbehörigheter, backup och ett testat migrations-/återställnings-
förfarande utöver alla skriv- och lanseringsgrindar i SECURITY.md.

För ett framtida isolerat driftprov krävs 64-bitars Linux med `procfs`, en
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

Release-workflow bygger fortfarande på GitHub-hostad ARM64-runner. Jobbet
`deploy-pi` kör endast för versionstaggar som verifierats tillhöra `main`,
använder environment `pi-preview` och runner-label `varplats-deploy`. Skapa
environment med ägaren som obligatorisk granskare och tillåt endast versionstaggar
innan runnern registreras. Kör aldrig CI, fork-PR eller checkout av PR-kod på Pi:n.
En self-hosted runner i ett publikt repo är trots detta en risk: workflowkod kan
ändras, och Docker-medlemskap ger i praktiken administratörsåtkomst till värden.
Ägaren måste godkänna risken; använd en separat deploymentvärd för stark isolering.

Releasejobbet läser `GOOGLE_CLIENT_ID`, `GOOGLE_CLIENT_SECRET`,
`OAUTH_GITHUB_CLIENT_ID`, `OAUTH_GITHUB_CLIENT_SECRET`, `MAILJET_API_KEY`,
`MAILJET_API_SECRET` och `MAILJET_SENDER_EMAIL` som secrets från environment
`pi-preview`. GitHub-namnen för GitHub OAuth mappas till Compose-inställningarna
`GITHUB_CLIENT_ID` och `GITHUB_CLIENT_SECRET`. De skickas till webbcontainern vid
varje godkänd deploy och skrivs inte till Git eller deployens `.env`; Docker-
administratörer på Pi:n kan dock läsa containerns miljö. Begränsa därför runner-
och värdåtkomst. Den godkända release-workflowen aktiverar autentisering men
håller `REPORTING_PUBLIC_WRITES` till `false`. Den läser Caddys exakta IPv4-gateway
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

För en **ny, tom installation** skapas ett slumpat schemaägarlösenord i
`deploy/secrets/db_password` och ett separat hexlösenord i
`deploy/secrets/runtime_password`. Katalogen ska vara `700`; `.env` och
runtime-lösenordet `600`. DB-secret måste vara läsbar av containerns postgres-
användare, exempelvis `444` innanför den skyddade katalogen. `.env` ska använda
`Username=varplats_reader` och runtime-lösenordet i `DATABASE_CONNECTION`.
SQL-initkatalogen måste vara läsbar i containern (`755`, SQL-filer `644`), även
om kopiering/extrahering skett med restriktiv umask. Installationsroten förblir
`700`, så andra värdanvändare får inte åtkomst genom dessa filrättigheter.
Det första godkända deploymentjobbet initierar databasen och SELECT-only-rollen
samt sätter ägarskap på endast projektets Data Protection-volym.

Images hämtas med workflowets exakta SHA-256-digests. GHCR-token används enbart i
en tillfällig Docker-konfiguration, inte i användarens befintliga registry-login.
`pi.override.yml` begränsar CPU, RAM, processantal och loggstorlek för den nya
appen. Senare deployment ändrar **endast webbcontainern**; databasen förblir
pinnad. Den godkända autentiseringsreleasen kör den idempotenta
`deploy/identity-schema.sql` och den begränsade `deploy/pi-identity-role.sql`
som `varplats`-ägaren innan webbcontainern uppdateras. Detta påverkar bara
Identity/passkeytabeller och deras runtime-grant, inte rapporttabellen, dess
data eller rapportbehörigheter. Övriga schema- och databasimageändringar kräver
separat granskning och backup. Misslyckad readiness eller auth-smoketest rullar
tillbaka webbimagen utan att stoppa databasen eller andra appar. När autentisering
är aktiverad kontrolleras att inloggningsalternativen visas och att en auth-route
är mappad.

Verifiera `docker info` för stöd för minnes-cgroups: vissa Pi-kärnor ignorerar
`mem_limit`. Pi-override begränsar även .NET:s GC-heap till 256 MiB och sänker
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
Endast exakt det konfigurerade GitHub-ID:t får adminrollen; Google-inloggning ger
inte adminbehörighet. Google- och GitHub-konton länkas endast explicit från en
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
sidan Inloggningsmetoder. `deploy/pi-public-write-role.sql` är en separat spärr
och körs inte; rapportskrivning förblir avstängd.

Operatörens lagringsregel är högst 12 månader för krypterade säkerhetskopior.
Backupmålet ligger utanför denna release-workflow: konfigurera och kontrollera
dess automatiska utgång separat. Vid begäran om radering ska uppgifter tas bort
från den aktiva databasen utan onödigt dröjsmål; användaren får svar inom en
månad, med högst två månaders förlängning för komplexa ärenden och besked inom
den första månaden. Säkerhetskopior kan innehålla raderade uppgifter tills
rotationen löper ut.

`REPORTING_PUBLIC_WRITES` är `false` i Compose-exemplet. Att sätta den till `true`
kräver även fungerande OAuth-konfiguration och den separata manuella SQL-granten.
Den aktuella releasen öppnar inte rapportskrivning.

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

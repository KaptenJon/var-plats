# Raspberry Pi och drift

**Inte ett godkännande för publik lansering.** Läs SECURITY.md och juridiska utkast
först. Konton, publika skrivningar och organisationsbevakning är inte öppnade.

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
runtime-roll med endast SELECT före publik läsdrift. Produktionskoden skriver
inte, men databasen ska försvara samma gräns. Begränsa schemaägaren till migrationer.

Webb kör som icke-root, med read-only rotfilsystem, tmpfs, borttagna capabilities
och beständig Data Protection-volym. Kryptering av volym/backup och nycklar är
driftansvar. Kontrollera volymrättigheter innan första riktiga uppstart.

## GitHub-deployment till en separat Pi-installation

Release-workflow bygger fortfarande på GitHub-hostad ARM64-runner. Jobbet
`deploy-pi` kör endast för versionstaggar som verifierats tillhöra `main`,
använder environment `pi-preview` och runner-label `varplats-deploy`. Skapa
environment med ägaren som obligatorisk granskare och tillåt endast versionstaggar
innan runnern registreras. Kör aldrig CI, fork-PR eller checkout av PR-kod på Pi:n.
En self-hosted runner i ett publikt repo är trots detta en risk: workflowkod kan
ändras, och Docker-medlemskap ger i praktiken administratörsåtkomst till värden.
Ägaren måste godkänna risken; använd en separat deploymentvärd för stark isolering.

Installationen under `/home/kapten/apps/varplats` innehåller `deploy/compose.yml`,
`deploy/pi.override.yml`, SQL-init, `deploy/pi-runtime-role.sql` och en skyddad
`deploy/.env`. Installera `deploy/deploy-release.sh` som
`bin/deploy-release.sh`. Script och Compose-konfiguration uppdateras separat
efter granskning, inte genom att Actions checkar ut godtycklig kod på Pi:n.
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
Det första godkända deploymentjobbet initierar databasen och SELECT-only-rollen
samt sätter ägarskap på endast projektets Data Protection-volym.

Images hämtas med workflowets exakta SHA-256-digests. GHCR-token används enbart i
en tillfällig Docker-konfiguration, inte i användarens befintliga registry-login.
`pi.override.yml` begränsar CPU, RAM, processantal och loggstorlek för den nya
appen. Senare deployment ändrar **endast webbcontainern**; databasen förblir
pinnad och inga migrationer körs automatiskt. Schema- och databasimageändringar
kräver separat granskning och backup. Misslyckad readiness rullar tillbaka till
föregående webbimage utan att stoppa databasen eller andra appar.

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

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

| Fungerar nu | Inte implementerat ännu |
|---|---|
| Leaflet-karta, kategorifilter och tillgänglig rapportlista | Konton, behörigheter och publik rapportering |
| Validering och rapportering i isolerad lokal demo | Bilder, EXIF-rensning och modereringsverktyg |
| EF Core/PostGIS-lagring i lokalt utvecklingsläge | Dokumenterade åtgärder och oberoende bekräftelser |
| Läs-API, livs-/beredskapskontroller, OTLP vid konfiguration | Poäng, missbruksskydd för poäng och organisationsbevakning |
| Installerbart webb/PWA-skal; kräver nätanslutning | Betalning, native-app och offlinekartor |

Produktionsläge är **endast läsning** och kräver databas. Ingen POST-route finns.
Demo eller lokala skrivningar i Production stoppar uppstarten.
Organisationsbevakning blir inte officiell kommunal felanmälan.

## Teknik och drift

- [Arkitektur och roadmap](docs/architecture.md)
- [Raspberry Pi, backup och drift](docs/operations.md)
- [Verifierade versioner och källor](docs/versions.md)
- [GitHub-kontroller och reviewbegränsningar](docs/github.md)
- [Säkerhetsmodell och återstående lanseringskrav](SECURITY.md)
- [Bidra och utveckla](CONTRIBUTING.md)
- [Villkor](docs/legal/terms-draft.md), [integritet](docs/legal/privacy-draft.md)
  och [innehållsregler](docs/legal/content-draft.md): **EJ LANSERINGSKLARA utkast**

Karttiles hämtas direkt från OpenStreetMap med synlig attribution och vanlig
webbläsarcache. Ingen serviceworker cachelagrar, förhämtar eller hämtar karttiles.
OSM:s community-tjänst har ingen SLA och är inte obegränsad gratis hosting.
`Map__TileUrl` och `Map__Attribution` kan ändras utan ombyggnad; granska alltid
leverantörens villkor, integritet och attribution innan byte.

## Öppna beslut före lansering

Kodlicens, personuppgiftsansvarig/kontaktvägar, rättslig grund och retention,
Pi-modell/RAM/64-bitars OS, domän/TLS, autentisering, modereringsansvar,
bildhantering, poängregler och tile-kapacitet. Ingen betaltjänst har aktiverats.

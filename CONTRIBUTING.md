# Bidra till Vår plats

Tack! Läs README, SECURITY och uppförandekoden. Projektets egen kod omfattas av
MIT-licensen i `LICENSE`; tredjepartskomponenter har separata villkor.
Skicka inte in extern kod utan klar rättighet och kompatibla licensvillkor.

## Utveckling

Använd SDK-versionen i global.json. Börja med lokal demo enligt README. Testdata
ska vara syntetiska utan namn, adresser till enskilda, foton eller hemligheter.

```powershell
dotnet restore --locked-mode
dotnet build -c Release --no-restore
dotnet test -c Release --no-build
dotnet format --verify-no-changes --no-restore
```

Ändra paketversionsfilen med avsikt, kör `dotnet restore` och checka in de ändrade
lockfilerna tillsammans. Leaflet uppdateras via `npm ci --ignore-scripts` efter
ändrad package.json/lockfil, sedan kopieras `dist/leaflet.js`, `dist/leaflet.css`,
`dist/images` och `LICENSE` till `wwwroot/vendor/leaflet`. Kontrollera diff,
paketintegritet och tredjepartslicenser. Ingen CDN behövs i runtime.

Håll PR små. Beskriv beteende och risk, lägg till relevanta tester och uppdatera
direkt berörda dokument. Kontrollera tangentbord, mobilbredd, fel-/tomlägen och
att OSM-attribution alltid syns. Ändra inte tile-cache till prefetch.
Publika skrivningar får inte smygaktiveras som en UI-förbättring.

Använd issues för förslag; säkerhetsbrister rapporteras privat enligt SECURITY.
Använd aldrig verkliga personuppgifter i reproduktioner, skärmbilder eller loggar.

# Tredjepartskomponenter

Projektets egen kod omfattas av MIT-licensen i `LICENSE`. Den licensen ändrar
inte respektive tredjepartskomponents villkor. Följande är en inventering, inte
en fullständig licensgranskning.

- Leaflet 1.9.4: BSD-2-Clause, full licens i
  `src/VarPlats.Web/wwwroot/vendor/leaflet/LICENSE`.
- Radzen.Blazor: MIT; se paketets licens på NuGet.
- .NET, ASP.NET Core, EF Core, Aspire och OpenTelemetry: respektive paketlicenser.
- Npgsql och Npgsql EF Core: PostgreSQL-licens enligt paketen.
- SkiaSharp/Skia och Magick.NET/ImageMagick: se respektive pakets och
  inbyggda native-komponenters licenser och medföljande meddelanden i
  `third-party-notices/` i webbimagen.
- ClamAV ingår inte i webbimagen; en eventuell lokal skanner har egna licens-
  och distributionsvillkor.
- PostgreSQL/PostGIS och Debian-paket i databasimagen: respektive upstream-
  licenser, inklusive PostGIS GPL-villkor. Distribution av images kräver att
  tillämpliga licens- och källkodserbjudandekrav bedöms/följs.
- OpenStreetMap-data: ODbL; synlig attribution och
  <https://www.openstreetmap.org/copyright>. Tiles har dessutom separat policy.

Inventera transitiva paket och containerinnehåll före extern distribution;
SBOM i release hjälper inventeringen men är inte en juridisk licensgranskning.

## Återstående distributionsgranskning

De befintliga webb- och databasimagernas GHCR-taggar var publikt nedladdningsbara
vid kontroll 2026-11-08. Publicera inte nya offentliga images innan tillämpliga
licens- och källkodsvillkor har inventerats och uppfyllts. Den privata
förhandsmiljön är inte ett godkännande för extern distribution och undanröjer
inte eventuella licens- eller källkodskrav.

- Bildpaketens egna meddelanden medföljer webbimagen, men en kontroll av
  `dotnet publish` visade att bland annat Radzens och NetTopologySuites
  fullständiga licensmeddelanden inte automatiskt följer med. Inventera och
  inkludera nödvändiga meddelanden även för övriga runtime-beroenden.
- Magick.NET:s och SkiaSharps meddelanden listar även LGPL-komponenter. Fastställ
  vilka som faktiskt ingår i Linux/ARM64-bygget och uppfyll eventuella krav på
  motsvarande källkod och möjlighet att återlänka.
- Databasimagen innehåller PostGIS och Debian-paket med copyleft-villkor.
  Kartlägg även webb- och databasimagernas operativsystemspaket och ordna
  tillgång till exakt motsvarande källkod där det krävs. En SBOM eller enbart
  en länk till en upstream-version är inte i sig ett verifierat källkodserbjudande.

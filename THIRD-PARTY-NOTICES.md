# Tredjepartskomponenter

Projektets egen kodlicens är **inte vald**. Följande information väljer inte en
licens för projektet och ersätter inte respektive komponents licensvillkor.

- Leaflet 1.9.4: BSD-2-Clause, full licens i
  `src/VarPlats.Web/wwwroot/vendor/leaflet/LICENSE`.
- Radzen.Blazor: MIT; se paketets licens på NuGet.
- .NET, ASP.NET Core, EF Core, Aspire och OpenTelemetry: respektive paketlicenser.
- Npgsql och Npgsql EF Core: PostgreSQL-licens enligt paketen.
- SkiaSharp/Skia och Magick.NET/ImageMagick: se respektive pakets och
  inbyggda native-komponenters licenser och medföljande meddelanden i
  `third-party-notices/` i publicerad webbapp eller webbimage.
- ClamAV ingår inte i webbimagen; en eventuell lokal skanner har egna licens-
  och distributionsvillkor.
- PostgreSQL/PostGIS och Debian-paket i databasimagen: respektive upstream-
  licenser, inklusive PostGIS GPL-villkor. Distribution av images kräver att
  tillämpliga licens- och källkodserbjudandekrav bedöms/följs.
- OpenStreetMap-data: ODbL; synlig attribution och
  <https://www.openstreetmap.org/copyright>. Tiles har dessutom separat policy.

Inventera transitiva paket och containerinnehåll före extern distribution;
SBOM i release hjälper inventeringen men är inte en juridisk licensgranskning.

# Versionsunderlag

Kontrollerat 2026-11-03 mot faktiska paketindex och release-metadata. Ingen stabil
.NET 11-release antas. Uppdatera som en sammanhållen kompatibilitetsändring.

| Komponent | Låst version |
|---|---|
| .NET SDK | 11.0.100-rc.3.26522.148 |
| ASP.NET Core / EF Core | 11.0.0-rc.3.26522.148 |
| Npgsql EF Core + NetTopologySuite | 11.0.0-rc.3 |
| Aspire AppHost SDK / hosting | 13.6.4 |
| Radzen.Blazor | 12.1.10 |
| OpenTelemetry | 1.20.1 |
| Leaflet | 1.9.4 |
| PostgreSQL bas | 18-bookworm, låst OCI-digest i Dockerfile |
| PostGIS vid lokalt verifieringsbygge | 3.7, från PostgreSQL:s Debian-paketförråd |

Npgsql RC3:s nuspec kräver exakt EF RC3 ovan. Radzen har net10-assets som används
av net11; solutionens build/test kontrollerar kompatibilitet. Aspire använder
senaste verifierade stabila paket och AppHost SDK, inte en antagen ".NET 11 Aspire".

NuGet-versioner finns i `Directory.Packages.props` och hela beroendeträdet i
`packages.lock.json` per projekt. `global.json` tillåter inte tyst SDK-rollforward.
Leaflet är låst i npm-manifest/lockfil och distributionen är incheckad lokalt
inklusive BSD-licensen. Node krävs bara vid uppdatering, inte i runtime.

Containerbaser är digest-låsta. Taggen är läsbar dokumentation, digest är identitet.
PostGIS installeras från PostgreSQL-basens signerade Debian-paketförråd vid bygge,
med paket för samma PostgreSQL-majorversion. PostgreSQL 18:s `PGDATA` ligger som
standard i `/var/lib/postgresql/18/docker`; Compose monterar därför datavolymen
på `/var/lib/postgresql`. En uppgradering från 17 kräver separat datamigrering
och verifierad backup; se [driftguiden](operations.md#första-lokala-driftprovet).
APT-transitiva paket är **inte byte-reproducerbart låsta**; publicerade release-images
ska därför användas via digest för identisk drift. Uppdatera och bygg om regelbundet
för säkerhetsfixar, med ny version och granskad diff.

## Källor

- [.NET 11 release-metadata](https://builds.dotnet.microsoft.com/dotnet/release-metadata/11.0/releases.json)
- [Npgsql EF RC3 nuspec](https://api.nuget.org/v3-flatcontainer/npgsql.entityframeworkcore.postgresql/11.0.0-rc.3/npgsql.entityframeworkcore.postgresql.nuspec)
- [NuGet](https://www.nuget.org/) – paketindex och nuspec för respektive paket ovan
- [Aspire](https://aspire.dev/) och [Radzen](https://blazor.radzen.com/)
- [.NET på ARM-enheter](https://learn.microsoft.com/dotnet/iot/deployment)
- [.NET 11 hårdvarukrav](https://learn.microsoft.com/dotnet/core/compatibility/jit/11/minimum-hardware-requirements)
- [OSM tile-policy](https://operations.osmfoundation.org/policies/tiles/)
- [Leaflet 1.9.4](https://www.npmjs.com/package/leaflet/v/1.9.4)

Microsofts metadata listar Linux ARM64 för SDK/runtime. Det bevisar inte att en
okänd Pi-modell, 32-bitars OS eller alla containerberoenden fungerar.
Linux ARM64:s JIT/AOT-minimum är fortsatt **armv8.0-a**, medan ReadyToRun-målet
har utökats med LSE. Äldre Linux-CPU:er utan LSE kan därför behöva mer JIT-arbete,
inte automatiskt vara inkompatibla. Windows ARM64:s skärpta krav ska inte
förväxlas med Linux. Se driftkraven; gör ett riktigt Pi-prov före produktion.

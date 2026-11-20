# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Project

"Vår plats" — a mobile-first, map-centred Swedish web/PWA for reporting problems in public places, documenting actions and having other accounts confirm them. It is a **prototype, not launch-ready**. All docs (README, SECURITY, docs/*) and the UI are in Swedish; keep user-facing text Swedish. Read README.md and SECURITY.md before changing behaviour.

## Commands

The SDK is pinned exactly in `global.json` (.NET 11.0.100, `rollForward: disable`); EF Core/Identity/Npgsql packages are still RC 3. `TreatWarningsAsErrors` is on for all projects.

```powershell
dotnet restore --locked-mode
dotnet build -c Release --no-restore
dotnet test -c Release --no-build
dotnet format --verify-no-changes --no-restore     # CI enforces this
```

- Single test: `dotnet test -c Release --no-build --filter "FullyQualifiedName~ReportTests.SomeMethod"` (xUnit).
- DB integration tests (`ReportWorkflowDatabaseTests`) skip unless `VARPLATS_REPORT_TEST_DB` points at an **empty, isolated, loopback** PostGIS database named `varplats_workflow_test` — they create tables and delete accounts. Easiest: `bash tests/report-workflow-db.test.sh` (spins up a throwaway PostGIS container and runs the full already-built suite; this is what CI does).
- Run locally without Docker (in-memory, per-circuit demo data): `dotnet run --project src/VarPlats.Web --launch-profile demo` → http://localhost:5180
- Run with PostgreSQL/PostGIS via Aspire (needs Docker + trusted dev cert): `dotnet run --project src/VarPlats.AppHost` (uses the `database` launch profile with `Reporting__LocalWrites=true`).
- CI also runs `node --check` on `wwwroot/{map,passkeys,service-worker}.js`, `bash -n` on deploy scripts, `bash tests/deploy-release.test.sh`, and `docker compose -f deploy/compose.yml config`.

Package management: central versions in `Directory.Packages.props` with committed `packages.lock.json` per project. After changing versions run `dotnet restore` and commit the lockfiles together. Leaflet is vendored: `npm ci --ignore-scripts`, then copy `dist/leaflet.js`, `dist/leaflet.css`, `dist/images`, `LICENSE` into `src/VarPlats.Web/wwwroot/vendor/leaflet`. No CDN at runtime; Node is not needed to run the app.

## Architecture

Modular monolith — one ASP.NET Core process, not microservices (see docs/architecture.md).

- `VarPlats.Core` — report/action models, validation (`ReportDraft`, `ReportActionDraft` as `IValidatableObject`), store interfaces (`IReportStore`, `IReportWorkflowStore`, `IReportActionStore`) and the in-memory `DemoReportStore`.
- `VarPlats.Web` — Blazor Server + Radzen UI, a small read API (`GET /api/reports`), Identity (Google/GitHub OAuth, passkeys, Mailjet email), EF Core adapters in `Data/`, and the photo pipeline in `Photos/` (size/format checks, strict JPEG validation, ClamAV over a unix socket, metadata-stripping re-encode, private Linux storage outside webroot). UI components call store interfaces in-process — no internal HTTP hop.
- `VarPlats.ServiceDefaults` — health checks and OpenTelemetry.
- `VarPlats.AppHost` — Aspire, local dev orchestration only (not a production component). Mounts `deploy/sql` as Postgres init scripts.
- `tools/ThirdPartyNoticeGenerator` — run during the Docker build to bundle NuGet license notices.
- `deploy/` — Dockerfile, compose, Raspberry Pi (ARM64) release scripts, SQL role/grant scripts. Releases are built by `.github/workflows/release.yml` on `v*.*.*` tags.

### Feature gating (central to `src/VarPlats.Web/Program.cs`)

Behaviour is driven by config flags validated at startup; invalid combinations throw:
- `Reporting:Demo` / `Reporting:LocalWrites` — Development only, loopback clients only. Using them outside Development stops startup.
- `Reporting:PublicWrites` requires `Authentication:Enabled` **and** `Reporting:WorkflowEnabled`; `WorkflowEnabled` requires authentication; authentication requires database mode (not Demo).
- `Photos:Enabled` defaults to false and additionally depends on auth/workflow/public-write gates.
- `Reporting:FlagsEnabled` (abuse flags, `ContentFlagCommands`) defaults to false and requires `WorkflowEnabled`; `ReportingMode.FlagQueue` lets admins read the queue, `CanFlag` additionally needs public writes.
- `Reporting:ArchiveEnabled` (admin archiving, `ReportArchiveCommands`) defaults to false and requires `WorkflowEnabled`. When on (`ReportingMode.Archive`), `DatabaseReportStore` list reads exclude rows in `report_archives` and new actions are refused; `GetAsync` still returns archived reports for their page. Never reference the archive (or any unapplied migration's) table in SQL that runs while its switch is off.
- Production default is **read-only** with a database. Do not loosen these guards or "sneak-enable" public writes as a UI improvement.

### Data and schema

- PostGIS `geography(Point,4326)`: **longitude is X, latitude is Y**. Latitude is limited to ±85 (Web Mercator). The map and `GET /api/reports?south=&west=&north=&east=` load at most 200 newest reports in the visible area (`MapArea` in Core, `ListInAreaAsync` on stores; truncation via `ReportPage.Truncated` / `X-Reports-Truncated`). Without an area, the latest 200 overall.
- The base schema comes from versioned init SQL (`deploy/sql/001-initial.sql`), not EF at startup. Init SQL only runs for a fresh volume.
- EF migrations in `src/VarPlats.Web/Data/Migrations` (review workflow, actions/confirmations, photos, content flags, report archive) are **not** applied automatically and are not applied on the Pi by release; each needs separate review, backup and owner approval. When the workflow is disabled, reads must not touch the newer tables so older schemas keep working.
- Submissions live in a separate table; only approved ones are copied into the public `reports` table. Report status with workflow on is computed at read time from actions and confirmations by *other* accounts (self-confirmation forbidden; one confirmation per action+account via composite PK).

## Project rules (from .github/copilot-instructions.md and CONTRIBUTING.md)

- No real personal data anywhere — tests, fixtures, screenshots, logs use synthetic data only.
- Preserve OSM attribution (always visible) and referer. Never prefetch or offline-cache OSM tiles; the service worker must not touch tiles.
- Organisation monitoring is not official municipal fault reporting; never encourage dangerous volunteer actions or reward click counts.
- Don't choose licences or treat legal docs as final for the owner.
- In reviews prioritise: authorization, personal data, geography/coordinate order, validation, risky volunteer actions, database changes, fork-PR security.
- UI conventions (DESIGN.md): ≥44px controls, visible labels, server-side validation, no colour-only status, respect reduced motion, list must work when the map fails, map before form/list below 850px.

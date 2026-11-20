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
- Run with PostgreSQL/PostGIS via Aspire (needs Docker + trusted dev cert): `dotnet run --project src/VarPlats.AppHost` (uses the `database` launch profile with `Reporting__LocalWrites=true`; `-- --VarPlats:WebLaunchProfile=konton` picks the test-account profile).
- All features with synthetic accounts in different roles (needs Docker, no dev cert): `./tools/local-accounts.ps1` → PostGIS container `varplats-local-db` on 127.0.0.1:55432 + launch profile `konton` (every flag except photos, plus `Authentication:DevAccounts`). Sign in at `/login` as admin, medlem-a/b/c, utan-roll (no role) or sparrad (locked). Native `confirm()` dialogs are suppressed in the built-in browser; override `window.confirm` in the page's main world before the first click when driving it.
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
- `Reporting:ConfirmationWithdrawalEnabled` (withdrawing one's own confirmation, `ReportActionCommands.WithdrawConfirmationAsync`) defaults to false and requires `WorkflowEnabled`; `ReportingMode.ConfirmationWithdrawal` reads the history table, `CanWithdrawConfirmation` also needs public writes. Withdrawal moves the row from `report_action_confirmations` into `report_action_confirmation_withdrawals` in one statement (status reads stay unchanged); while on, the same account cannot re-confirm and archived reports refuse withdrawal.
- `Reporting:RateLimits` (`RateLimitOptions`, `WriteRateLimiter`) — `Enabled` defaults to false; per-kind `PermitLimit`/`WindowSeconds` are validated at startup even when off. When on, the command classes (report/action/confirmation/flag/photo) call `WriteRateLimiter.Acquire(accountId, …)` after auth and validation, before saving; it throws `WriteRateLimitException` (Swedish message) that pages must catch. In-memory, per process, keyed on account id, uses `TimeProvider`; logs only the kind. `Api` adds the `reports-api` per-IP policy on `GET /api/reports` (behind Caddy that IP is the proxy). No migration.
- `Reporting:PointsEnabled` (points ledger, hearts, gifts; `PointsCommands`, `DatabasePointsStore`, `PointsLedger`) defaults to false and requires `WorkflowEnabled`; amounts/limits live under `Points:` and are validated at startup. `ReportingMode.Points` reads, `CanUsePoints` additionally needs public writes. Base points are written only inside the confirmation transaction (once per action); `points_events` is append-only (reversals are compensating rows; confirmation withdrawal, action removal and report removal reverse in their own transaction; further hooks on `IPointsStore`), hearts never give points, gifts only move existing points. With the switch off, confirm/remove paths must not name `points_events` or `action_hearts`. No leaderboards.
- `Authentication:DevAccounts` (`DevAccounts.cs`) — Development only, requires `Authentication:Enabled`, not with Demo/LocalWrites, connection host must be localhost/loopback, and the database must carry the comment `varplats-lokal-utveckling` (`DevAccounts.LocalDatabaseMarker`, set by `tools/local-accounts.ps1`) before anything is migrated or seeded. A middleware before `UseForwardedHeaders` refuses requests that are not loopback-to-loopback or that carry proxy headers (`Forwarded`, `X-Forwarded-*`, `X-Real-IP`). The script's DB password is random per container, never in the repo. Replaces OAuth/Mailjet/passkeys (`externalSignIn` is false), seeds synthetic `.test` accounts and runs EF `MigrateAsync` on that local database at startup (with a context built without the app's Identity options, because the migrations were generated without them; with them EF reports pending model changes for Identity key column lengths). Never use it as a way to apply migrations anywhere else.
- Production default is **read-only** with a database. Do not loosen these guards or "sneak-enable" public writes as a UI improvement.
- Admin role comes only from `Authentication:AdminGitHubId` or `Authentication:AdminEmails` (`AdminEmails`, comma-separated, matched against a **confirmed** account email in the cookie `OnSigningIn` event, which also adds the role claim to the cookie being issued). The release reads it from secret `ADMIN_EMAILS`; never put addresses in Git and never auto-promote "the first account".
- **Release (owner decision 2026-11-20):** `.github/workflows/release.yml` sets `REPORTING_WORKFLOW_ENABLED`, `REPORTING_PUBLIC_WRITES`, `REPORTING_FLAGS_ENABLED`, `REPORTING_ARCHIVE_ENABLED`, `REPORTING_CONFIRMATION_WITHDRAWAL_ENABLED`, `REPORTING_POINTS_ENABLED`, `RATE_LIMITS_ENABLED` and `PHOTOS_ENABLED` to true, and passes the `POINTS_*` values explicitly (currently the code defaults; the owner has not decided other amounts/caps) (plus `AUTHENTICATION_ENABLED`). `deploy/compose.yml` and `.env.example` keep every flag `false`. The release never applies migrations or grants: the owner runs `deploy/pi-workflow-migrations.sql`, `pi-public-write-role.sql` and `pi-workflow-grants.sql` by hand after a backup; `deploy-release.sh` does a read-only check (`__EFMigrationsHistory`, `has_table_privilege`) and stops before swapping web if anything is missing. With photos on it also prepares the photo/socket volumes and starts the `clamav` service (profile `photos`, `clamav/clamav-debian` pinned to its arm64 digest). `points_events` is append-only for the runtime role (SELECT/INSERT); account deletion relies on FK cascades/SET NULL, which run as the table owner. A new flag-gated slice must add its migration id and grants to that preflight, the grants file and the CI privilege check before the release enables it.
- `deploy/pi-workflow-migrations.sql` is generated by EF (`PiMigrationScriptTests` compares it with the migrations). After adding a migration, update the test's target and regenerate; when local tests are blocked, CI uploads the regenerated file as the `pi-workflow-migrations` artifact on mismatch. Launch-gate items (privacy/retention, terms, staffed review, backup/restore test, ARM64 clamd test, Pi load test) are still open — don't describe them as done.

### Data and schema

- PostGIS `geography(Point,4326)`: **longitude is X, latitude is Y**. Latitude is limited to ±85 (Web Mercator). The map and `GET /api/reports?south=&west=&north=&east=` load at most 200 newest reports in the visible area (`MapArea` in Core, `ListInAreaAsync` on stores; truncation via `ReportPage.Truncated` / `X-Reports-Truncated`). Without an area, the latest 200 overall.
- The base schema comes from versioned init SQL (`deploy/sql/001-initial.sql`), not EF at startup. Init SQL only runs for a fresh volume.
- EF migrations in `src/VarPlats.Web/Data/Migrations` (review workflow, actions/confirmations, photos, content flags, report archive, points) are **not** applied automatically and are not applied on the Pi by release; each needs separate review, backup and owner approval. When the workflow is disabled, reads must not touch the newer tables so older schemas keep working.
- Submissions live in a separate table; only approved ones are copied into the public `reports` table. Report status with workflow on is computed at read time from actions and confirmations by *other* accounts (self-confirmation forbidden; one confirmation per action+account via composite PK).

## Project rules (from .github/copilot-instructions.md and CONTRIBUTING.md)

- No real personal data anywhere — tests, fixtures, screenshots, logs use synthetic data only.
- Preserve OSM attribution (always visible) and referer. Never prefetch or offline-cache OSM tiles; the service worker must not touch tiles.
- Organisation monitoring is not official municipal fault reporting; never encourage dangerous volunteer actions or reward click counts.
- Don't choose licences or treat legal docs as final for the owner.
- In reviews prioritise: authorization, personal data, geography/coordinate order, validation, risky volunteer actions, database changes, fork-PR security.
- UI conventions (DESIGN.md): ≥44px controls, visible labels, server-side validation, no colour-only status, respect reduced motion, list must work when the map fails, map before form/list below 850px.

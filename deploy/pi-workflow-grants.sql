-- Minsta behörigheter för körrollen varplats_reader när arbetsflöde, åtgärder,
-- bilder, flaggor, arkivering och återtagande av bekräftelser är påslagna.
-- Kör som ägarrollen varplats EFTER deploy/pi-workflow-migrations.sql och
-- deploy/pi-public-write-role.sql. Granskat mot koden 2026-11-20 (Data/*Store.cs,
-- Photos/DatabasePhotoWriter.cs), igen efter #36 (WithdrawConfirmationAsync).
-- Rader som tas bort genom ON DELETE CASCADE kräver ingen DELETE-grant: Postgres
-- kör referensåtgärder som tabellägaren. Inga nya sekvenser behövs (Guid-nycklar).
\set ON_ERROR_STOP on
BEGIN;

-- Inskick: skapas av konton, granskas (UPDATE av status och SELECT ... FOR UPDATE).
GRANT SELECT, INSERT, UPDATE ON TABLE report_submissions TO varplats_reader;

-- Åtgärder: skapas av konton, tas bort av admin. Bekräftelser skapas;
-- DELETE ges nedan bara för återtagande.
GRANT SELECT, INSERT, DELETE ON TABLE report_actions TO varplats_reader;
GRANT SELECT, INSERT ON TABLE report_action_confirmations TO varplats_reader;

-- Bilder: metadata är oföränderlig (trigger) och raderas bara via cascade.
GRANT SELECT, INSERT ON TABLE report_photos TO varplats_reader;

-- Flaggor: skapas av konton, avfärdas av admin (UPDATE av "DismissedAt").
GRANT SELECT, INSERT, UPDATE ON TABLE content_flags TO varplats_reader;

-- Arkivering: aktuell status kan tas bort vid återställning; händelseloggen är append-only.
GRANT SELECT, INSERT, DELETE ON TABLE report_archives TO varplats_reader;
GRANT SELECT, INSERT ON TABLE report_archive_events TO varplats_reader;

-- Återtagande av bekräftelse: en sats flyttar kontots egen rad från
-- report_action_confirmations (DELETE) till append-only-historiken (INSERT).
GRANT DELETE ON TABLE report_action_confirmations TO varplats_reader;
GRANT SELECT, INSERT ON TABLE report_action_confirmation_withdrawals TO varplats_reader;

COMMIT;

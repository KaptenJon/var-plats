START TRANSACTION;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261108154722_ReportReviewWorkflow') THEN
    CREATE TABLE report_submissions (
        "Id" uuid NOT NULL,
        "Title" character varying(100) NOT NULL,
        "Description" character varying(1000) NOT NULL,
        "Category" integer NOT NULL,
        "Location" geography (point, 4326) NOT NULL,
        "CreatedAt" timestamp with time zone NOT NULL,
        "ReviewStatus" integer NOT NULL DEFAULT 0,
        "AuthorId" uuid,
        "PublishedReportId" uuid,
        CONSTRAINT "PK_report_submissions" PRIMARY KEY ("Id"),
        CONSTRAINT "CK_report_submissions_Category" CHECK ("Category" BETWEEN 0 AND 2),
        CONSTRAINT "CK_report_submissions_Description" CHECK (length(trim("Description")) >= 10),
        CONSTRAINT "CK_report_submissions_Publication" CHECK (("ReviewStatus" = 1 AND "PublishedReportId" IS NOT NULL AND "PublishedReportId" = "Id") OR ("ReviewStatus" IN (0, 2) AND "PublishedReportId" IS NULL)),
        CONSTRAINT "CK_report_submissions_ReviewStatus" CHECK ("ReviewStatus" IN (0, 1, 2)),
        CONSTRAINT "CK_report_submissions_Title" CHECK (length(trim("Title")) >= 5),
        CONSTRAINT "FK_report_submissions_AspNetUsers_AuthorId" FOREIGN KEY ("AuthorId") REFERENCES "AspNetUsers" ("Id") ON DELETE SET NULL,
        CONSTRAINT "FK_report_submissions_reports_PublishedReportId" FOREIGN KEY ("PublishedReportId") REFERENCES reports ("Id") ON DELETE CASCADE
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261108154722_ReportReviewWorkflow') THEN
    CREATE INDEX "IX_report_submissions_AuthorId" ON report_submissions ("AuthorId");
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261108154722_ReportReviewWorkflow') THEN
    CREATE UNIQUE INDEX "IX_report_submissions_PublishedReportId" ON report_submissions ("PublishedReportId");
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261108154722_ReportReviewWorkflow') THEN
    CREATE INDEX "IX_report_submissions_ReviewStatus_CreatedAt" ON report_submissions ("ReviewStatus", "CreatedAt");
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261108154722_ReportReviewWorkflow') THEN
    INSERT INTO "__EFMigrationsHistory" ("MigrationId", "ProductVersion")
    VALUES ('20261108154722_ReportReviewWorkflow', '11.0.0-rc.3.26522.148');
    END IF;
END $EF$;
COMMIT;

START TRANSACTION;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261108162158_ReportActionsAndConfirmations') THEN
    CREATE TABLE report_actions (
        "Id" uuid NOT NULL,
        "ReportId" uuid NOT NULL,
        "AuthorId" uuid,
        "Description" character varying(1000) NOT NULL,
        "CreatedAt" timestamp with time zone NOT NULL,
        CONSTRAINT "PK_report_actions" PRIMARY KEY ("Id"),
        CONSTRAINT "CK_report_actions_Description" CHECK (length(trim("Description")) >= 10),
        CONSTRAINT "FK_report_actions_AspNetUsers_AuthorId" FOREIGN KEY ("AuthorId") REFERENCES "AspNetUsers" ("Id") ON DELETE SET NULL,
        CONSTRAINT "FK_report_actions_reports_ReportId" FOREIGN KEY ("ReportId") REFERENCES reports ("Id") ON DELETE CASCADE
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261108162158_ReportActionsAndConfirmations') THEN
    CREATE TABLE report_action_confirmations (
        "ActionId" uuid NOT NULL,
        "AccountId" uuid NOT NULL,
        "CreatedAt" timestamp with time zone NOT NULL,
        CONSTRAINT "PK_report_action_confirmations" PRIMARY KEY ("ActionId", "AccountId"),
        CONSTRAINT "FK_report_action_confirmations_AspNetUsers_AccountId" FOREIGN KEY ("AccountId") REFERENCES "AspNetUsers" ("Id") ON DELETE CASCADE,
        CONSTRAINT "FK_report_action_confirmations_report_actions_ActionId" FOREIGN KEY ("ActionId") REFERENCES report_actions ("Id") ON DELETE CASCADE
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261108162158_ReportActionsAndConfirmations') THEN
    CREATE INDEX "IX_report_action_confirmations_AccountId" ON report_action_confirmations ("AccountId");
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261108162158_ReportActionsAndConfirmations') THEN
    CREATE INDEX "IX_report_actions_AuthorId" ON report_actions ("AuthorId");
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261108162158_ReportActionsAndConfirmations') THEN
    CREATE INDEX "IX_report_actions_ReportId_CreatedAt" ON report_actions ("ReportId", "CreatedAt");
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261108162158_ReportActionsAndConfirmations') THEN
    INSERT INTO "__EFMigrationsHistory" ("MigrationId", "ProductVersion")
    VALUES ('20261108162158_ReportActionsAndConfirmations', '11.0.0-rc.3.26522.148');
    END IF;
END $EF$;
COMMIT;

START TRANSACTION;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261108164953_ReportPhotos') THEN
    CREATE TABLE report_photos (
        "Id" uuid NOT NULL,
        "SubmissionId" uuid,
        "ActionId" uuid,
        "StorageName" character varying(36) NOT NULL,
        "ContentType" character varying(10) NOT NULL,
        "Sha256" character varying(64) NOT NULL,
        "ByteLength" bigint NOT NULL,
        "Width" integer NOT NULL,
        "Height" integer NOT NULL,
        CONSTRAINT "PK_report_photos" PRIMARY KEY ("Id"),
        CONSTRAINT "CK_report_photos_ContentType" CHECK (("ContentType" = 'image/jpeg' AND "StorageName" LIKE '%.jpg') OR ("ContentType" = 'image/png' AND "StorageName" LIKE '%.png')),
        CONSTRAINT "CK_report_photos_Parent" CHECK (("SubmissionId" IS NOT NULL AND "ActionId" IS NULL) OR ("SubmissionId" IS NULL AND "ActionId" IS NOT NULL)),
        CONSTRAINT "CK_report_photos_Sha256" CHECK ("Sha256" ~ '^[0-9a-f]{64}$'),
        CONSTRAINT "CK_report_photos_Size" CHECK ("ByteLength" BETWEEN 1 AND 5242880 AND "Width" BETWEEN 1 AND 4096 AND "Height" BETWEEN 1 AND 4096 AND "Width"::bigint * "Height" <= 16000000),
        CONSTRAINT "CK_report_photos_StorageName" CHECK ("StorageName" ~ '^[0-9a-f]{32}\.(jpg|png)$'),
        CONSTRAINT "FK_report_photos_report_actions_ActionId" FOREIGN KEY ("ActionId") REFERENCES report_actions ("Id") ON DELETE CASCADE,
        CONSTRAINT "FK_report_photos_report_submissions_SubmissionId" FOREIGN KEY ("SubmissionId") REFERENCES report_submissions ("Id") ON DELETE CASCADE
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261108164953_ReportPhotos') THEN
    CREATE UNIQUE INDEX "IX_report_photos_ActionId" ON report_photos ("ActionId");
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261108164953_ReportPhotos') THEN
    CREATE UNIQUE INDEX "IX_report_photos_StorageName" ON report_photos ("StorageName");
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261108164953_ReportPhotos') THEN
    CREATE UNIQUE INDEX "IX_report_photos_SubmissionId" ON report_photos ("SubmissionId");
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261108164953_ReportPhotos') THEN
    CREATE FUNCTION varplats_guard_report_photo() RETURNS trigger
    LANGUAGE plpgsql AS $$
    BEGIN
        IF TG_OP = 'UPDATE' THEN
            RAISE EXCEPTION 'Photo metadata is immutable' USING ERRCODE = 'check_violation';
        END IF;
        IF NEW."SubmissionId" IS NOT NULL THEN
            PERFORM 1 FROM report_submissions
            WHERE "Id" = NEW."SubmissionId" AND "ReviewStatus" = 0
            FOR UPDATE;
            IF NOT FOUND THEN
                RAISE EXCEPTION 'A report photo requires a pending submission'
                    USING ERRCODE = 'check_violation';
            END IF;
        END IF;
        RETURN NEW;
    END;
    $$;
    CREATE TRIGGER varplats_guard_report_photo
    BEFORE INSERT OR UPDATE ON report_photos
    FOR EACH ROW EXECUTE FUNCTION varplats_guard_report_photo();

    CREATE FUNCTION varplats_guard_photo_approval() RETURNS trigger
    LANGUAGE plpgsql AS $$
    BEGIN
        IF OLD."ReviewStatus" = 0 AND NEW."ReviewStatus" = 1
            AND EXISTS (SELECT 1 FROM report_photos WHERE "SubmissionId" = NEW."Id")
            AND current_setting('varplats.photo_review_verified', true) IS DISTINCT FROM NEW."Id"::text THEN
            RAISE EXCEPTION 'A report photo must be available for review'
                USING ERRCODE = 'check_violation';
        END IF;
        RETURN NEW;
    END;
    $$;
    CREATE TRIGGER varplats_guard_photo_approval
    BEFORE UPDATE OF "ReviewStatus" ON report_submissions
    FOR EACH ROW EXECUTE FUNCTION varplats_guard_photo_approval();
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261108164953_ReportPhotos') THEN
    INSERT INTO "__EFMigrationsHistory" ("MigrationId", "ProductVersion")
    VALUES ('20261108164953_ReportPhotos', '11.0.0-rc.3.26522.148');
    END IF;
END $EF$;
COMMIT;

START TRANSACTION;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261119061342_ContentFlags') THEN
    CREATE TABLE content_flags (
        "Id" uuid NOT NULL,
        "ReportId" uuid NOT NULL,
        "ActionId" uuid,
        "ReporterId" uuid NOT NULL,
        "Reason" integer NOT NULL,
        "Note" character varying(500),
        "CreatedAt" timestamp with time zone NOT NULL,
        "DismissedAt" timestamp with time zone,
        CONSTRAINT "PK_content_flags" PRIMARY KEY ("Id"),
        CONSTRAINT "CK_content_flags_Note" CHECK ("Note" IS NULL OR length(trim("Note")) BETWEEN 1 AND 500),
        CONSTRAINT "CK_content_flags_Reason" CHECK ("Reason" BETWEEN 0 AND 5),
        CONSTRAINT "FK_content_flags_AspNetUsers_ReporterId" FOREIGN KEY ("ReporterId") REFERENCES "AspNetUsers" ("Id") ON DELETE CASCADE,
        CONSTRAINT "FK_content_flags_report_actions_ActionId" FOREIGN KEY ("ActionId") REFERENCES report_actions ("Id") ON DELETE CASCADE,
        CONSTRAINT "FK_content_flags_reports_ReportId" FOREIGN KEY ("ReportId") REFERENCES reports ("Id") ON DELETE CASCADE
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261119061342_ContentFlags') THEN
    CREATE INDEX "IX_content_flags_ActionId" ON content_flags ("ActionId");
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261119061342_ContentFlags') THEN
    CREATE INDEX "IX_content_flags_DismissedAt_CreatedAt" ON content_flags ("DismissedAt", "CreatedAt");
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261119061342_ContentFlags') THEN
    CREATE INDEX "IX_content_flags_ReporterId_CreatedAt" ON content_flags ("ReporterId", "CreatedAt");
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261119061342_ContentFlags') THEN
    CREATE UNIQUE INDEX "IX_content_flags_ReportId_ActionId_ReporterId" ON content_flags ("ReportId", "ActionId", "ReporterId") NULLS NOT DISTINCT;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261119061342_ContentFlags') THEN
    INSERT INTO "__EFMigrationsHistory" ("MigrationId", "ProductVersion")
    VALUES ('20261119061342_ContentFlags', '11.0.0-rc.3.26522.148');
    END IF;
END $EF$;
COMMIT;

START TRANSACTION;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261119222822_ReportArchive') THEN
    CREATE TABLE report_archive_events (
        "Id" uuid NOT NULL,
        "ReportId" uuid NOT NULL,
        "Kind" integer NOT NULL,
        "Reason" integer,
        "Note" character varying(500),
        "CreatedAt" timestamp with time zone NOT NULL,
        CONSTRAINT "PK_report_archive_events" PRIMARY KEY ("Id"),
        CONSTRAINT "CK_report_archive_events_Kind" CHECK (("Kind" = 0 AND "Reason" BETWEEN 0 AND 3) OR ("Kind" = 1 AND "Reason" IS NULL AND "Note" IS NULL)),
        CONSTRAINT "CK_report_archive_events_Note" CHECK ("Note" IS NULL OR length(trim("Note")) BETWEEN 1 AND 500),
        CONSTRAINT "FK_report_archive_events_reports_ReportId" FOREIGN KEY ("ReportId") REFERENCES reports ("Id") ON DELETE CASCADE
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261119222822_ReportArchive') THEN
    CREATE TABLE report_archives (
        "ReportId" uuid NOT NULL,
        "Reason" integer NOT NULL,
        "Note" character varying(500),
        "ArchivedAt" timestamp with time zone NOT NULL,
        CONSTRAINT "PK_report_archives" PRIMARY KEY ("ReportId"),
        CONSTRAINT "CK_report_archives_Note" CHECK ("Note" IS NULL OR length(trim("Note")) BETWEEN 1 AND 500),
        CONSTRAINT "CK_report_archives_Reason" CHECK ("Reason" BETWEEN 0 AND 3),
        CONSTRAINT "FK_report_archives_reports_ReportId" FOREIGN KEY ("ReportId") REFERENCES reports ("Id") ON DELETE CASCADE
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261119222822_ReportArchive') THEN
    CREATE INDEX "IX_report_archive_events_ReportId_CreatedAt" ON report_archive_events ("ReportId", "CreatedAt");
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261119222822_ReportArchive') THEN
    CREATE INDEX "IX_report_archives_ArchivedAt" ON report_archives ("ArchivedAt");
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261119222822_ReportArchive') THEN
    INSERT INTO "__EFMigrationsHistory" ("MigrationId", "ProductVersion")
    VALUES ('20261119222822_ReportArchive', '11.0.0-rc.3.26522.148');
    END IF;
END $EF$;
COMMIT;

START TRANSACTION;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261120080000_ConfirmationWithdrawals') THEN
    CREATE TABLE report_action_confirmation_withdrawals (
        "Id" uuid NOT NULL,
        "ActionId" uuid NOT NULL,
        "AccountId" uuid,
        "ConfirmedAt" timestamp with time zone NOT NULL,
        "WithdrawnAt" timestamp with time zone NOT NULL,
        CONSTRAINT "PK_report_action_confirmation_withdrawals" PRIMARY KEY ("Id"),
        CONSTRAINT "CK_report_action_confirmation_withdrawals_Order" CHECK ("WithdrawnAt" >= "ConfirmedAt"),
        CONSTRAINT "FK_report_action_confirmation_withdrawals_AspNetUsers_AccountId" FOREIGN KEY ("AccountId") REFERENCES "AspNetUsers" ("Id") ON DELETE SET NULL,
        CONSTRAINT "FK_report_action_confirmation_withdrawals_report_actions_ActionId" FOREIGN KEY ("ActionId") REFERENCES report_actions ("Id") ON DELETE CASCADE
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261120080000_ConfirmationWithdrawals') THEN
    CREATE INDEX "IX_report_action_confirmation_withdrawals_AccountId" ON report_action_confirmation_withdrawals ("AccountId");
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261120080000_ConfirmationWithdrawals') THEN
    CREATE INDEX "IX_report_action_confirmation_withdrawals_ActionId_AccountId" ON report_action_confirmation_withdrawals ("ActionId", "AccountId");
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261120080000_ConfirmationWithdrawals') THEN
    INSERT INTO "__EFMigrationsHistory" ("MigrationId", "ProductVersion")
    VALUES ('20261120080000_ConfirmationWithdrawals', '11.0.0-rc.3.26522.148');
    END IF;
END $EF$;
COMMIT;

START TRANSACTION;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261120090000_Points') THEN
    CREATE TABLE action_hearts (
        "ActionId" uuid NOT NULL,
        "AccountId" uuid NOT NULL,
        "CreatedAt" timestamp with time zone NOT NULL,
        CONSTRAINT "PK_action_hearts" PRIMARY KEY ("ActionId", "AccountId"),
        CONSTRAINT "FK_action_hearts_AspNetUsers_AccountId" FOREIGN KEY ("AccountId") REFERENCES "AspNetUsers" ("Id") ON DELETE CASCADE,
        CONSTRAINT "FK_action_hearts_report_actions_ActionId" FOREIGN KEY ("ActionId") REFERENCES report_actions ("Id") ON DELETE CASCADE
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261120090000_Points') THEN
    CREATE TABLE points_events (
        "Id" uuid NOT NULL,
        "AccountId" uuid NOT NULL,
        "Amount" integer NOT NULL,
        "Reason" integer NOT NULL,
        "ActionId" uuid,
        "ConfirmerId" uuid,
        "TransferId" uuid,
        "ReversesEventId" uuid,
        "ReversalCause" integer,
        "RuleVersion" character varying(32) NOT NULL,
        "CreatedAt" timestamp with time zone NOT NULL,
        CONSTRAINT "PK_points_events" PRIMARY KEY ("Id"),
        CONSTRAINT "CK_points_events_RuleVersion" CHECK (length(trim("RuleVersion")) BETWEEN 1 AND 32),
        CONSTRAINT "CK_points_events_Shape" CHECK (("Reason" = 0 AND "Amount" > 0 AND "ActionId" IS NOT NULL AND "TransferId" IS NULL AND "ReversesEventId" IS NULL AND "ReversalCause" IS NULL) OR ("Reason" = 1 AND "Amount" <> 0 AND "TransferId" IS NULL AND "ConfirmerId" IS NULL AND "ReversesEventId" IS NOT NULL AND "ReversalCause" BETWEEN 0 AND 4) OR ("Reason" = 2 AND "Amount" < 0 AND "ActionId" IS NOT NULL AND "TransferId" IS NOT NULL AND "ConfirmerId" IS NULL AND "ReversesEventId" IS NULL AND "ReversalCause" IS NULL) OR ("Reason" = 3 AND "Amount" > 0 AND "ActionId" IS NOT NULL AND "TransferId" IS NOT NULL AND "ConfirmerId" IS NULL AND "ReversesEventId" IS NULL AND "ReversalCause" IS NULL)),
        CONSTRAINT "FK_points_events_AspNetUsers_AccountId" FOREIGN KEY ("AccountId") REFERENCES "AspNetUsers" ("Id") ON DELETE CASCADE,
        CONSTRAINT "FK_points_events_AspNetUsers_ConfirmerId" FOREIGN KEY ("ConfirmerId") REFERENCES "AspNetUsers" ("Id") ON DELETE SET NULL,
        CONSTRAINT "FK_points_events_points_events_ReversesEventId" FOREIGN KEY ("ReversesEventId") REFERENCES points_events ("Id") ON DELETE CASCADE
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261120090000_Points') THEN
    CREATE INDEX "IX_action_hearts_AccountId" ON action_hearts ("AccountId");
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261120090000_Points') THEN
    CREATE INDEX "IX_points_events_AccountId_CreatedAt" ON points_events ("AccountId", "CreatedAt");
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261120090000_Points') THEN
    CREATE UNIQUE INDEX "IX_points_events_ActionId_ConfirmerId" ON points_events ("ActionId", "ConfirmerId") WHERE "Reason" = 0;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261120090000_Points') THEN
    CREATE INDEX "IX_points_events_ActionId_Reason" ON points_events ("ActionId", "Reason");
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261120090000_Points') THEN
    CREATE INDEX "IX_points_events_ConfirmerId" ON points_events ("ConfirmerId");
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261120090000_Points') THEN
    CREATE UNIQUE INDEX "IX_points_events_ReversesEventId" ON points_events ("ReversesEventId");
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261120090000_Points') THEN
    CREATE UNIQUE INDEX "IX_points_events_TransferId_Reason" ON points_events ("TransferId", "Reason") WHERE "TransferId" IS NOT NULL;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261120090000_Points') THEN
    INSERT INTO "__EFMigrationsHistory" ("MigrationId", "ProductVersion")
    VALUES ('20261120090000_Points', '11.0.0-rc.3.26522.148');
    END IF;
END $EF$;
COMMIT;


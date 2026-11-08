using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace VarPlats.Web.Data.Migrations;

/// <inheritdoc />
public partial class _20261108164953_ReportPhotos : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "report_photos",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                SubmissionId = table.Column<Guid>(type: "uuid", nullable: true),
                ActionId = table.Column<Guid>(type: "uuid", nullable: true),
                StorageName = table.Column<string>(type: "character varying(36)", maxLength: 36, nullable: false),
                ContentType = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                Sha256 = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                ByteLength = table.Column<long>(type: "bigint", nullable: false),
                Width = table.Column<int>(type: "integer", nullable: false),
                Height = table.Column<int>(type: "integer", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_report_photos", x => x.Id);
                table.CheckConstraint("CK_report_photos_ContentType", "(\"ContentType\" = 'image/jpeg' AND \"StorageName\" LIKE '%.jpg') OR (\"ContentType\" = 'image/png' AND \"StorageName\" LIKE '%.png')");
                table.CheckConstraint("CK_report_photos_Parent", "(\"SubmissionId\" IS NOT NULL AND \"ActionId\" IS NULL) OR (\"SubmissionId\" IS NULL AND \"ActionId\" IS NOT NULL)");
                table.CheckConstraint("CK_report_photos_Sha256", "\"Sha256\" ~ '^[0-9a-f]{64}$'");
                table.CheckConstraint("CK_report_photos_Size", "\"ByteLength\" BETWEEN 1 AND 5242880 AND \"Width\" BETWEEN 1 AND 4096 AND \"Height\" BETWEEN 1 AND 4096 AND \"Width\"::bigint * \"Height\" <= 16000000");
                table.CheckConstraint("CK_report_photos_StorageName", "\"StorageName\" ~ '^[0-9a-f]{32}\\.(jpg|png)$'");
                table.ForeignKey(
                    name: "FK_report_photos_report_actions_ActionId",
                    column: x => x.ActionId,
                    principalTable: "report_actions",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Cascade);
                table.ForeignKey(
                    name: "FK_report_photos_report_submissions_SubmissionId",
                    column: x => x.SubmissionId,
                    principalTable: "report_submissions",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateIndex(
            name: "IX_report_photos_ActionId",
            table: "report_photos",
            column: "ActionId",
            unique: true);

        migrationBuilder.CreateIndex(
            name: "IX_report_photos_StorageName",
            table: "report_photos",
            column: "StorageName",
            unique: true);

        migrationBuilder.CreateIndex(
            name: "IX_report_photos_SubmissionId",
            table: "report_photos",
            column: "SubmissionId",
            unique: true);

        migrationBuilder.Sql("""
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
            """);
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
            DROP TRIGGER varplats_guard_photo_approval ON report_submissions;
            DROP FUNCTION varplats_guard_photo_approval();
            """);
        migrationBuilder.DropTable(name: "report_photos");
        migrationBuilder.Sql("DROP FUNCTION varplats_guard_report_photo();");
    }
}

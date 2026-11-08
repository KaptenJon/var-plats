using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace VarPlats.Web.Data.Migrations;

/// <inheritdoc />
public partial class _20261108035738_IdentityAccountActivity : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "IdentityAccountActivity",
            columns: table => new
            {
                UserId = table.Column<Guid>(type: "uuid", nullable: false),
                CreatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "CURRENT_TIMESTAMP"),
                LastSignInAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_IdentityAccountActivity", x => x.UserId);
                table.ForeignKey(
                    name: "FK_IdentityAccountActivity_AspNetUsers_UserId",
                    column: x => x.UserId,
                    principalTable: "AspNetUsers",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateIndex(
            name: "IX_IdentityAccountActivity_CreatedAtUtc",
            table: "IdentityAccountActivity",
            column: "CreatedAtUtc");

        migrationBuilder.CreateIndex(
            name: "IX_IdentityAccountActivity_LastSignInAtUtc",
            table: "IdentityAccountActivity",
            column: "LastSignInAtUtc");

        migrationBuilder.Sql("""
            INSERT INTO public."IdentityAccountActivity" ("UserId", "CreatedAtUtc")
            SELECT "Id", CURRENT_TIMESTAMP
            FROM public."AspNetUsers"
            ON CONFLICT ("UserId") DO NOTHING;
            """);
        migrationBuilder.Sql("""
            CREATE OR REPLACE FUNCTION public.varplats_identity_activity_on_user_created()
            RETURNS trigger
            LANGUAGE plpgsql
            AS $function$
            BEGIN
                INSERT INTO public."IdentityAccountActivity" ("UserId", "CreatedAtUtc")
                VALUES (NEW."Id", CURRENT_TIMESTAMP)
                ON CONFLICT ("UserId") DO NOTHING;
                RETURN NEW;
            END;
            $function$;

            CREATE TRIGGER varplats_identity_activity_after_user_insert
            AFTER INSERT ON public."AspNetUsers"
            FOR EACH ROW
            EXECUTE FUNCTION public.varplats_identity_activity_on_user_created();
            """);
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
            DROP TRIGGER IF EXISTS varplats_identity_activity_after_user_insert
                ON public."AspNetUsers";
            DROP FUNCTION IF EXISTS public.varplats_identity_activity_on_user_created();
            """);
        migrationBuilder.DropTable(
            name: "IdentityAccountActivity");
    }
}

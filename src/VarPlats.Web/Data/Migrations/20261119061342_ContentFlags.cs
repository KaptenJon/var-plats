using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace VarPlats.Web.Data.Migrations;

/// <inheritdoc />
public partial class _20261119061342_ContentFlags : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "content_flags",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                ReportId = table.Column<Guid>(type: "uuid", nullable: false),
                ActionId = table.Column<Guid>(type: "uuid", nullable: true),
                ReporterId = table.Column<Guid>(type: "uuid", nullable: false),
                Reason = table.Column<int>(type: "integer", nullable: false),
                Note = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                DismissedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_content_flags", x => x.Id);
                table.CheckConstraint("CK_content_flags_Note", "\"Note\" IS NULL OR length(trim(\"Note\")) BETWEEN 1 AND 500");
                table.CheckConstraint("CK_content_flags_Reason", "\"Reason\" BETWEEN 0 AND 5");
                table.ForeignKey(
                    name: "FK_content_flags_AspNetUsers_ReporterId",
                    column: x => x.ReporterId,
                    principalTable: "AspNetUsers",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Cascade);
                table.ForeignKey(
                    name: "FK_content_flags_report_actions_ActionId",
                    column: x => x.ActionId,
                    principalTable: "report_actions",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Cascade);
                table.ForeignKey(
                    name: "FK_content_flags_reports_ReportId",
                    column: x => x.ReportId,
                    principalTable: "reports",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateIndex(
            name: "IX_content_flags_ActionId",
            table: "content_flags",
            column: "ActionId");

        migrationBuilder.CreateIndex(
            name: "IX_content_flags_DismissedAt_CreatedAt",
            table: "content_flags",
            columns: new[] { "DismissedAt", "CreatedAt" });

        migrationBuilder.CreateIndex(
            name: "IX_content_flags_ReporterId_CreatedAt",
            table: "content_flags",
            columns: new[] { "ReporterId", "CreatedAt" });

        migrationBuilder.CreateIndex(
            name: "IX_content_flags_ReportId_ActionId_ReporterId",
            table: "content_flags",
            columns: new[] { "ReportId", "ActionId", "ReporterId" },
            unique: true)
            .Annotation("Npgsql:NullsDistinct", false);
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(
            name: "content_flags");
    }
}

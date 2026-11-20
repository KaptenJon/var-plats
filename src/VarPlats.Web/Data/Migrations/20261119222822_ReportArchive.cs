using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace VarPlats.Web.Data.Migrations;

/// <inheritdoc />
public partial class _20261119222822_ReportArchive : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "report_archive_events",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                ReportId = table.Column<Guid>(type: "uuid", nullable: false),
                Kind = table.Column<int>(type: "integer", nullable: false),
                Reason = table.Column<int>(type: "integer", nullable: true),
                Note = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_report_archive_events", x => x.Id);
                table.CheckConstraint("CK_report_archive_events_Kind", "(\"Kind\" = 0 AND \"Reason\" BETWEEN 0 AND 3) OR (\"Kind\" = 1 AND \"Reason\" IS NULL AND \"Note\" IS NULL)");
                table.CheckConstraint("CK_report_archive_events_Note", "\"Note\" IS NULL OR length(trim(\"Note\")) BETWEEN 1 AND 500");
                table.ForeignKey(
                    name: "FK_report_archive_events_reports_ReportId",
                    column: x => x.ReportId,
                    principalTable: "reports",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateTable(
            name: "report_archives",
            columns: table => new
            {
                ReportId = table.Column<Guid>(type: "uuid", nullable: false),
                Reason = table.Column<int>(type: "integer", nullable: false),
                Note = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                ArchivedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_report_archives", x => x.ReportId);
                table.CheckConstraint("CK_report_archives_Note", "\"Note\" IS NULL OR length(trim(\"Note\")) BETWEEN 1 AND 500");
                table.CheckConstraint("CK_report_archives_Reason", "\"Reason\" BETWEEN 0 AND 3");
                table.ForeignKey(
                    name: "FK_report_archives_reports_ReportId",
                    column: x => x.ReportId,
                    principalTable: "reports",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateIndex(
            name: "IX_report_archive_events_ReportId_CreatedAt",
            table: "report_archive_events",
            columns: new[] { "ReportId", "CreatedAt" });

        migrationBuilder.CreateIndex(
            name: "IX_report_archives_ArchivedAt",
            table: "report_archives",
            column: "ArchivedAt");
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(
            name: "report_archive_events");

        migrationBuilder.DropTable(
            name: "report_archives");
    }
}

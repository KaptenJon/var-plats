using System;
using Microsoft.EntityFrameworkCore.Migrations;
using NetTopologySuite.Geometries;

#nullable disable

namespace VarPlats.Web.Data.Migrations;

/// <inheritdoc />
public partial class _20261108154722_ReportReviewWorkflow : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "report_submissions",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                Title = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                Description = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: false),
                Category = table.Column<int>(type: "integer", nullable: false),
                Location = table.Column<Point>(type: "geography (point, 4326)", nullable: false),
                CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                ReviewStatus = table.Column<int>(type: "integer", nullable: false, defaultValue: 0),
                AuthorId = table.Column<Guid>(type: "uuid", nullable: true),
                PublishedReportId = table.Column<Guid>(type: "uuid", nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_report_submissions", x => x.Id);
                table.CheckConstraint("CK_report_submissions_Category", "\"Category\" BETWEEN 0 AND 2");
                table.CheckConstraint("CK_report_submissions_Description", "length(trim(\"Description\")) >= 10");
                table.CheckConstraint("CK_report_submissions_Publication", "(\"ReviewStatus\" = 1 AND \"PublishedReportId\" IS NOT NULL AND \"PublishedReportId\" = \"Id\") OR (\"ReviewStatus\" IN (0, 2) AND \"PublishedReportId\" IS NULL)");
                table.CheckConstraint("CK_report_submissions_ReviewStatus", "\"ReviewStatus\" IN (0, 1, 2)");
                table.CheckConstraint("CK_report_submissions_Title", "length(trim(\"Title\")) >= 5");
                table.ForeignKey(
                    name: "FK_report_submissions_AspNetUsers_AuthorId",
                    column: x => x.AuthorId,
                    principalTable: "AspNetUsers",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.SetNull);
                table.ForeignKey(
                    name: "FK_report_submissions_reports_PublishedReportId",
                    column: x => x.PublishedReportId,
                    principalTable: "reports",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateIndex(
            name: "IX_report_submissions_AuthorId",
            table: "report_submissions",
            column: "AuthorId");

        migrationBuilder.CreateIndex(
            name: "IX_report_submissions_PublishedReportId",
            table: "report_submissions",
            column: "PublishedReportId",
            unique: true);

        migrationBuilder.CreateIndex(
            name: "IX_report_submissions_ReviewStatus_CreatedAt",
            table: "report_submissions",
            columns: new[] { "ReviewStatus", "CreatedAt" });
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(
            name: "report_submissions");
    }
}

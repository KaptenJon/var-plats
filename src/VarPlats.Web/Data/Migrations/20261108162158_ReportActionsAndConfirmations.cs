using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace VarPlats.Web.Data.Migrations;

/// <inheritdoc />
public partial class _20261108162158_ReportActionsAndConfirmations : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "report_actions",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                ReportId = table.Column<Guid>(type: "uuid", nullable: false),
                AuthorId = table.Column<Guid>(type: "uuid", nullable: true),
                Description = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: false),
                CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_report_actions", x => x.Id);
                table.CheckConstraint("CK_report_actions_Description", "length(trim(\"Description\")) >= 10");
                table.ForeignKey(
                    name: "FK_report_actions_AspNetUsers_AuthorId",
                    column: x => x.AuthorId,
                    principalTable: "AspNetUsers",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.SetNull);
                table.ForeignKey(
                    name: "FK_report_actions_reports_ReportId",
                    column: x => x.ReportId,
                    principalTable: "reports",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateTable(
            name: "report_action_confirmations",
            columns: table => new
            {
                ActionId = table.Column<Guid>(type: "uuid", nullable: false),
                AccountId = table.Column<Guid>(type: "uuid", nullable: false),
                CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_report_action_confirmations", x => new { x.ActionId, x.AccountId });
                table.ForeignKey(
                    name: "FK_report_action_confirmations_AspNetUsers_AccountId",
                    column: x => x.AccountId,
                    principalTable: "AspNetUsers",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Cascade);
                table.ForeignKey(
                    name: "FK_report_action_confirmations_report_actions_ActionId",
                    column: x => x.ActionId,
                    principalTable: "report_actions",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateIndex(
            name: "IX_report_action_confirmations_AccountId",
            table: "report_action_confirmations",
            column: "AccountId");

        migrationBuilder.CreateIndex(
            name: "IX_report_actions_AuthorId",
            table: "report_actions",
            column: "AuthorId");

        migrationBuilder.CreateIndex(
            name: "IX_report_actions_ReportId_CreatedAt",
            table: "report_actions",
            columns: new[] { "ReportId", "CreatedAt" });
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(
            name: "report_action_confirmations");

        migrationBuilder.DropTable(
            name: "report_actions");
    }
}

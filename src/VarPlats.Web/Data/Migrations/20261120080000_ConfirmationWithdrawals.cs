using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace VarPlats.Web.Data.Migrations;

/// <inheritdoc />
public partial class _20261120080000_ConfirmationWithdrawals : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "report_action_confirmation_withdrawals",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                ActionId = table.Column<Guid>(type: "uuid", nullable: false),
                AccountId = table.Column<Guid>(type: "uuid", nullable: true),
                ConfirmedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                WithdrawnAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_report_action_confirmation_withdrawals", x => x.Id);
                table.CheckConstraint("CK_report_action_confirmation_withdrawals_Order", "\"WithdrawnAt\" >= \"ConfirmedAt\"");
                table.ForeignKey(
                    name: "FK_report_action_confirmation_withdrawals_AspNetUsers_AccountId",
                    column: x => x.AccountId,
                    principalTable: "AspNetUsers",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.SetNull);
                table.ForeignKey(
                    name: "FK_report_action_confirmation_withdrawals_report_actions_ActionId",
                    column: x => x.ActionId,
                    principalTable: "report_actions",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateIndex(
            name: "IX_report_action_confirmation_withdrawals_AccountId",
            table: "report_action_confirmation_withdrawals",
            column: "AccountId");

        migrationBuilder.CreateIndex(
            name: "IX_report_action_confirmation_withdrawals_ActionId_AccountId",
            table: "report_action_confirmation_withdrawals",
            columns: new[] { "ActionId", "AccountId" });
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(
            name: "report_action_confirmation_withdrawals");
    }
}

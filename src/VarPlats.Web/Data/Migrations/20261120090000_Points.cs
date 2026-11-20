using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace VarPlats.Web.Data.Migrations;

/// <inheritdoc />
public partial class _20261120090000_Points : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "action_hearts",
            columns: table => new
            {
                ActionId = table.Column<Guid>(type: "uuid", nullable: false),
                AccountId = table.Column<Guid>(type: "uuid", nullable: false),
                CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_action_hearts", x => new { x.ActionId, x.AccountId });
                table.ForeignKey(
                    name: "FK_action_hearts_AspNetUsers_AccountId",
                    column: x => x.AccountId,
                    principalTable: "AspNetUsers",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Cascade);
                table.ForeignKey(
                    name: "FK_action_hearts_report_actions_ActionId",
                    column: x => x.ActionId,
                    principalTable: "report_actions",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateTable(
            name: "points_events",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                AccountId = table.Column<Guid>(type: "uuid", nullable: false),
                Amount = table.Column<int>(type: "integer", nullable: false),
                Reason = table.Column<int>(type: "integer", nullable: false),
                ActionId = table.Column<Guid>(type: "uuid", nullable: true),
                ConfirmerId = table.Column<Guid>(type: "uuid", nullable: true),
                TransferId = table.Column<Guid>(type: "uuid", nullable: true),
                ReversesEventId = table.Column<Guid>(type: "uuid", nullable: true),
                ReversalCause = table.Column<int>(type: "integer", nullable: true),
                RuleVersion = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_points_events", x => x.Id);
                table.CheckConstraint("CK_points_events_RuleVersion", "length(trim(\"RuleVersion\")) BETWEEN 1 AND 32");
                table.CheckConstraint("CK_points_events_Shape", "(\"Reason\" = 0 AND \"Amount\" > 0 AND \"ActionId\" IS NOT NULL AND \"TransferId\" IS NULL AND \"ReversesEventId\" IS NULL AND \"ReversalCause\" IS NULL) OR (\"Reason\" = 1 AND \"Amount\" <> 0 AND \"TransferId\" IS NULL AND \"ConfirmerId\" IS NULL AND \"ReversesEventId\" IS NOT NULL AND \"ReversalCause\" BETWEEN 0 AND 4) OR (\"Reason\" = 2 AND \"Amount\" < 0 AND \"ActionId\" IS NOT NULL AND \"TransferId\" IS NOT NULL AND \"ConfirmerId\" IS NULL AND \"ReversesEventId\" IS NULL AND \"ReversalCause\" IS NULL) OR (\"Reason\" = 3 AND \"Amount\" > 0 AND \"ActionId\" IS NOT NULL AND \"TransferId\" IS NOT NULL AND \"ConfirmerId\" IS NULL AND \"ReversesEventId\" IS NULL AND \"ReversalCause\" IS NULL)");
                table.ForeignKey(
                    name: "FK_points_events_AspNetUsers_AccountId",
                    column: x => x.AccountId,
                    principalTable: "AspNetUsers",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Cascade);
                table.ForeignKey(
                    name: "FK_points_events_AspNetUsers_ConfirmerId",
                    column: x => x.ConfirmerId,
                    principalTable: "AspNetUsers",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.SetNull);
                table.ForeignKey(
                    name: "FK_points_events_points_events_ReversesEventId",
                    column: x => x.ReversesEventId,
                    principalTable: "points_events",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateIndex(
            name: "IX_action_hearts_AccountId",
            table: "action_hearts",
            column: "AccountId");

        migrationBuilder.CreateIndex(
            name: "IX_points_events_AccountId_CreatedAt",
            table: "points_events",
            columns: new[] { "AccountId", "CreatedAt" });

        migrationBuilder.CreateIndex(
            name: "IX_points_events_ActionId_ConfirmerId",
            table: "points_events",
            columns: new[] { "ActionId", "ConfirmerId" },
            unique: true,
            filter: "\"Reason\" = 0");

        migrationBuilder.CreateIndex(
            name: "IX_points_events_ActionId_Reason",
            table: "points_events",
            columns: new[] { "ActionId", "Reason" });

        migrationBuilder.CreateIndex(
            name: "IX_points_events_ConfirmerId",
            table: "points_events",
            column: "ConfirmerId");

        migrationBuilder.CreateIndex(
            name: "IX_points_events_ReversesEventId",
            table: "points_events",
            column: "ReversesEventId",
            unique: true);

        migrationBuilder.CreateIndex(
            name: "IX_points_events_TransferId_Reason",
            table: "points_events",
            columns: new[] { "TransferId", "Reason" },
            unique: true,
            filter: "\"TransferId\" IS NOT NULL");
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(
            name: "action_hearts");

        migrationBuilder.DropTable(
            name: "points_events");
    }
}

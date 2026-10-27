using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace HealthTrack.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class PlanCompletionAndInsights : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "PlanItemId",
                table: "Logs",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.AddColumn<DateOnly>(
                name: "ScheduledDate",
                table: "Logs",
                type: "date",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_Profiles_CreatedAtUtc",
                table: "Profiles",
                column: "CreatedAtUtc");

            migrationBuilder.CreateIndex(
                name: "IX_Logs_PlanItemId_ScheduledDate",
                table: "Logs",
                columns: new[] { "PlanItemId", "ScheduledDate" },
                unique: true,
                filter: "[PlanItemId] IS NOT NULL");

            migrationBuilder.AddCheckConstraint(
                name: "CK_Log_PlanOccurrence",
                table: "Logs",
                sql: "([PlanItemId] IS NULL AND [ScheduledDate] IS NULL) OR ([PlanItemId] IS NOT NULL AND [ScheduledDate] IS NOT NULL)");

            migrationBuilder.AddForeignKey(
                name: "FK_Logs_PlanItems_PlanItemId",
                table: "Logs",
                column: "PlanItemId",
                principalTable: "PlanItems",
                principalColumn: "Id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Logs_PlanItems_PlanItemId",
                table: "Logs");

            migrationBuilder.DropIndex(
                name: "IX_Profiles_CreatedAtUtc",
                table: "Profiles");

            migrationBuilder.DropIndex(
                name: "IX_Logs_PlanItemId_ScheduledDate",
                table: "Logs");

            migrationBuilder.DropCheckConstraint(
                name: "CK_Log_PlanOccurrence",
                table: "Logs");

            migrationBuilder.DropColumn(
                name: "PlanItemId",
                table: "Logs");

            migrationBuilder.DropColumn(
                name: "ScheduledDate",
                table: "Logs");
        }
    }
}

using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace HealthTrack.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class DailyWellnessAndPersonalGoals : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "IsFavorite",
                table: "Logs",
                type: "bit",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "SleepStartedAtUtc",
                table: "Logs",
                type: "datetimeoffset",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "WaterMl",
                table: "Logs",
                type: "nvarchar(max)",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "WellnessPreferences",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    UserId = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: false),
                    Focus = table.Column<int>(type: "int", nullable: false),
                    WeeklyWorkoutMinutes = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    DailyWaterMl = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    NightlySleepMinutes = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    DailySteps = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    ShowWater = table.Column<bool>(type: "bit", nullable: false),
                    ShowSleep = table.Column<bool>(type: "bit", nullable: false),
                    ShowNutrition = table.Column<bool>(type: "bit", nullable: false),
                    ShowSteps = table.Column<bool>(type: "bit", nullable: false),
                    SetupCompletedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WellnessPreferences", x => x.Id);
                    table.ForeignKey(
                        name: "FK_WellnessPreferences_AspNetUsers_UserId",
                        column: x => x.UserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateIndex(
                name: "IX_Logs_ClientId_IsFavorite_Kind",
                table: "Logs",
                columns: new[] { "ClientId", "IsFavorite", "Kind" });

            migrationBuilder.CreateIndex(
                name: "IX_WellnessPreferences_UserId",
                table: "WellnessPreferences",
                column: "UserId",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "WellnessPreferences");

            migrationBuilder.DropIndex(
                name: "IX_Logs_ClientId_IsFavorite_Kind",
                table: "Logs");

            migrationBuilder.DropColumn(
                name: "IsFavorite",
                table: "Logs");

            migrationBuilder.DropColumn(
                name: "SleepStartedAtUtc",
                table: "Logs");

            migrationBuilder.DropColumn(
                name: "WaterMl",
                table: "Logs");
        }
    }
}

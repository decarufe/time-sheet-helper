using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TimeSheetHelper.Storage.Migrations
{
    /// <inheritdoc />
    public partial class WeekTaskChoices : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "WeekTaskChoices",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    WeekStart = table.Column<DateOnly>(type: "TEXT", nullable: false),
                    ForecastTaskId = table.Column<int>(type: "INTEGER", nullable: false),
                    Ignore = table.Column<bool>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WeekTaskChoices", x => x.Id);
                    table.ForeignKey(
                        name: "FK_WeekTaskChoices_ForecastTasks_ForecastTaskId",
                        column: x => x.ForecastTaskId,
                        principalTable: "ForecastTasks",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_WeekTaskChoices_ForecastTaskId",
                table: "WeekTaskChoices",
                column: "ForecastTaskId");

            migrationBuilder.CreateIndex(
                name: "IX_WeekTaskChoices_WeekStart_ForecastTaskId",
                table: "WeekTaskChoices",
                columns: new[] { "WeekStart", "ForecastTaskId" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "WeekTaskChoices");
        }
    }
}

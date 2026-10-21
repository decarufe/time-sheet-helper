using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TimeSheetHelper.Storage.Migrations
{
    /// <inheritdoc />
    public partial class AssignmentFeedback : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "AssignmentFeedback",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    GroupKey = table.Column<string>(type: "TEXT", nullable: false),
                    ForecastTaskId = table.Column<int>(type: "INTEGER", nullable: false),
                    Count = table.Column<int>(type: "INTEGER", nullable: false),
                    LastUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AssignmentFeedback", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AssignmentFeedback_ForecastTasks_ForecastTaskId",
                        column: x => x.ForecastTaskId,
                        principalTable: "ForecastTasks",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_AssignmentFeedback_ForecastTaskId",
                table: "AssignmentFeedback",
                column: "ForecastTaskId");

            migrationBuilder.CreateIndex(
                name: "IX_AssignmentFeedback_GroupKey_ForecastTaskId",
                table: "AssignmentFeedback",
                columns: new[] { "GroupKey", "ForecastTaskId" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AssignmentFeedback");
        }
    }
}

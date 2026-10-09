using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TimeSheetHelper.Storage.Migrations
{
    /// <inheritdoc />
    public partial class Initial : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Activities",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Source = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    StartUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    EndUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    Kind = table.Column<string>(type: "TEXT", nullable: true),
                    ProcessName = table.Column<string>(type: "TEXT", nullable: true),
                    Title = table.Column<string>(type: "TEXT", nullable: true),
                    FilePath = table.Column<string>(type: "TEXT", nullable: true),
                    Folder = table.Column<string>(type: "TEXT", nullable: true),
                    Url = table.Column<string>(type: "TEXT", nullable: true),
                    Repo = table.Column<string>(type: "TEXT", nullable: true),
                    Project = table.Column<string>(type: "TEXT", nullable: true),
                    Subject = table.Column<string>(type: "TEXT", nullable: true),
                    Participants = table.Column<string>(type: "TEXT", nullable: true),
                    ExternalId = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Activities", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ClassificationCache",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Fingerprint = table.Column<string>(type: "TEXT", nullable: false),
                    ForecastTaskId = table.Column<int>(type: "INTEGER", nullable: true),
                    Confidence = table.Column<double>(type: "REAL", nullable: false),
                    Reason = table.Column<string>(type: "TEXT", nullable: true),
                    SuggestedRuleJson = table.Column<string>(type: "TEXT", nullable: true),
                    CreatedUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ClassificationCache", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ForecastTasks",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    ExternalId = table.Column<string>(type: "TEXT", nullable: false),
                    ProjectName = table.Column<string>(type: "TEXT", nullable: false),
                    ProjectCode = table.Column<string>(type: "TEXT", nullable: true),
                    ClientName = table.Column<string>(type: "TEXT", nullable: true),
                    TaskName = table.Column<string>(type: "TEXT", nullable: false),
                    IsActive = table.Column<bool>(type: "INTEGER", nullable: false),
                    LastSeenUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ForecastTasks", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "SlotOverrides",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    SlotStartUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    ForecastTaskId = table.Column<int>(type: "INTEGER", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SlotOverrides", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "TimesheetDrafts",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    WeekStart = table.Column<DateOnly>(type: "TEXT", nullable: false),
                    SubmittedUtc = table.Column<DateTime>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TimesheetDrafts", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Rules",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Order = table.Column<int>(type: "INTEGER", nullable: false),
                    Name = table.Column<string>(type: "TEXT", nullable: false),
                    Field = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    Operator = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    Pattern = table.Column<string>(type: "TEXT", nullable: false),
                    ForecastTaskId = table.Column<int>(type: "INTEGER", nullable: false),
                    Enabled = table.Column<bool>(type: "INTEGER", nullable: false),
                    Origin = table.Column<string>(type: "TEXT", nullable: false),
                    CreatedUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Rules", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Rules_ForecastTasks_ForecastTaskId",
                        column: x => x.ForecastTaskId,
                        principalTable: "ForecastTasks",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "TimesheetEntries",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    TimesheetDraftId = table.Column<int>(type: "INTEGER", nullable: false),
                    Date = table.Column<DateOnly>(type: "TEXT", nullable: false),
                    ForecastTaskId = table.Column<int>(type: "INTEGER", nullable: false),
                    Hours = table.Column<double>(type: "REAL", nullable: false),
                    Notes = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TimesheetEntries", x => x.Id);
                    table.ForeignKey(
                        name: "FK_TimesheetEntries_ForecastTasks_ForecastTaskId",
                        column: x => x.ForecastTaskId,
                        principalTable: "ForecastTasks",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_TimesheetEntries_TimesheetDrafts_TimesheetDraftId",
                        column: x => x.TimesheetDraftId,
                        principalTable: "TimesheetDrafts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Activities_Source_ExternalId",
                table: "Activities",
                columns: new[] { "Source", "ExternalId" },
                unique: true,
                filter: "ExternalId IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_Activities_StartUtc",
                table: "Activities",
                column: "StartUtc");

            migrationBuilder.CreateIndex(
                name: "IX_ClassificationCache_Fingerprint",
                table: "ClassificationCache",
                column: "Fingerprint",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ForecastTasks_ExternalId",
                table: "ForecastTasks",
                column: "ExternalId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Rules_ForecastTaskId",
                table: "Rules",
                column: "ForecastTaskId");

            migrationBuilder.CreateIndex(
                name: "IX_SlotOverrides_SlotStartUtc",
                table: "SlotOverrides",
                column: "SlotStartUtc",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_TimesheetDrafts_WeekStart",
                table: "TimesheetDrafts",
                column: "WeekStart",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_TimesheetEntries_ForecastTaskId",
                table: "TimesheetEntries",
                column: "ForecastTaskId");

            migrationBuilder.CreateIndex(
                name: "IX_TimesheetEntries_TimesheetDraftId",
                table: "TimesheetEntries",
                column: "TimesheetDraftId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "Activities");

            migrationBuilder.DropTable(
                name: "ClassificationCache");

            migrationBuilder.DropTable(
                name: "Rules");

            migrationBuilder.DropTable(
                name: "SlotOverrides");

            migrationBuilder.DropTable(
                name: "TimesheetEntries");

            migrationBuilder.DropTable(
                name: "ForecastTasks");

            migrationBuilder.DropTable(
                name: "TimesheetDrafts");
        }
    }
}

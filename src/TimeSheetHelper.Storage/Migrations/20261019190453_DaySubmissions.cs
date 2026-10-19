using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TimeSheetHelper.Storage.Migrations
{
    /// <inheritdoc />
    public partial class DaySubmissions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "DaySubmissions",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    WeekStart = table.Column<DateOnly>(type: "TEXT", nullable: false),
                    Date = table.Column<DateOnly>(type: "TEXT", nullable: false),
                    Status = table.Column<int>(type: "INTEGER", nullable: false),
                    Fingerprint = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    SentUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    InProgressSinceUtc = table.Column<DateTime>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DaySubmissions", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "DaySubmissionCells",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    DaySubmissionId = table.Column<int>(type: "INTEGER", nullable: false),
                    ForecastTaskId = table.Column<int>(type: "INTEGER", nullable: false),
                    SentHours = table.Column<double>(type: "REAL", nullable: false),
                    ForecastEntryId = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DaySubmissionCells", x => x.Id);
                    table.ForeignKey(
                        name: "FK_DaySubmissionCells_DaySubmissions_DaySubmissionId",
                        column: x => x.DaySubmissionId,
                        principalTable: "DaySubmissions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "SubmissionJournal",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    DaySubmissionId = table.Column<int>(type: "INTEGER", nullable: false),
                    TimestampUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    Kind = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    Outcome = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    ForecastTaskId = table.Column<int>(type: "INTEGER", nullable: true),
                    Hours = table.Column<double>(type: "REAL", nullable: true),
                    Message = table.Column<string>(type: "TEXT", maxLength: 500, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SubmissionJournal", x => x.Id);
                    table.ForeignKey(
                        name: "FK_SubmissionJournal_DaySubmissions_DaySubmissionId",
                        column: x => x.DaySubmissionId,
                        principalTable: "DaySubmissions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_DaySubmissionCells_DaySubmissionId_ForecastTaskId",
                table: "DaySubmissionCells",
                columns: new[] { "DaySubmissionId", "ForecastTaskId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_DaySubmissions_WeekStart_Date",
                table: "DaySubmissions",
                columns: new[] { "WeekStart", "Date" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_SubmissionJournal_DaySubmissionId",
                table: "SubmissionJournal",
                column: "DaySubmissionId");

            // Every day of an already submitted week was sent, but what was sent is unknown: never propose it again as new.
            migrationBuilder.Sql(
                """
                INSERT INTO DaySubmissions (WeekStart, Date, Status, Fingerprint, SentUtc, InProgressSinceUtc)
                SELECT DISTINCT d.WeekStart, e.Date, 3, NULL, d.SubmittedUtc, NULL
                FROM TimesheetDrafts d
                JOIN TimesheetEntries e ON e.TimesheetDraftId = d.Id
                WHERE d.SubmittedUtc IS NOT NULL;
                """);

            migrationBuilder.Sql(
                """
                INSERT INTO SubmissionJournal (DaySubmissionId, TimestampUtc, Kind, Outcome, ForecastTaskId, Hours, Message)
                SELECT Id, strftime('%Y-%m-%d %H:%M:%S', 'now'), 'Migrated', 'Unknown', NULL, NULL,
                       'Jour marqué « Envoyé (empreinte inconnue) » d''après la soumission de la semaine.'
                FROM DaySubmissions;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "DaySubmissionCells");

            migrationBuilder.DropTable(
                name: "SubmissionJournal");

            migrationBuilder.DropTable(
                name: "DaySubmissions");
        }
    }
}

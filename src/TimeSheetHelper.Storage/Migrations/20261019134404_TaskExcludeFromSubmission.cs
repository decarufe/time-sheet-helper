using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TimeSheetHelper.Storage.Migrations
{
    /// <inheritdoc />
    public partial class TaskExcludeFromSubmission : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "ExcludeFromSubmission",
                table: "ForecastTasks",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ExcludeFromSubmission",
                table: "ForecastTasks");
        }
    }
}

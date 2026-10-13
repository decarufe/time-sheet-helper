using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TimeSheetHelper.Storage.Migrations
{
    /// <inheritdoc />
    public partial class SlotOverrideNotes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Notes",
                table: "SlotOverrides",
                type: "TEXT",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Notes",
                table: "SlotOverrides");
        }
    }
}

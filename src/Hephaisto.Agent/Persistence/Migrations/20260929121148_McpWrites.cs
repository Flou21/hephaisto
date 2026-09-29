using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Hephaisto.Agent.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class McpWrites : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "acknowledged_claimed_by",
                table: "incidents",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "assigned_claimed_by",
                table: "incidents",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "closed_claimed_by",
                table: "incidents",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "relayed_by_agent",
                table: "alert_note_entries",
                type: "boolean",
                nullable: false,
                defaultValue: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "acknowledged_claimed_by",
                table: "incidents");

            migrationBuilder.DropColumn(
                name: "assigned_claimed_by",
                table: "incidents");

            migrationBuilder.DropColumn(
                name: "closed_claimed_by",
                table: "incidents");

            migrationBuilder.DropColumn(
                name: "relayed_by_agent",
                table: "alert_note_entries");
        }
    }
}

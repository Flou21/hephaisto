using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Hephaisto.Agent.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class IncidentLabelsAndRecipients : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "recipients",
                table: "notification_deliveries",
                type: "jsonb",
                nullable: false,
                defaultValueSql: "'[]'::jsonb");

            migrationBuilder.AddColumn<string>(
                name: "routes",
                table: "notification_deliveries",
                type: "jsonb",
                nullable: false,
                defaultValueSql: "'[]'::jsonb");

            migrationBuilder.AddColumn<bool>(
                name: "uses_channel_recipients",
                table: "notification_deliveries",
                type: "boolean",
                nullable: false,
                defaultValue: true);

            migrationBuilder.AddColumn<string>(
                name: "alert_name",
                table: "incidents",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "labels",
                table: "incidents",
                type: "jsonb",
                nullable: false,
                defaultValueSql: "'{}'::jsonb");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "recipients",
                table: "notification_deliveries");

            migrationBuilder.DropColumn(
                name: "routes",
                table: "notification_deliveries");

            migrationBuilder.DropColumn(
                name: "uses_channel_recipients",
                table: "notification_deliveries");

            migrationBuilder.DropColumn(
                name: "alert_name",
                table: "incidents");

            migrationBuilder.DropColumn(
                name: "labels",
                table: "incidents");
        }
    }
}

using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Hephaisto.Agent.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AlertIdentity : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_signals_incident_id",
                table: "signals");

            migrationBuilder.AddColumn<string>(
                name: "alert_key",
                table: "signals",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "status",
                table: "signals",
                type: "text",
                nullable: false,
                defaultValue: "Firing");

            migrationBuilder.AddColumn<string>(
                name: "target_cluster",
                table: "signals",
                type: "text",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "target_cluster",
                table: "incidents",
                type: "text",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "target_cluster",
                table: "agent_actions",
                type: "text",
                nullable: false,
                defaultValue: "");

            migrationBuilder.CreateIndex(
                name: "ix_signals_incident_id_alert_key",
                table: "signals",
                columns: new[] { "incident_id", "alert_key" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_signals_incident_id_alert_key",
                table: "signals");

            migrationBuilder.DropColumn(
                name: "alert_key",
                table: "signals");

            migrationBuilder.DropColumn(
                name: "status",
                table: "signals");

            migrationBuilder.DropColumn(
                name: "target_cluster",
                table: "signals");

            migrationBuilder.DropColumn(
                name: "target_cluster",
                table: "incidents");

            migrationBuilder.DropColumn(
                name: "target_cluster",
                table: "agent_actions");

            migrationBuilder.CreateIndex(
                name: "ix_signals_incident_id",
                table: "signals",
                column: "incident_id");
        }
    }
}

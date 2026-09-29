using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Hephaisto.Agent.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class McpReadSurface : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Incidents opened before alert_name existed have none, so "how often did this
            // alert fire" would count only the recent ones. Filled from the incident's first
            // Alertmanager signal, whose reason is the alertname - the same rule IncidentTriage
            // applies to a new incident. A Kubernetes watch's incident stays without one.
            migrationBuilder.Sql("""
                UPDATE incidents AS i
                SET alert_name = s.reason
                FROM (
                    SELECT DISTINCT ON (incident_id) incident_id, reason
                    FROM signals
                    WHERE source = 'Alertmanager' AND incident_id IS NOT NULL AND reason <> ''
                    ORDER BY incident_id, first_seen
                ) AS s
                WHERE i.id = s.incident_id AND i.alert_name IS NULL;
                """);

            migrationBuilder.CreateIndex(
                name: "ix_incidents_alert_name_opened_at",
                table: "incidents",
                columns: new[] { "alert_name", "opened_at" },
                descending: new[] { false, true },
                filter: "alert_name IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_incidents_alert_name_opened_at",
                table: "incidents");
        }
    }
}

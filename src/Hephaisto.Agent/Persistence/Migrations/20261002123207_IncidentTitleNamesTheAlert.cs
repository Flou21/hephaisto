using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Hephaisto.Agent.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class IncidentTitleNamesTheAlert : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Titles are written once, when the incident opens. Without this the incidents already
            // on the board keep reading "Unknown on ..." after the agent stopped writing it.
            migrationBuilder.Sql(
                """
                UPDATE incidents
                SET title = alert_name || substr(title, length('Unknown') + 1)
                WHERE kind = 'Unknown'
                  AND alert_name IS NOT NULL
                  AND alert_name <> ''
                  AND title LIKE 'Unknown on %';
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Nothing to undo: the old title said less, and no schema changed.
        }
    }
}

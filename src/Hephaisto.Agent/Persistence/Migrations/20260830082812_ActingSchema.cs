using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Hephaisto.Agent.Persistence.Migrations
{
    /// <summary>
    /// The schema changes v0.2.0 needs to start acting: one column that outlived its meaning,
    /// and one column whose values were lying.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>agent_mode.mode is dropped.</b> The database arm of the kill switch used to declare
    /// it, and InitialCreate seeds it to <c>Observe</c> - and the resolver takes the minimum
    /// over every arm that speaks. So <c>mode: Auto</c> in the chart resolved to Observe on
    /// every database that had ever been migrated, and the only way to lift it was a
    /// hand-written UPDATE. The arm is now silent unless the runaway latch is set, and the
    /// mode belongs to the Helm values, so the column has no reader and no writer left. It
    /// goes rather than sitting there looking like a control.
    /// </para>
    /// <para>
    /// <b>approval_source is corrected in place.</b> Its enum's zero value was <c>Ui</c> and
    /// nothing sets the field on the denial path, so every action the policy engine refused
    /// was recorded as though a human had typed a name into the console. <c>approved_by</c>
    /// was correctly null on those same rows, so nothing was ever unsafe - it was the audit
    /// trail asserting something untrue, which is the one place where misleading is worse
    /// than absent. The enum gains <c>NotApplicable = 0</c> and the existing rows move onto
    /// it. A data fix rather than a schema change: the column is text and enums are stored by
    /// name, so no value's meaning shifts underneath the rows.
    /// </para>
    /// </remarks>
    public partial class ActingSchema : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "mode",
                table: "agent_mode");

            // Only rows that nobody approved. Scoped by approved_by rather than by state, so
            // an action a human really did approve through the UI keeps saying Ui even if it
            // has since failed or been rolled back.
            migrationBuilder.Sql(
                """
                UPDATE agent_actions
                   SET approval_source = 'NotApplicable'
                 WHERE approval_source = 'Ui'
                   AND (approved_by IS NULL OR approved_by = '');
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "mode",
                table: "agent_mode",
                type: "text",
                nullable: false,
                defaultValue: "");

            migrationBuilder.UpdateData(
                table: "agent_mode",
                keyColumn: "id",
                keyValue: "singleton",
                column: "mode",
                value: "Observe");

            // Back to the old vocabulary, which had no way to say "nobody". Ui is where
            // these rows came from and where a downgrade has to put them.
            migrationBuilder.Sql(
                """
                UPDATE agent_actions
                   SET approval_source = 'Ui'
                 WHERE approval_source = 'NotApplicable';
                """);
        }
    }
}

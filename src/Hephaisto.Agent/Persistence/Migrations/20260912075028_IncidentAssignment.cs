using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Hephaisto.Agent.Persistence.Migrations
{
    /// <summary>
    /// Assignment: whose job an incident is (backlog #112).
    /// </summary>
    /// <remarks>
    /// <para>
    /// Additive and nullable, so every existing incident reads as unassigned - which is exactly
    /// true, and is also the state an incident returns to when it is handed back to the pool.
    /// </para>
    /// <para>
    /// <b>An index on <c>assigned_to</c>, unlike the acknowledgement columns.</b> The console's
    /// "mine" filter queries on it on every page load, whereas <c>acknowledged_by</c> is only
    /// ever read back on a single incident. An unindexed filter is fine at a hundred incidents
    /// and is a sequential scan at a hundred thousand, which is the count an install that never
    /// prunes reaches - see #109 for why that used to be the only direction available.
    /// </para>
    /// </remarks>
    public partial class IncidentAssignment : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "assigned_at",
                table: "incidents",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "assigned_by",
                table: "incidents",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "assigned_to",
                table: "incidents",
                type: "text",
                nullable: true);

            // Filtered: the query is always "assigned to somebody", never "assigned to nobody",
            // so the null rows are dead weight - and on a mostly-unassigned table that is most
            // of them.
            migrationBuilder.CreateIndex(
                name: "ix_incidents_assigned_to",
                table: "incidents",
                column: "assigned_to",
                filter: "assigned_to IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(name: "ix_incidents_assigned_to", table: "incidents");

            migrationBuilder.DropColumn(
                name: "assigned_at",
                table: "incidents");

            migrationBuilder.DropColumn(
                name: "assigned_by",
                table: "incidents");

            migrationBuilder.DropColumn(
                name: "assigned_to",
                table: "incidents");
        }
    }
}

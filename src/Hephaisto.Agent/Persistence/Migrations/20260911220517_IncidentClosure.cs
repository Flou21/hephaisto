using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Hephaisto.Agent.Persistence.Migrations
{
    /// <summary>
    /// The four columns behind <c>IncidentState.Closed</c> and acknowledgement (backlog #109).
    /// </summary>
    /// <remarks>
    /// <para>
    /// Additive and all nullable, so it is safe on a database with history: every existing
    /// incident reads as never acknowledged and never closed, which is exactly true.
    /// </para>
    /// <para>
    /// No data migration and no backfill. It is tempting to sweep the escalated incidents that
    /// accumulated before <c>Closed</c> existed into it, and it would be wrong: nobody looked at
    /// them, so marking them "a human dealt with it" would write a fact that never happened into
    /// the one table the audit story rests on. The sweeper introduced alongside this expires them
    /// instead, which is what actually happened to them.
    /// </para>
    /// <para>
    /// The grants in <c>InitialCreate</c> are table-level, so new columns on <c>incidents</c>
    /// inherit them and the audit-immutability block on <c>audit_events</c> is untouched.
    /// </para>
    /// </remarks>
    public partial class IncidentClosure : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "acknowledged_at",
                table: "incidents",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "acknowledged_by",
                table: "incidents",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "closed_at",
                table: "incidents",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "closed_by",
                table: "incidents",
                type: "text",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "acknowledged_at",
                table: "incidents");

            migrationBuilder.DropColumn(
                name: "acknowledged_by",
                table: "incidents");

            migrationBuilder.DropColumn(
                name: "closed_at",
                table: "incidents");

            migrationBuilder.DropColumn(
                name: "closed_by",
                table: "incidents");
        }
    }
}

using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Hephaisto.Agent.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class WorkloadQuarantine : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "quarantine_reason",
                table: "workload_action_locks",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "quarantined_until",
                table: "workload_action_locks",
                type: "timestamp with time zone",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "quarantine_reason",
                table: "workload_action_locks");

            migrationBuilder.DropColumn(
                name: "quarantined_until",
                table: "workload_action_locks");
        }
    }
}

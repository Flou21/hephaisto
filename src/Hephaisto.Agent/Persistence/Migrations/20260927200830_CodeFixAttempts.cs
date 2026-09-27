using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Hephaisto.Agent.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class CodeFixAttempts : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "code_fix_attempt_id",
                table: "llm_usage",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "code_fix_attempts",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    incident_id = table.Column<Guid>(type: "uuid", nullable: false),
                    investigation_id = table.Column<Guid>(type: "uuid", nullable: true),
                    state = table.Column<string>(type: "text", nullable: false),
                    workload = table.Column<string>(type: "text", nullable: false),
                    repository_url = table.Column<string>(type: "text", nullable: false),
                    default_branch = table.Column<string>(type: "text", nullable: false),
                    branch = table.Column<string>(type: "text", nullable: false),
                    requested_by = table.Column<string>(type: "text", nullable: false),
                    request_json = table.Column<string>(type: "jsonb", nullable: true),
                    plan_result_json = table.Column<string>(type: "jsonb", nullable: true),
                    implement_result_json = table.Column<string>(type: "jsonb", nullable: true),
                    summary = table.Column<string>(type: "text", nullable: true),
                    root_cause = table.Column<string>(type: "text", nullable: true),
                    confidence = table.Column<double>(type: "double precision", nullable: true),
                    verification_level = table.Column<string>(type: "text", nullable: true),
                    needs_cait = table.Column<bool>(type: "boolean", nullable: false),
                    analysed_ref = table.Column<string>(type: "text", nullable: true),
                    context_sha = table.Column<string>(type: "text", nullable: true),
                    plan_job_name = table.Column<string>(type: "text", nullable: true),
                    implement_job_name = table.Column<string>(type: "text", nullable: true),
                    plan_session_id = table.Column<string>(type: "text", nullable: true),
                    implement_session_id = table.Column<string>(type: "text", nullable: true),
                    plan_cost_usd = table.Column<decimal>(type: "numeric(14,6)", nullable: false),
                    implement_cost_usd = table.Column<decimal>(type: "numeric(14,6)", nullable: false),
                    pr_url = table.Column<string>(type: "text", nullable: true),
                    pr_number = table.Column<int>(type: "integer", nullable: true),
                    approved_by = table.Column<string>(type: "text", nullable: true),
                    approval_source = table.Column<string>(type: "text", nullable: false),
                    failure_reason = table.Column<string>(type: "text", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    plan_started_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    plan_ready_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    decided_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    implement_started_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    finished_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    trace_id = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_code_fix_attempts", x => x.id);
                    table.ForeignKey(
                        name: "fk_code_fix_attempts_incidents_incident_id",
                        column: x => x.incident_id,
                        principalTable: "incidents",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_code_fix_attempts_incident_id",
                table: "code_fix_attempts",
                column: "incident_id");

            migrationBuilder.CreateIndex(
                name: "ix_code_fix_attempts_repository_url_created_at",
                table: "code_fix_attempts",
                columns: new[] { "repository_url", "created_at" });

            migrationBuilder.CreateIndex(
                name: "ix_code_fix_attempts_state_created_at",
                table: "code_fix_attempts",
                columns: new[] { "state", "created_at" });

            migrationBuilder.CreateIndex(
                name: "ux_code_fix_attempts_one_open_per_incident",
                table: "code_fix_attempts",
                column: "incident_id",
                unique: true,
                filter: "state IN ('Eligible', 'Planning', 'PlanReady', 'Implementing')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "code_fix_attempts");

            migrationBuilder.DropColumn(
                name: "code_fix_attempt_id",
                table: "llm_usage");
        }
    }
}

using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Pgvector;

#nullable disable

namespace Watchtower.Agent.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class InitialCreate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // pgvector must exist before the CREATE TABLE that declares a vector(768)
            // column. EF emits it from the model annotation as well; the explicit statement
            // is here so a reader of the migration does not have to know that.
            migrationBuilder.Sql("CREATE EXTENSION IF NOT EXISTS vector;");

            migrationBuilder.AlterDatabase()
                .Annotation("Npgsql:PostgresExtension:vector", ",,");

            migrationBuilder.CreateTable(
                name: "agent_mode",
                columns: table => new
                {
                    id = table.Column<string>(type: "text", nullable: false),
                    mode = table.Column<string>(type: "text", nullable: false),
                    runaway_latched = table.Column<bool>(type: "boolean", nullable: false),
                    latch_reason = table.Column<string>(type: "text", nullable: true),
                    latched_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    changed_by = table.Column<string>(type: "text", nullable: true),
                    changed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_agent_mode", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "audit_events",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    type = table.Column<string>(type: "text", nullable: false),
                    incident_id = table.Column<Guid>(type: "uuid", nullable: true),
                    investigation_id = table.Column<Guid>(type: "uuid", nullable: true),
                    action_id = table.Column<Guid>(type: "uuid", nullable: true),
                    actor = table.Column<string>(type: "text", nullable: false),
                    summary = table.Column<string>(type: "text", nullable: false),
                    detail = table.Column<string>(type: "jsonb", nullable: true),
                    trace_id = table.Column<string>(type: "text", nullable: true),
                    span_id = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_audit_events", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "incidents",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    correlation_key = table.Column<string>(type: "text", nullable: false),
                    title = table.Column<string>(type: "text", nullable: false),
                    kind = table.Column<string>(type: "text", nullable: false),
                    severity = table.Column<string>(type: "text", nullable: false),
                    state = table.Column<string>(type: "text", nullable: false),
                    suppression_reason = table.Column<string>(type: "text", nullable: false),
                    escalation_reason = table.Column<string>(type: "text", nullable: false),
                    target_namespace = table.Column<string>(type: "text", nullable: false),
                    target_kind = table.Column<string>(type: "text", nullable: false),
                    target_name = table.Column<string>(type: "text", nullable: false),
                    target_uid = table.Column<string>(type: "text", nullable: true),
                    target_owner_kind = table.Column<string>(type: "text", nullable: true),
                    target_owner_name = table.Column<string>(type: "text", nullable: true),
                    target_node_name = table.Column<string>(type: "text", nullable: true),
                    mode = table.Column<string>(type: "text", nullable: false),
                    opened_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    last_signal_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    resolved_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    quarantined_until = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    resolution = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_incidents", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "llm_budget_breaches",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    hour_bucket = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    kind = table.Column<string>(type: "text", nullable: false),
                    at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    detail = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_llm_budget_breaches", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "llm_usage",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    incident_id = table.Column<Guid>(type: "uuid", nullable: false),
                    investigation_id = table.Column<Guid>(type: "uuid", nullable: true),
                    at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    input_tokens = table.Column<long>(type: "bigint", nullable: false),
                    output_tokens = table.Column<long>(type: "bigint", nullable: false),
                    cost_usd = table.Column<decimal>(type: "numeric(14,6)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_llm_usage", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "workload_action_locks",
                columns: table => new
                {
                    workload_key = table.Column<string>(type: "text", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_workload_action_locks", x => x.workload_key);
                });

            migrationBuilder.CreateTable(
                name: "human_feedback",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    incident_id = table.Column<Guid>(type: "uuid", nullable: false),
                    helpful = table.Column<bool>(type: "boolean", nullable: false),
                    root_cause_correct = table.Column<bool>(type: "boolean", nullable: true),
                    false_positive = table.Column<bool>(type: "boolean", nullable: false),
                    comment = table.Column<string>(type: "text", nullable: true),
                    submitted_by = table.Column<string>(type: "text", nullable: false),
                    at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_human_feedback", x => x.id);
                    table.ForeignKey(
                        name: "fk_human_feedback_incidents_incident_id",
                        column: x => x.incident_id,
                        principalTable: "incidents",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "incident_digests",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    incident_id = table.Column<Guid>(type: "uuid", nullable: false),
                    digest = table.Column<string>(type: "text", nullable: false),
                    digest_hash = table.Column<string>(type: "text", nullable: false),
                    embedding = table.Column<Vector>(type: "vector(768)", nullable: true),
                    @namespace = table.Column<string>(name: "namespace", type: "text", nullable: false),
                    workload_key = table.Column<string>(type: "text", nullable: false),
                    kind = table.Column<string>(type: "text", nullable: false),
                    resolved = table.Column<bool>(type: "boolean", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_incident_digests", x => x.id);
                    table.ForeignKey(
                        name: "fk_incident_digests_incidents_incident_id",
                        column: x => x.incident_id,
                        principalTable: "incidents",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "incident_events",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    incident_id = table.Column<Guid>(type: "uuid", nullable: false),
                    from = table.Column<string>(type: "text", nullable: true),
                    to = table.Column<string>(type: "text", nullable: false),
                    reason = table.Column<string>(type: "text", nullable: false),
                    at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    trace_id = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_incident_events", x => x.id);
                    table.ForeignKey(
                        name: "fk_incident_events_incidents_incident_id",
                        column: x => x.incident_id,
                        principalTable: "incidents",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "investigations",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    incident_id = table.Column<Guid>(type: "uuid", nullable: false),
                    trace_id = table.Column<string>(type: "text", nullable: true),
                    model_id = table.Column<string>(type: "text", nullable: false),
                    started_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    completed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    termination_reason = table.Column<string>(type: "text", nullable: false),
                    steps_used = table.Column<int>(type: "integer", nullable: false),
                    tool_calls_used = table.Column<int>(type: "integer", nullable: false),
                    input_tokens = table.Column<long>(type: "bigint", nullable: false),
                    output_tokens = table.Column<long>(type: "bigint", nullable: false),
                    cost_usd = table.Column<decimal>(type: "numeric(14,6)", nullable: false),
                    confidence = table.Column<double>(type: "double precision", nullable: true),
                    error = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_investigations", x => x.id);
                    table.ForeignKey(
                        name: "fk_investigations_incidents_incident_id",
                        column: x => x.incident_id,
                        principalTable: "incidents",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "signals",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    fingerprint = table.Column<string>(type: "text", nullable: false),
                    source = table.Column<string>(type: "text", nullable: false),
                    kind = table.Column<string>(type: "text", nullable: false),
                    target_namespace = table.Column<string>(type: "text", nullable: false),
                    target_kind = table.Column<string>(type: "text", nullable: false),
                    target_name = table.Column<string>(type: "text", nullable: false),
                    target_uid = table.Column<string>(type: "text", nullable: true),
                    target_owner_kind = table.Column<string>(type: "text", nullable: true),
                    target_owner_name = table.Column<string>(type: "text", nullable: true),
                    target_node_name = table.Column<string>(type: "text", nullable: true),
                    severity = table.Column<string>(type: "text", nullable: false),
                    reason = table.Column<string>(type: "text", nullable: false),
                    message = table.Column<string>(type: "text", nullable: false),
                    first_seen = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    last_seen = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    count = table.Column<int>(type: "integer", nullable: false),
                    labels = table.Column<string>(type: "jsonb", nullable: false),
                    raw_payload = table.Column<string>(type: "jsonb", nullable: true),
                    incident_id = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_signals", x => x.id);
                    table.ForeignKey(
                        name: "fk_signals_incidents_incident_id",
                        column: x => x.incident_id,
                        principalTable: "incidents",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "action_plans",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    investigation_id = table.Column<Guid>(type: "uuid", nullable: false),
                    summary = table.Column<string>(type: "text", nullable: false),
                    no_action_required = table.Column<bool>(type: "boolean", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_action_plans", x => x.id);
                    table.ForeignKey(
                        name: "fk_action_plans_investigations_investigation_id",
                        column: x => x.investigation_id,
                        principalTable: "investigations",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "evidence_blobs",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    investigation_id = table.Column<Guid>(type: "uuid", nullable: false),
                    content_type = table.Column<string>(type: "text", nullable: false),
                    content = table.Column<string>(type: "text", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    expires_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_evidence_blobs", x => x.id);
                    table.ForeignKey(
                        name: "fk_evidence_blobs_investigations_investigation_id",
                        column: x => x.investigation_id,
                        principalTable: "investigations",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "findings",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    investigation_id = table.Column<Guid>(type: "uuid", nullable: false),
                    category = table.Column<string>(type: "text", nullable: false),
                    hypothesis = table.Column<string>(type: "text", nullable: false),
                    confidence = table.Column<double>(type: "double precision", nullable: false),
                    is_primary = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_findings", x => x.id);
                    table.ForeignKey(
                        name: "fk_findings_investigations_investigation_id",
                        column: x => x.investigation_id,
                        principalTable: "investigations",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "investigation_steps",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    investigation_id = table.Column<Guid>(type: "uuid", nullable: false),
                    ordinal = table.Column<int>(type: "integer", nullable: false),
                    kind = table.Column<string>(type: "text", nullable: false),
                    tool_name = table.Column<string>(type: "text", nullable: true),
                    tool_server = table.Column<string>(type: "text", nullable: true),
                    arguments = table.Column<string>(type: "jsonb", nullable: true),
                    result_digest = table.Column<string>(type: "text", nullable: true),
                    raw_blob_id = table.Column<Guid>(type: "uuid", nullable: true),
                    result_truncated = table.Column<bool>(type: "boolean", nullable: false),
                    result_bytes = table.Column<int>(type: "integer", nullable: false),
                    duration_ms = table.Column<long>(type: "bigint", nullable: false),
                    input_tokens = table.Column<long>(type: "bigint", nullable: false),
                    output_tokens = table.Column<long>(type: "bigint", nullable: false),
                    cost_usd = table.Column<decimal>(type: "numeric(14,6)", nullable: false),
                    failed = table.Column<bool>(type: "boolean", nullable: false),
                    error = table.Column<string>(type: "text", nullable: true),
                    at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_investigation_steps", x => x.id);
                    table.ForeignKey(
                        name: "fk_investigation_steps_investigations_investigation_id",
                        column: x => x.investigation_id,
                        principalTable: "investigations",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "agent_actions",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    incident_id = table.Column<Guid>(type: "uuid", nullable: false),
                    action_plan_id = table.Column<Guid>(type: "uuid", nullable: true),
                    type = table.Column<string>(type: "text", nullable: false),
                    target_namespace = table.Column<string>(type: "text", nullable: false),
                    target_kind = table.Column<string>(type: "text", nullable: false),
                    target_name = table.Column<string>(type: "text", nullable: false),
                    target_uid = table.Column<string>(type: "text", nullable: true),
                    target_owner_kind = table.Column<string>(type: "text", nullable: true),
                    target_owner_name = table.Column<string>(type: "text", nullable: true),
                    target_node_name = table.Column<string>(type: "text", nullable: true),
                    arguments = table.Column<string>(type: "jsonb", nullable: true),
                    risk = table.Column<string>(type: "text", nullable: false),
                    state = table.Column<string>(type: "text", nullable: false),
                    predicted_effect = table.Column<string>(type: "text", nullable: true),
                    rollback_spec = table.Column<string>(type: "jsonb", nullable: true),
                    evidence_finding_ids = table.Column<string>(type: "jsonb", nullable: false),
                    decision = table.Column<string>(type: "text", nullable: false),
                    decision_reasons = table.Column<string>(type: "jsonb", nullable: false),
                    approved_by = table.Column<string>(type: "text", nullable: true),
                    approved_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    approval_reason = table.Column<string>(type: "text", nullable: true),
                    approval_source = table.Column<string>(type: "text", nullable: false),
                    dry_run = table.Column<bool>(type: "boolean", nullable: false),
                    mode_at_execution = table.Column<string>(type: "text", nullable: false),
                    pre_state = table.Column<string>(type: "jsonb", nullable: true),
                    post_state = table.Column<string>(type: "jsonb", nullable: true),
                    executed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    outcome = table.Column<string>(type: "text", nullable: true),
                    error = table.Column<string>(type: "text", nullable: true),
                    is_rollback_of = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_agent_actions", x => x.id);
                    table.ForeignKey(
                        name: "fk_agent_actions_action_plans_action_plan_id",
                        column: x => x.action_plan_id,
                        principalTable: "action_plans",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "fk_agent_actions_incidents_incident_id",
                        column: x => x.incident_id,
                        principalTable: "incidents",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "evidence",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    finding_id = table.Column<Guid>(type: "uuid", nullable: false),
                    step_id = table.Column<Guid>(type: "uuid", nullable: false),
                    excerpt = table.Column<string>(type: "text", nullable: false),
                    source_uri = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_evidence", x => x.id);
                    table.ForeignKey(
                        name: "fk_evidence_findings_finding_id",
                        column: x => x.finding_id,
                        principalTable: "findings",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_evidence_investigation_steps_step_id",
                        column: x => x.step_id,
                        principalTable: "investigation_steps",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "verifications",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    action_id = table.Column<Guid>(type: "uuid", nullable: false),
                    attempt = table.Column<int>(type: "integer", nullable: false),
                    due_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ran_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    outcome = table.Column<string>(type: "text", nullable: false),
                    checks = table.Column<string>(type: "jsonb", nullable: true),
                    detail = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_verifications", x => x.id);
                    table.ForeignKey(
                        name: "fk_verifications_agent_actions_action_id",
                        column: x => x.action_id,
                        principalTable: "agent_actions",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.InsertData(
                table: "agent_mode",
                columns: new[] { "id", "changed_at", "changed_by", "latch_reason", "latched_at", "mode", "runaway_latched" },
                values: new object[] { "singleton", new DateTimeOffset(new DateTime(1970, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified), new TimeSpan(0, 0, 0, 0, 0)), "watchtower/system", null, null, "Observe", false });

            migrationBuilder.CreateIndex(
                name: "ix_action_plans_investigation_id",
                table: "action_plans",
                column: "investigation_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_agent_actions_action_plan_id",
                table: "agent_actions",
                column: "action_plan_id");

            migrationBuilder.CreateIndex(
                name: "ix_agent_actions_approved_at",
                table: "agent_actions",
                column: "approved_at",
                descending: new bool[0]);

            migrationBuilder.CreateIndex(
                name: "ix_agent_actions_executed_at",
                table: "agent_actions",
                column: "executed_at",
                descending: new bool[0]);

            migrationBuilder.CreateIndex(
                name: "ix_agent_actions_incident_id",
                table: "agent_actions",
                column: "incident_id");

            migrationBuilder.CreateIndex(
                name: "ix_audit_events_at",
                table: "audit_events",
                column: "at",
                descending: new bool[0]);

            migrationBuilder.CreateIndex(
                name: "ix_audit_events_detail_gin",
                table: "audit_events",
                column: "detail")
                .Annotation("Npgsql:IndexMethod", "gin");

            migrationBuilder.CreateIndex(
                name: "ix_audit_events_incident_id",
                table: "audit_events",
                column: "incident_id");

            migrationBuilder.CreateIndex(
                name: "ix_audit_events_type_at",
                table: "audit_events",
                columns: new[] { "type", "at" },
                descending: new[] { false, true });

            migrationBuilder.CreateIndex(
                name: "ix_evidence_finding_id",
                table: "evidence",
                column: "finding_id");

            migrationBuilder.CreateIndex(
                name: "ix_evidence_step_id",
                table: "evidence",
                column: "step_id");

            migrationBuilder.CreateIndex(
                name: "ix_evidence_blobs_expires_at",
                table: "evidence_blobs",
                column: "expires_at");

            migrationBuilder.CreateIndex(
                name: "ix_evidence_blobs_investigation_id",
                table: "evidence_blobs",
                column: "investigation_id");

            migrationBuilder.CreateIndex(
                name: "ix_findings_investigation_id_is_primary",
                table: "findings",
                columns: new[] { "investigation_id", "is_primary" });

            migrationBuilder.CreateIndex(
                name: "ix_human_feedback_at",
                table: "human_feedback",
                column: "at");

            migrationBuilder.CreateIndex(
                name: "ix_human_feedback_incident_id",
                table: "human_feedback",
                column: "incident_id");

            migrationBuilder.CreateIndex(
                name: "ix_incident_digests_digest_hash",
                table: "incident_digests",
                column: "digest_hash");

            migrationBuilder.CreateIndex(
                name: "ix_incident_digests_incident_id",
                table: "incident_digests",
                column: "incident_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_incident_digests_workload_key",
                table: "incident_digests",
                column: "workload_key");

            migrationBuilder.CreateIndex(
                name: "ix_incident_events_incident_id_at",
                table: "incident_events",
                columns: new[] { "incident_id", "at" });

            migrationBuilder.CreateIndex(
                name: "ix_incidents_correlation_key",
                table: "incidents",
                column: "correlation_key");

            migrationBuilder.CreateIndex(
                name: "ix_incidents_opened_at",
                table: "incidents",
                column: "opened_at",
                descending: new bool[0]);

            migrationBuilder.CreateIndex(
                name: "ix_incidents_state_open",
                table: "incidents",
                column: "state",
                filter: "state IN ('Detected', 'Triaging', 'Investigating', 'AwaitingApproval', 'Acting', 'Verifying', 'Escalated')");

            migrationBuilder.CreateIndex(
                name: "ix_investigation_steps_investigation_id_ordinal",
                table: "investigation_steps",
                columns: new[] { "investigation_id", "ordinal" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_investigations_incident_id",
                table: "investigations",
                column: "incident_id");

            migrationBuilder.CreateIndex(
                name: "ix_investigations_started_at",
                table: "investigations",
                column: "started_at",
                descending: new bool[0]);

            migrationBuilder.CreateIndex(
                name: "ix_investigations_trace_id",
                table: "investigations",
                column: "trace_id");

            migrationBuilder.CreateIndex(
                name: "ix_llm_budget_breaches_at",
                table: "llm_budget_breaches",
                column: "at",
                descending: new bool[0]);

            migrationBuilder.CreateIndex(
                name: "ix_llm_budget_breaches_hour_bucket_kind",
                table: "llm_budget_breaches",
                columns: new[] { "hour_bucket", "kind" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_llm_usage_at",
                table: "llm_usage",
                column: "at",
                descending: new bool[0]);

            migrationBuilder.CreateIndex(
                name: "ix_llm_usage_incident_id_at",
                table: "llm_usage",
                columns: new[] { "incident_id", "at" });

            migrationBuilder.CreateIndex(
                name: "ix_signals_fingerprint_last_seen",
                table: "signals",
                columns: new[] { "fingerprint", "last_seen" },
                descending: new[] { false, true });

            migrationBuilder.CreateIndex(
                name: "ix_signals_incident_id",
                table: "signals",
                column: "incident_id");

            migrationBuilder.CreateIndex(
                name: "ix_signals_labels_gin",
                table: "signals",
                column: "labels")
                .Annotation("Npgsql:IndexMethod", "gin");

            migrationBuilder.CreateIndex(
                name: "ix_verifications_action_id_attempt",
                table: "verifications",
                columns: new[] { "action_id", "attempt" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_verifications_due_at_outcome",
                table: "verifications",
                columns: new[] { "due_at", "outcome" });

            // ----------------------------------------------------------------------
            // Below here: everything the EF model cannot express, as raw SQL.
            // ----------------------------------------------------------------------

            // Hybrid retrieval, lexical arm. A generated column rather than a trigger:
            // Postgres recomputes it inside the same write, so tsv can never disagree with
            // digest. A trigger would leave a path - a bulk update, a disabled trigger -
            // where an incident silently stops being findable while still being present.
            migrationBuilder.Sql("""
                ALTER TABLE incident_digests
                    ADD COLUMN tsv tsvector
                    GENERATED ALWAYS AS (to_tsvector('english', digest)) STORED;
                """);

            migrationBuilder.Sql(
                "CREATE INDEX ix_incident_digests_tsv_gin ON incident_digests USING gin (tsv);");

            // Hybrid retrieval, semantic arm. vector_cosine_ops, matching the <=> operator
            // IncidentSearch orders by: an HNSW index built for a different operator class
            // is not an error, it is simply never used, and the search quietly degrades
            // into a sequential scan over every digest ever written.
            migrationBuilder.Sql("""
                CREATE INDEX ix_incident_digests_embedding_hnsw
                    ON incident_digests USING hnsw (embedding vector_cosine_ops);
                """);

            // Cooldown, per-workload budget and flap detection all filter by the workload
            // and then by time, inside TryAdmitActionAsync's transaction. Composite rather
            // than two indexes, because filtering on the workload and ordering by time
            // separately means reading every action that workload ever had in order to
            // keep the last hour - while holding a lock, on the path that mutates a cluster.
            migrationBuilder.Sql("""
                CREATE INDEX ix_agent_actions_owner_approved_at
                    ON agent_actions (target_namespace, target_owner_kind, target_owner_name, approved_at DESC);
                """);

            // The same query shape for a bare object with no controller, which falls back
            // to kind/name - see TargetRef.WorkloadKey.
            migrationBuilder.Sql("""
                CREATE INDEX ix_agent_actions_target_approved_at
                    ON agent_actions (target_namespace, target_kind, target_name, approved_at DESC);
                """);

            migrationBuilder.Sql("""
                CREATE INDEX ix_incidents_owner_opened_at
                    ON incidents (target_namespace, target_owner_kind, target_owner_name, opened_at DESC);
                """);

            // ----------------------------------------------------------------------
            // The application connects as watchtower_app. Immutability of audit_events is
            // enforced by Postgres, not by convention: a bug or a compromised process must
            // not be able to rewrite history. "No audit, no action" is the paired invariant
            // on the application side - the executor refuses to act when it cannot write a
            // row - and WatchtowerDbContext.SaveChangesAsync throws on a Modified or
            // Deleted audit entry so the same mistake also fails loudly on a developer's
            // database, where this role usually does not exist at all.
            //
            // Wrapped in a DO block for exactly that reason: with no watchtower_app role
            // there is nothing to revoke and the migration must still run to completion.
            // The order inside matters - the blanket grant comes first and the revoke
            // after it, so audit_events ends up as the one table the application can only
            // append to. A later migration that adds a table has to repeat the grant.
            // ----------------------------------------------------------------------
            migrationBuilder.Sql("""
                DO $$
                BEGIN
                    IF EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'watchtower_app') THEN
                        GRANT USAGE ON SCHEMA public TO watchtower_app;
                        GRANT SELECT, INSERT, UPDATE, DELETE ON ALL TABLES IN SCHEMA public TO watchtower_app;

                        REVOKE UPDATE, DELETE, TRUNCATE ON audit_events FROM watchtower_app;
                    ELSE
                        RAISE NOTICE
                            'role watchtower_app not present: audit_events immutability is enforced only by WatchtowerDbContext in this database';
                    END IF;
                END
                $$;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "agent_mode");

            migrationBuilder.DropTable(
                name: "audit_events");

            migrationBuilder.DropTable(
                name: "evidence");

            migrationBuilder.DropTable(
                name: "evidence_blobs");

            migrationBuilder.DropTable(
                name: "human_feedback");

            migrationBuilder.DropTable(
                name: "incident_digests");

            migrationBuilder.DropTable(
                name: "incident_events");

            migrationBuilder.DropTable(
                name: "llm_budget_breaches");

            migrationBuilder.DropTable(
                name: "llm_usage");

            migrationBuilder.DropTable(
                name: "signals");

            migrationBuilder.DropTable(
                name: "verifications");

            migrationBuilder.DropTable(
                name: "workload_action_locks");

            migrationBuilder.DropTable(
                name: "findings");

            migrationBuilder.DropTable(
                name: "investigation_steps");

            migrationBuilder.DropTable(
                name: "agent_actions");

            migrationBuilder.DropTable(
                name: "action_plans");

            migrationBuilder.DropTable(
                name: "investigations");

            migrationBuilder.DropTable(
                name: "incidents");
        }
    }
}

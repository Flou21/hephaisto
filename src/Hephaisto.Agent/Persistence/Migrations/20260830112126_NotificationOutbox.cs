using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Hephaisto.Agent.Persistence.Migrations
{
    /// <summary>
    /// The outbox: one row per outbound message per channel, written in the same transaction as
    /// the state change that caused it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Why a table and not a queue.</b> Before this, the only thing that carried an
    /// escalation outward was <c>IIncidentNotifier</c> - an in-process channel, bounded at 64,
    /// <c>DropOldest</c>, one per Blazor circuit. It is documented as designed to drop, which is
    /// correct for nudging a browser tab and catastrophic for telling somebody the agent has
    /// given up. "Escalated, and nobody was told" is the worst failure this system has, and a
    /// pod restart must not be able to cause it. Only a row in Postgres survives a restart.
    /// </para>
    /// <para>
    /// <b>snapshot is jsonb and is frozen at enqueue.</b> Re-reading the incident at send time
    /// would let a retry twenty minutes into an outage describe a LATER state than the event it
    /// reports - an escalation card that has quietly become a resolution card. It also means a
    /// rendering fix reaches rows already queued, which storing the rendered payload would not.
    /// </para>
    /// <para>
    /// <b>correlation_key is denormalised out of that document</b> because the outbound cooldown
    /// queries it, and a cooldown that had to deserialise every candidate row to find its key
    /// would put a sequential scan on the delivery path.
    /// </para>
    /// <para>
    /// <b>No foreign key to incidents</b>, matching <c>audit_events</c> for the same reason: a
    /// delivery records what was said, and cascading it away with the incident would delete the
    /// evidence of whether anybody was told.
    /// </para>
    /// <para>
    /// <b>No GRANT block here, deliberately.</b> <c>InitialCreate</c> carried one wrapped in
    /// <c>IF EXISTS (SELECT 1 FROM pg_roles ...)</c>, and its own comment noted that a later
    /// migration adding a table has to repeat it. That is exactly the trap backlog #6 was:
    /// <c>EnsureAuditImmutabilityAsync</c> now re-applies <c>GRANT ... ON ALL TABLES IN
    /// SCHEMA</c> on every boot, so this table is covered without a line here - and an
    /// integration test asserts the serving role can actually write it rather than assuming so.
    /// </para>
    /// </remarks>
    public partial class NotificationOutbox : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "notification_deliveries",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    @event = table.Column<string>(name: "event", type: "text", nullable: false),
                    incident_id = table.Column<Guid>(type: "uuid", nullable: true),
                    channel = table.Column<string>(type: "text", nullable: false),
                    correlation_key = table.Column<string>(type: "text", nullable: false),
                    status = table.Column<string>(type: "text", nullable: false),
                    snapshot = table.Column<string>(type: "jsonb", nullable: false),
                    attempt_count = table.Column<int>(type: "integer", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    next_attempt_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    delivered_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    last_error = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_notification_deliveries", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_notification_deliveries_channel_correlation_key_delivered_at",
                table: "notification_deliveries",
                columns: new[] { "channel", "correlation_key", "delivered_at" });

            migrationBuilder.CreateIndex(
                name: "ix_notification_deliveries_channel_delivered_at",
                table: "notification_deliveries",
                columns: new[] { "channel", "delivered_at" });

            migrationBuilder.CreateIndex(
                name: "ix_notification_deliveries_incident_id",
                table: "notification_deliveries",
                column: "incident_id");

            migrationBuilder.CreateIndex(
                name: "ix_notification_deliveries_status_next_attempt_at",
                table: "notification_deliveries",
                columns: new[] { "status", "next_attempt_at" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "notification_deliveries");
        }
    }
}

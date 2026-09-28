using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Hephaisto.Agent.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class SignalAnnotations : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_notification_deliveries_channel_correlation_key_delivered_at",
                table: "notification_deliveries");

            migrationBuilder.AddColumn<string>(
                name: "annotations",
                table: "signals",
                type: "jsonb",
                nullable: false,
                defaultValueSql: "'{}'::jsonb");

            migrationBuilder.CreateIndex(
                name: "ix_notification_deliveries_channel_correlation_key_event_deliv~",
                table: "notification_deliveries",
                columns: new[] { "channel", "correlation_key", "event", "delivered_at" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_notification_deliveries_channel_correlation_key_event_deliv~",
                table: "notification_deliveries");

            migrationBuilder.DropColumn(
                name: "annotations",
                table: "signals");

            migrationBuilder.CreateIndex(
                name: "ix_notification_deliveries_channel_correlation_key_delivered_at",
                table: "notification_deliveries",
                columns: new[] { "channel", "correlation_key", "delivered_at" });
        }
    }
}

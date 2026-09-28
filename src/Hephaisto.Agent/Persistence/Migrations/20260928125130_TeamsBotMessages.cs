using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Hephaisto.Agent.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class TeamsBotMessages : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "teams_bot_messages",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    kind = table.Column<string>(type: "text", nullable: false),
                    state = table.Column<string>(type: "text", nullable: false),
                    incident_id = table.Column<Guid>(type: "uuid", nullable: true),
                    recipient = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: false),
                    conversation_id = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                    activity_id = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    content_hash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    delivery_id = table.Column<Guid>(type: "uuid", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    last_error = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_teams_bot_messages", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_teams_bot_messages_incident_recipient",
                table: "teams_bot_messages",
                columns: new[] { "incident_id", "recipient" });

            migrationBuilder.CreateIndex(
                name: "ix_teams_bot_messages_kind_state",
                table: "teams_bot_messages",
                columns: new[] { "kind", "state" });

            migrationBuilder.CreateIndex(
                name: "ux_teams_bot_messages_delivery_recipient",
                table: "teams_bot_messages",
                columns: new[] { "delivery_id", "recipient" },
                unique: true,
                filter: "delivery_id IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "teams_bot_messages");
        }
    }
}

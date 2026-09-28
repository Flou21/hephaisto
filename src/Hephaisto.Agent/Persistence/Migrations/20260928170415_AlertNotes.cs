using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Hephaisto.Agent.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AlertNotes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "alert_notes",
                columns: table => new
                {
                    alert_name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    body = table.Column<string>(type: "character varying(16384)", maxLength: 16384, nullable: false),
                    updated_by = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_alert_notes", x => x.alert_name);
                });

            migrationBuilder.CreateTable(
                name: "alert_note_entries",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    alert_name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    incident_id = table.Column<Guid>(type: "uuid", nullable: true),
                    author = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: false),
                    text = table.Column<string>(type: "character varying(4096)", maxLength: 4096, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_alert_note_entries", x => x.id);
                    table.ForeignKey(
                        name: "fk_alert_note_entries_alert_notes_alert_name",
                        column: x => x.alert_name,
                        principalTable: "alert_notes",
                        principalColumn: "alert_name",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_alert_note_entries_alert_name_created_at",
                table: "alert_note_entries",
                columns: new[] { "alert_name", "created_at" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "alert_note_entries");

            migrationBuilder.DropTable(
                name: "alert_notes");
        }
    }
}

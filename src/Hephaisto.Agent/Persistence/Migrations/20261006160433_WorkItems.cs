using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Hephaisto.Agent.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class WorkItems : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "work_items",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    source = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    repository = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    number = table.Column<int>(type: "integer", nullable: false),
                    node_id = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    url = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                    title = table.Column<string>(type: "text", nullable: false),
                    type = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    author_login = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    author_id = table.Column<long>(type: "bigint", nullable: false),
                    body = table.Column<string>(type: "text", nullable: false),
                    labels = table.Column<string>(type: "jsonb", nullable: false, defaultValueSql: "'[]'::jsonb"),
                    state = table.Column<string>(type: "text", nullable: false),
                    state_reason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    taken_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    closed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    status_comment_id = table.Column<long>(type: "bigint", nullable: true),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_work_items", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_work_items_source_repository_state",
                table: "work_items",
                columns: new[] { "source", "repository", "state" });

            migrationBuilder.CreateIndex(
                name: "ix_work_items_state_taken_at",
                table: "work_items",
                columns: new[] { "state", "taken_at" });

            migrationBuilder.CreateIndex(
                name: "ux_work_items_one_taken_per_issue",
                table: "work_items",
                columns: new[] { "source", "repository", "number" },
                unique: true,
                filter: "state = 'Taken'");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "work_items");
        }
    }
}

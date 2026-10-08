using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Hephaisto.Agent.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class ReplanOnIssue : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "replan_after_attempt_id",
                table: "work_items",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "replan_requested_by",
                table: "work_items",
                type: "character varying(128)",
                maxLength: 128,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "replan_after_attempt_id",
                table: "work_items");

            migrationBuilder.DropColumn(
                name: "replan_requested_by",
                table: "work_items");
        }
    }
}

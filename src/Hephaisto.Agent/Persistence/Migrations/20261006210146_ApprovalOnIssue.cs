using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Hephaisto.Agent.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class ApprovalOnIssue : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "still_assigned",
                table: "work_items",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "command_answers",
                table: "code_fix_attempts",
                type: "character varying(512)",
                maxLength: 512,
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "command_comment_id",
                table: "code_fix_attempts",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "pr_body",
                table: "code_fix_attempts",
                type: "text",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "still_assigned",
                table: "work_items");

            migrationBuilder.DropColumn(
                name: "command_answers",
                table: "code_fix_attempts");

            migrationBuilder.DropColumn(
                name: "command_comment_id",
                table: "code_fix_attempts");

            migrationBuilder.DropColumn(
                name: "pr_body",
                table: "code_fix_attempts");
        }
    }
}

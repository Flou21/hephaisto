using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Hephaisto.Agent.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class WorkItemCodeFix : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "decline_codes",
                table: "work_items",
                type: "character varying(512)",
                maxLength: 512,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "decline_reason",
                table: "work_items",
                type: "character varying(500)",
                maxLength: 500,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "status_comment_digest",
                table: "work_items",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AlterColumn<Guid>(
                name: "incident_id",
                table: "llm_usage",
                type: "uuid",
                nullable: true,
                oldClrType: typeof(Guid),
                oldType: "uuid");

            migrationBuilder.AlterColumn<Guid>(
                name: "incident_id",
                table: "code_fix_attempts",
                type: "uuid",
                nullable: true,
                oldClrType: typeof(Guid),
                oldType: "uuid");

            migrationBuilder.AddColumn<long>(
                name: "plan_comment_id",
                table: "code_fix_attempts",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "work_item_id",
                table: "code_fix_attempts",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "ix_code_fix_attempts_work_item_id",
                table: "code_fix_attempts",
                column: "work_item_id");

            migrationBuilder.CreateIndex(
                name: "ux_code_fix_attempts_one_open_per_work_item",
                table: "code_fix_attempts",
                column: "work_item_id",
                unique: true,
                filter: "state IN ('Eligible', 'Planning', 'PlanReady', 'Implementing')");

            migrationBuilder.AddCheckConstraint(
                name: "ck_code_fix_attempts_one_subject",
                table: "code_fix_attempts",
                sql: "(incident_id IS NULL) <> (work_item_id IS NULL)");

            migrationBuilder.AddForeignKey(
                name: "fk_code_fix_attempts_work_items_work_item_id",
                table: "code_fix_attempts",
                column: "work_item_id",
                principalTable: "work_items",
                principalColumn: "id",
                onDelete: ReferentialAction.Cascade);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "fk_code_fix_attempts_work_items_work_item_id",
                table: "code_fix_attempts");

            migrationBuilder.DropIndex(
                name: "ix_code_fix_attempts_work_item_id",
                table: "code_fix_attempts");

            migrationBuilder.DropIndex(
                name: "ux_code_fix_attempts_one_open_per_work_item",
                table: "code_fix_attempts");

            migrationBuilder.DropCheckConstraint(
                name: "ck_code_fix_attempts_one_subject",
                table: "code_fix_attempts");

            migrationBuilder.DropColumn(
                name: "decline_codes",
                table: "work_items");

            migrationBuilder.DropColumn(
                name: "decline_reason",
                table: "work_items");

            migrationBuilder.DropColumn(
                name: "status_comment_digest",
                table: "work_items");

            migrationBuilder.DropColumn(
                name: "plan_comment_id",
                table: "code_fix_attempts");

            migrationBuilder.DropColumn(
                name: "work_item_id",
                table: "code_fix_attempts");

            migrationBuilder.AlterColumn<Guid>(
                name: "incident_id",
                table: "llm_usage",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"),
                oldClrType: typeof(Guid),
                oldType: "uuid",
                oldNullable: true);

            migrationBuilder.AlterColumn<Guid>(
                name: "incident_id",
                table: "code_fix_attempts",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"),
                oldClrType: typeof(Guid),
                oldType: "uuid",
                oldNullable: true);
        }
    }
}

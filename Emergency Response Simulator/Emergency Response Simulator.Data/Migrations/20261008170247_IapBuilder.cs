using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Emergency_Response_Simulator.Data.Migrations
{
    /// <inheritdoc />
    public partial class IapBuilder : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "fk_incident_action_plans_users_approved_by_id",
                table: "incident_action_plans");

            migrationBuilder.DropForeignKey(
                name: "fk_incident_action_plans_users_prepared_by_id",
                table: "incident_action_plans");

            migrationBuilder.DropIndex(
                name: "ix_incident_action_plans_approved_by_id",
                table: "incident_action_plans");

            migrationBuilder.DropIndex(
                name: "ix_incident_action_plans_prepared_by_id",
                table: "incident_action_plans");

            migrationBuilder.DropColumn(
                name: "approved_by_id",
                table: "incident_action_plans");

            migrationBuilder.DropColumn(
                name: "assignments",
                table: "incident_action_plans");

            migrationBuilder.DropColumn(
                name: "commanders_intent",
                table: "incident_action_plans");

            migrationBuilder.DropColumn(
                name: "communications_plan",
                table: "incident_action_plans");

            migrationBuilder.DropColumn(
                name: "medical_plan",
                table: "incident_action_plans");

            migrationBuilder.DropColumn(
                name: "objectives",
                table: "incident_action_plans");

            migrationBuilder.DropColumn(
                name: "organization",
                table: "incident_action_plans");

            migrationBuilder.RenameColumn(
                name: "safety_message",
                table: "incident_action_plans",
                newName: "return_comments");

            migrationBuilder.RenameColumn(
                name: "prepared_by_id",
                table: "incident_action_plans",
                newName: "based_on_id");

            migrationBuilder.AddColumn<string>(
                name: "focus",
                table: "operational_periods",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "approved_by",
                table: "incident_action_plans",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "briefed_at",
                table: "incident_action_plans",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "content",
                table: "incident_action_plans",
                type: "jsonb",
                nullable: false,
                defaultValue: "{}");

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "created_at",
                table: "incident_action_plans",
                type: "timestamp with time zone",
                nullable: false,
                defaultValue: new DateTimeOffset(new DateTime(1, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified), new TimeSpan(0, 0, 0, 0, 0)));

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "last_saved_at",
                table: "incident_action_plans",
                type: "timestamp with time zone",
                nullable: false,
                defaultValue: new DateTimeOffset(new DateTime(1, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified), new TimeSpan(0, 0, 0, 0, 0)));

            migrationBuilder.AddColumn<string>(
                name: "prepared_by",
                table: "incident_action_plans",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "returned_by",
                table: "incident_action_plans",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "submitted_at",
                table: "incident_action_plans",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "submitted_by",
                table: "incident_action_plans",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "focus",
                table: "operational_periods");

            migrationBuilder.DropColumn(
                name: "approved_by",
                table: "incident_action_plans");

            migrationBuilder.DropColumn(
                name: "briefed_at",
                table: "incident_action_plans");

            migrationBuilder.DropColumn(
                name: "content",
                table: "incident_action_plans");

            migrationBuilder.DropColumn(
                name: "created_at",
                table: "incident_action_plans");

            migrationBuilder.DropColumn(
                name: "last_saved_at",
                table: "incident_action_plans");

            migrationBuilder.DropColumn(
                name: "prepared_by",
                table: "incident_action_plans");

            migrationBuilder.DropColumn(
                name: "returned_by",
                table: "incident_action_plans");

            migrationBuilder.DropColumn(
                name: "submitted_at",
                table: "incident_action_plans");

            migrationBuilder.DropColumn(
                name: "submitted_by",
                table: "incident_action_plans");

            migrationBuilder.RenameColumn(
                name: "return_comments",
                table: "incident_action_plans",
                newName: "safety_message");

            migrationBuilder.RenameColumn(
                name: "based_on_id",
                table: "incident_action_plans",
                newName: "prepared_by_id");

            migrationBuilder.AddColumn<Guid>(
                name: "approved_by_id",
                table: "incident_action_plans",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "assignments",
                table: "incident_action_plans",
                type: "jsonb",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "commanders_intent",
                table: "incident_action_plans",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "communications_plan",
                table: "incident_action_plans",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "medical_plan",
                table: "incident_action_plans",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "objectives",
                table: "incident_action_plans",
                type: "jsonb",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "organization",
                table: "incident_action_plans",
                type: "jsonb",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "ix_incident_action_plans_approved_by_id",
                table: "incident_action_plans",
                column: "approved_by_id");

            migrationBuilder.CreateIndex(
                name: "ix_incident_action_plans_prepared_by_id",
                table: "incident_action_plans",
                column: "prepared_by_id");

            migrationBuilder.AddForeignKey(
                name: "fk_incident_action_plans_users_approved_by_id",
                table: "incident_action_plans",
                column: "approved_by_id",
                principalTable: "users",
                principalColumn: "id",
                onDelete: ReferentialAction.SetNull);

            migrationBuilder.AddForeignKey(
                name: "fk_incident_action_plans_users_prepared_by_id",
                table: "incident_action_plans",
                column: "prepared_by_id",
                principalTable: "users",
                principalColumn: "id",
                onDelete: ReferentialAction.SetNull);
        }
    }
}

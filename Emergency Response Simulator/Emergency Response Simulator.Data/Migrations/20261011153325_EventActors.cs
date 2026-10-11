using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Emergency_Response_Simulator.Data.Migrations
{
    /// <inheritdoc />
    public partial class EventActors : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "actor_id",
                table: "events",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "actor_role",
                table: "events",
                type: "character varying(40)",
                maxLength: 40,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "ix_events_session_id_actor_id",
                table: "events",
                columns: new[] { "session_id", "actor_id" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_events_session_id_actor_id",
                table: "events");

            migrationBuilder.DropColumn(
                name: "actor_id",
                table: "events");

            migrationBuilder.DropColumn(
                name: "actor_role",
                table: "events");
        }
    }
}

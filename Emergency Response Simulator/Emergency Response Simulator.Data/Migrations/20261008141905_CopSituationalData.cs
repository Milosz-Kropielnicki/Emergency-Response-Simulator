using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Emergency_Response_Simulator.Data.Migrations
{
    /// <inheritdoc />
    public partial class CopSituationalData : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "last_contact_at",
                table: "resources",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "incident_commander_name",
                table: "incidents",
                type: "text",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "last_contact_at",
                table: "resources");

            migrationBuilder.DropColumn(
                name: "incident_commander_name",
                table: "incidents");
        }
    }
}

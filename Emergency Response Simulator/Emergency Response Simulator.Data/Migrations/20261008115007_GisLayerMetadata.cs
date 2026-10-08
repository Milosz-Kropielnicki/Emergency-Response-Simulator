using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Emergency_Response_Simulator.Data.Migrations
{
    /// <inheritdoc />
    public partial class GisLayerMetadata : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "group",
                table: "gis_layers",
                type: "character varying(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "imported_at",
                table: "gis_layers",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "min_zoom",
                table: "gis_layers",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "source",
                table: "gis_layers",
                type: "character varying(400)",
                maxLength: 400,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "group",
                table: "gis_layers");

            migrationBuilder.DropColumn(
                name: "imported_at",
                table: "gis_layers");

            migrationBuilder.DropColumn(
                name: "min_zoom",
                table: "gis_layers");

            migrationBuilder.DropColumn(
                name: "source",
                table: "gis_layers");
        }
    }
}

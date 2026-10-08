using System;
using System.Collections.Generic;
using Microsoft.EntityFrameworkCore.Migrations;
using NetTopologySuite.Geometries;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Emergency_Response_Simulator.Data.Migrations
{
    /// <inheritdoc />
    public partial class InitialCreate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterDatabase()
                .Annotation("Npgsql:PostgresExtension:postgis", ",,");

            migrationBuilder.CreateTable(
                name: "agencies",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    short_name = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    type = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    jurisdiction = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_agencies", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "events",
                columns: table => new
                {
                    sequence = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityAlwaysColumn),
                    session_id = table.Column<Guid>(type: "uuid", nullable: false),
                    sim_time = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    wall_time = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    visibility = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    source = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    type = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    payload = table.Column<string>(type: "jsonb", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_events", x => x.sequence);
                });

            migrationBuilder.CreateTable(
                name: "gis_layers",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    key = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: false),
                    name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    kind = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    visible_by_default = table.Column<bool>(type: "boolean", nullable: false),
                    display_order = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_gis_layers", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "users",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    display_name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    role = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    agency_id = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_users", x => x.id);
                    table.ForeignKey(
                        name: "fk_users_agencies_agency_id",
                        column: x => x.agency_id,
                        principalTable: "agencies",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "gis_features",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    geometry = table.Column<Geometry>(type: "geometry(Geometry,4326)", nullable: false),
                    properties = table.Column<string>(type: "jsonb", nullable: false),
                    layer_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_gis_features", x => x.id);
                    table.ForeignKey(
                        name: "fk_gis_features_gis_layers_layer_id",
                        column: x => x.layer_id,
                        principalTable: "gis_layers",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "incidents",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    number = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    type = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    priority = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    status = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    location = table.Column<Point>(type: "geometry(Point,4326)", nullable: false),
                    address = table.Column<string>(type: "character varying(400)", maxLength: 400, nullable: true),
                    reported_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    last_updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    threats = table.Column<List<string>>(type: "text[]", nullable: false),
                    casualties_reported = table.Column<int>(type: "integer", nullable: false),
                    casualties_confirmed = table.Column<int>(type: "integer", nullable: false),
                    evacuation_required = table.Column<bool>(type: "boolean", nullable: false),
                    incident_commander_id = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_incidents", x => x.id);
                    table.ForeignKey(
                        name: "fk_incidents_users_incident_commander_id",
                        column: x => x.incident_commander_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "operational_periods",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    number = table.Column<int>(type: "integer", nullable: false),
                    start = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    end = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    incident_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_operational_periods", x => x.id);
                    table.ForeignKey(
                        name: "fk_operational_periods_incidents_incident_id",
                        column: x => x.incident_id,
                        principalTable: "incidents",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "reports",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    source = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    source_name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    claim = table.Column<string>(type: "text", nullable: false),
                    confidence = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    verification = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    location = table.Column<Point>(type: "geometry(Point,4326)", nullable: true),
                    location_accuracy_meters = table.Column<double>(type: "double precision", nullable: true),
                    received_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    incident_id = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_reports", x => x.id);
                    table.ForeignKey(
                        name: "fk_reports_incidents_incident_id",
                        column: x => x.incident_id,
                        principalTable: "incidents",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "resources",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    kind = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    quantity = table.Column<int>(type: "integer", nullable: false),
                    location = table.Column<Point>(type: "geometry(Point,4326)", nullable: true),
                    agency_id = table.Column<Guid>(type: "uuid", nullable: true),
                    resource_type = table.Column<string>(type: "character varying(8)", maxLength: 8, nullable: false),
                    callsign = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: true),
                    type = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: true),
                    status = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: true),
                    crew_size = table.Column<int>(type: "integer", nullable: true),
                    capabilities = table.Column<List<string>>(type: "text[]", nullable: true),
                    home_station = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    speed_kph = table.Column<double>(type: "double precision", nullable: true),
                    heading = table.Column<double>(type: "double precision", nullable: true),
                    last_avl_update = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    comms_connected = table.Column<bool>(type: "boolean", nullable: true),
                    eta = table.Column<TimeSpan>(type: "interval", nullable: true),
                    assigned_incident_id = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_resources", x => x.id);
                    table.ForeignKey(
                        name: "fk_resources_agencies_agency_id",
                        column: x => x.agency_id,
                        principalTable: "agencies",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "fk_resources_incidents_assigned_incident_id",
                        column: x => x.assigned_incident_id,
                        principalTable: "incidents",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "zones",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    type = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    area = table.Column<Geometry>(type: "geometry(Geometry,4326)", nullable: false),
                    effective_from = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    effective_until = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    incident_id = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_zones", x => x.id);
                    table.ForeignKey(
                        name: "fk_zones_incidents_incident_id",
                        column: x => x.incident_id,
                        principalTable: "incidents",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "incident_action_plans",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    version = table.Column<int>(type: "integer", nullable: false),
                    status = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    commanders_intent = table.Column<string>(type: "text", nullable: true),
                    communications_plan = table.Column<string>(type: "text", nullable: true),
                    medical_plan = table.Column<string>(type: "text", nullable: true),
                    safety_message = table.Column<string>(type: "text", nullable: true),
                    incident_id = table.Column<Guid>(type: "uuid", nullable: false),
                    operational_period_id = table.Column<Guid>(type: "uuid", nullable: false),
                    prepared_by_id = table.Column<Guid>(type: "uuid", nullable: true),
                    approved_by_id = table.Column<Guid>(type: "uuid", nullable: true),
                    approved_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    assignments = table.Column<string>(type: "jsonb", nullable: true),
                    objectives = table.Column<string>(type: "jsonb", nullable: true),
                    organization = table.Column<string>(type: "jsonb", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_incident_action_plans", x => x.id);
                    table.ForeignKey(
                        name: "fk_incident_action_plans_incidents_incident_id",
                        column: x => x.incident_id,
                        principalTable: "incidents",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_incident_action_plans_operational_periods_operational_perio",
                        column: x => x.operational_period_id,
                        principalTable: "operational_periods",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_incident_action_plans_users_approved_by_id",
                        column: x => x.approved_by_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "fk_incident_action_plans_users_prepared_by_id",
                        column: x => x.prepared_by_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "alerts",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    category = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    severity = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    title = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    message = table.Column<string>(type: "text", nullable: false),
                    raised_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    acknowledged_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    acknowledged_by_id = table.Column<Guid>(type: "uuid", nullable: true),
                    incident_id = table.Column<Guid>(type: "uuid", nullable: true),
                    unit_id = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_alerts", x => x.id);
                    table.ForeignKey(
                        name: "fk_alerts_incidents_incident_id",
                        column: x => x.incident_id,
                        principalTable: "incidents",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "fk_alerts_units_unit_id",
                        column: x => x.unit_id,
                        principalTable: "resources",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "fk_alerts_users_acknowledged_by_id",
                        column: x => x.acknowledged_by_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateIndex(
                name: "ix_alerts_acknowledged_by_id",
                table: "alerts",
                column: "acknowledged_by_id");

            migrationBuilder.CreateIndex(
                name: "ix_alerts_incident_id",
                table: "alerts",
                column: "incident_id");

            migrationBuilder.CreateIndex(
                name: "ix_alerts_raised_at",
                table: "alerts",
                column: "raised_at");

            migrationBuilder.CreateIndex(
                name: "ix_alerts_unit_id",
                table: "alerts",
                column: "unit_id");

            migrationBuilder.CreateIndex(
                name: "ix_events_session_id_sequence",
                table: "events",
                columns: new[] { "session_id", "sequence" });

            migrationBuilder.CreateIndex(
                name: "ix_events_session_id_sim_time",
                table: "events",
                columns: new[] { "session_id", "sim_time" });

            migrationBuilder.CreateIndex(
                name: "ix_gis_features_geometry",
                table: "gis_features",
                column: "geometry")
                .Annotation("Npgsql:IndexMethod", "gist");

            migrationBuilder.CreateIndex(
                name: "ix_gis_features_layer_id",
                table: "gis_features",
                column: "layer_id");

            migrationBuilder.CreateIndex(
                name: "ix_gis_layers_key",
                table: "gis_layers",
                column: "key",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_incident_action_plans_approved_by_id",
                table: "incident_action_plans",
                column: "approved_by_id");

            migrationBuilder.CreateIndex(
                name: "ix_incident_action_plans_incident_id_operational_period_id_ver",
                table: "incident_action_plans",
                columns: new[] { "incident_id", "operational_period_id", "version" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_incident_action_plans_operational_period_id",
                table: "incident_action_plans",
                column: "operational_period_id");

            migrationBuilder.CreateIndex(
                name: "ix_incident_action_plans_prepared_by_id",
                table: "incident_action_plans",
                column: "prepared_by_id");

            migrationBuilder.CreateIndex(
                name: "ix_incidents_incident_commander_id",
                table: "incidents",
                column: "incident_commander_id");

            migrationBuilder.CreateIndex(
                name: "ix_incidents_location",
                table: "incidents",
                column: "location")
                .Annotation("Npgsql:IndexMethod", "gist");

            migrationBuilder.CreateIndex(
                name: "ix_incidents_number",
                table: "incidents",
                column: "number",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_operational_periods_incident_id_number",
                table: "operational_periods",
                columns: new[] { "incident_id", "number" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_reports_incident_id",
                table: "reports",
                column: "incident_id");

            migrationBuilder.CreateIndex(
                name: "ix_reports_received_at",
                table: "reports",
                column: "received_at");

            migrationBuilder.CreateIndex(
                name: "ix_resources_agency_id",
                table: "resources",
                column: "agency_id");

            migrationBuilder.CreateIndex(
                name: "ix_resources_assigned_incident_id",
                table: "resources",
                column: "assigned_incident_id");

            migrationBuilder.CreateIndex(
                name: "ix_resources_callsign",
                table: "resources",
                column: "callsign");

            migrationBuilder.CreateIndex(
                name: "ix_resources_location",
                table: "resources",
                column: "location")
                .Annotation("Npgsql:IndexMethod", "gist");

            migrationBuilder.CreateIndex(
                name: "ix_resources_status",
                table: "resources",
                column: "status");

            migrationBuilder.CreateIndex(
                name: "ix_users_agency_id",
                table: "users",
                column: "agency_id");

            migrationBuilder.CreateIndex(
                name: "ix_zones_area",
                table: "zones",
                column: "area")
                .Annotation("Npgsql:IndexMethod", "gist");

            migrationBuilder.CreateIndex(
                name: "ix_zones_incident_id",
                table: "zones",
                column: "incident_id");

            // The event stream is append-only: reject any attempt to rewrite history.
            migrationBuilder.Sql("""
                CREATE FUNCTION reject_event_mutation() RETURNS trigger
                LANGUAGE plpgsql AS $$
                BEGIN
                    RAISE EXCEPTION 'events is append-only: % is not allowed', TG_OP
                        USING ERRCODE = 'restrict_violation';
                END;
                $$;

                CREATE TRIGGER events_append_only_rows
                    BEFORE UPDATE OR DELETE ON events
                    FOR EACH ROW EXECUTE FUNCTION reject_event_mutation();

                CREATE TRIGGER events_append_only_truncate
                    BEFORE TRUNCATE ON events
                    FOR EACH STATEMENT EXECUTE FUNCTION reject_event_mutation();
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DROP TRIGGER IF EXISTS events_append_only_truncate ON events;
                DROP TRIGGER IF EXISTS events_append_only_rows ON events;
                DROP FUNCTION IF EXISTS reject_event_mutation();
                """);

            migrationBuilder.DropTable(
                name: "alerts");

            migrationBuilder.DropTable(
                name: "events");

            migrationBuilder.DropTable(
                name: "gis_features");

            migrationBuilder.DropTable(
                name: "incident_action_plans");

            migrationBuilder.DropTable(
                name: "reports");

            migrationBuilder.DropTable(
                name: "zones");

            migrationBuilder.DropTable(
                name: "resources");

            migrationBuilder.DropTable(
                name: "gis_layers");

            migrationBuilder.DropTable(
                name: "operational_periods");

            migrationBuilder.DropTable(
                name: "incidents");

            migrationBuilder.DropTable(
                name: "users");

            migrationBuilder.DropTable(
                name: "agencies");
        }
    }
}

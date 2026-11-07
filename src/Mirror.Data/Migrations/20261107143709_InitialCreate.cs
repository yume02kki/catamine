using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Mirror.Data.Migrations
{
    /// <inheritdoc />
    public partial class InitialCreate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "jobs",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    mission_id = table.Column<Guid>(type: "uuid", nullable: true),
                    type = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    status = table.Column<int>(type: "integer", nullable: false),
                    @params = table.Column<string>(name: "params", type: "jsonb", nullable: false),
                    done = table.Column<long>(type: "bigint", nullable: false),
                    total = table.Column<long>(type: "bigint", nullable: true),
                    result = table.Column<string>(type: "jsonb", nullable: true),
                    checkpoint = table.Column<string>(type: "jsonb", nullable: true),
                    error = table.Column<string>(type: "text", nullable: true),
                    cancel_requested = table.Column<bool>(type: "boolean", nullable: false),
                    worker_id = table.Column<string>(type: "text", nullable: true),
                    attempts = table.Column<int>(type: "integer", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    started_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    finished_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    heartbeat_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_jobs", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "missions",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    slug = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    description = table.Column<string>(type: "text", nullable: true),
                    main_url = table.Column<string>(type: "character varying(2048)", maxLength: 2048, nullable: false),
                    dev_url = table.Column<string>(type: "character varying(2048)", maxLength: 2048, nullable: false),
                    enabled = table.Column<bool>(type: "boolean", nullable: false),
                    mirror_enabled = table.Column<bool>(type: "boolean", nullable: false),
                    sample_rate = table.Column<double>(type: "double precision", nullable: false),
                    mirror_methods = table.Column<string[]>(type: "text[]", nullable: false),
                    max_mirror_body_bytes = table.Column<int>(type: "integer", nullable: false),
                    dev_timeout_ms = table.Column<int>(type: "integer", nullable: false),
                    dev_max_concurrency = table.Column<int>(type: "integer", nullable: false),
                    header_policy = table.Column<string>(type: "jsonb", nullable: false),
                    compared_headers = table.Column<string[]>(type: "text[]", nullable: false),
                    rules_version = table.Column<int>(type: "integer", nullable: false),
                    retention_days = table.Column<int>(type: "integer", nullable: false),
                    max_storage_mb = table.Column<int>(type: "integer", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_missions", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "proxy_instances",
                columns: table => new
                {
                    instance_id = table.Column<string>(type: "text", nullable: false),
                    last_seen = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    started_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    metrics = table.Column<string>(type: "jsonb", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_proxy_instances", x => x.instance_id);
                });

            migrationBuilder.CreateTable(
                name: "mission_rules",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    mission_id = table.Column<Guid>(type: "uuid", nullable: false),
                    order = table.Column<int>(type: "integer", nullable: false),
                    type = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    name = table.Column<string>(type: "text", nullable: true),
                    enabled = table.Column<bool>(type: "boolean", nullable: false),
                    config = table.Column<string>(type: "jsonb", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_mission_rules", x => x.id);
                    table.ForeignKey(
                        name: "fk_mission_rules_missions_mission_id",
                        column: x => x.mission_id,
                        principalTable: "missions",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_jobs_mission_id_created_at",
                table: "jobs",
                columns: new[] { "mission_id", "created_at" });

            migrationBuilder.CreateIndex(
                name: "ix_jobs_status_created_at",
                table: "jobs",
                columns: new[] { "status", "created_at" });

            migrationBuilder.CreateIndex(
                name: "ix_mission_rules_mission_id_order",
                table: "mission_rules",
                columns: new[] { "mission_id", "order" });

            migrationBuilder.CreateIndex(
                name: "ix_missions_slug",
                table: "missions",
                column: "slug",
                unique: true);

            migrationBuilder.Sql(LogSchema.Create);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(LogSchema.Drop);

            migrationBuilder.DropTable(
                name: "jobs");

            migrationBuilder.DropTable(
                name: "mission_rules");

            migrationBuilder.DropTable(
                name: "proxy_instances");

            migrationBuilder.DropTable(
                name: "missions");
        }
    }
}

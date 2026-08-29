using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace RiskManagementService.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class InitialCreate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "kill_switch",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false),
                    Engaged = table.Column<bool>(type: "boolean", nullable: false),
                    Actor = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    Reason = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: true),
                    ChangedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_kill_switch", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "lockout",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false),
                    ReleaseOn = table.Column<DateOnly>(type: "date", nullable: false),
                    Reason = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: false),
                    EngagedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_lockout", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "risk_settings",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false),
                    Json = table.Column<string>(type: "jsonb", nullable: false),
                    Version = table.Column<int>(type: "integer", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_risk_settings", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "settings_change_log",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Actor = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    ChangeType = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Reason = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: false),
                    ChangedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    Before = table.Column<string>(type: "text", nullable: true),
                    After = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_settings_change_log", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_settings_change_log_ChangedAt",
                table: "settings_change_log",
                column: "ChangedAt");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "kill_switch");

            migrationBuilder.DropTable(
                name: "lockout");

            migrationBuilder.DropTable(
                name: "risk_settings");

            migrationBuilder.DropTable(
                name: "settings_change_log");
        }
    }
}

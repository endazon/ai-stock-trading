using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace RiskManagementService.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddPositionDriftAdoption : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "broker_position_observation",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false),
                    PositionsJson = table.Column<string>(type: "jsonb", nullable: false),
                    ObservedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_broker_position_observation", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "position_drift_adoptions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    IdempotencyKey = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    Symbol = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    Market = table.Column<int>(type: "integer", nullable: false),
                    Side = table.Column<int>(type: "integer", nullable: false),
                    Quantity = table.Column<int>(type: "integer", nullable: false),
                    CostBasisPrice = table.Column<decimal>(type: "numeric", nullable: false),
                    FxRateToBase = table.Column<decimal>(type: "numeric", nullable: false),
                    LedgerQuantityBefore = table.Column<int>(type: "integer", nullable: false),
                    BrokerQuantity = table.Column<int>(type: "integer", nullable: false),
                    ObservedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    Actor = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    Reason = table.Column<string>(type: "text", nullable: false),
                    AdoptedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_position_drift_adoptions", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_position_drift_adoptions_IdempotencyKey",
                table: "position_drift_adoptions",
                column: "IdempotencyKey",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "broker_position_observation");

            migrationBuilder.DropTable(
                name: "position_drift_adoptions");
        }
    }
}

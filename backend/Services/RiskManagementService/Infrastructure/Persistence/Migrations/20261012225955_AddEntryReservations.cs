using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace RiskManagementService.Infrastructure.Migrations
{
    /// <summary>
    /// FR-10, #1319, IADR-0529 決定2: 新規建ての予約 <c>entry_reservations</c> を作る（審査が承認した新規建てを、承認行が
    /// 届くまでの間も保有建玉数・日次発注累計・段階資金へ算入するため）。既存の表は変えない。起動時の自動移行で適用される。
    /// </summary>
    public partial class AddEntryReservations : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "entry_reservations",
                columns: table => new
                {
                    DecisionId = table.Column<Guid>(type: "uuid", nullable: false),
                    Symbol = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    Market = table.Column<int>(type: "integer", nullable: false),
                    Side = table.Column<int>(type: "integer", nullable: false),
                    Quantity = table.Column<int>(type: "integer", nullable: false),
                    Price = table.Column<decimal>(type: "numeric", nullable: false),
                    FxRateToBase = table.Column<decimal>(type: "numeric", nullable: false),
                    ApprovedQuantity = table.Column<int>(type: "integer", nullable: false),
                    ApprovedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    StopLossMethod = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_entry_reservations", x => x.DecisionId);
                });

            migrationBuilder.CreateIndex(
                name: "IX_entry_reservations_ApprovedAt",
                table: "entry_reservations",
                column: "ApprovedAt");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "entry_reservations");
        }
    }
}

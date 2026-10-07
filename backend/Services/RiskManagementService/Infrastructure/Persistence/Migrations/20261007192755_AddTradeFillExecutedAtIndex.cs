using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace RiskManagementService.Infrastructure.Migrations
{
    /// <summary>
    /// FR-06, FR-16, #1186, IADR-0506 決定 4: <c>trade_fills.ExecutedAt</c> にインデックスを張る。
    /// <para>
    /// 期間の約定（<c>GET /risk-controls/fills</c>・gRPC <c>GetFills</c>）と期間開始時点の在庫の照会は、約定時刻の範囲
    /// （取引日の外包）を SQL の条件に載せて読む。列・データは変えない（インデックスの作成のみ）。起動時の自動移行で適用される。
    /// </para>
    /// </summary>
    public partial class AddTradeFillExecutedAtIndex : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "IX_trade_fills_ExecutedAt",
                table: "trade_fills",
                column: "ExecutedAt");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_trade_fills_ExecutedAt",
                table: "trade_fills");
        }
    }
}

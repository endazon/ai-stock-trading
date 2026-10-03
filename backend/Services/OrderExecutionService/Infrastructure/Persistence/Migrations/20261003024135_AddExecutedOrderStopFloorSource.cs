using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OrderExecutionService.Infrastructure.Migrations
{
    /// <inheritdoc />
    // 🔴 FR-10, ADR-0049 決定1, #1122, IADR-0486 決定6: 新規建ての損切り幅に下限を掛けてラインを引いた印（出所の序数。1＝Fallback2Pct / 2＝Atr14）を
    // 発注結果に残す。**列の追加だけ**である（integer NULL）。既存行は null ＝「分からない」で読まれ、既存の S1 への下限の遡及は
    // 従来どおり「ラインを引いた価格から 2% 以上離れているか」で判定する（既存行を埋める移行はしない）。
    public partial class AddExecutedOrderStopFloorSource : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "StopFloorSource",
                table: "executed_orders",
                type: "integer",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "StopFloorSource",
                table: "executed_orders");
        }
    }
}

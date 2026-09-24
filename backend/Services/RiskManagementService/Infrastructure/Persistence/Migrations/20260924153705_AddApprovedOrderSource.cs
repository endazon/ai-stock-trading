using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace RiskManagementService.Infrastructure.Migrations
{
    /// <summary>
    /// FR-10, #935, IADR-0394 決定6: <c>approved_orders.Source</c>（承認行の由来）を足す。
    /// <para>
    /// 🔴 <b>既存行は <c>null</c> のまま残す（埋め戻さない）。</b> 本列の追加前に記録された決済が損切りだったかは
    /// 台帳からは分からない。<c>null</c> は「由来が記録されていない」＝不明として読まれ、当日の決済なら
    /// 同じ方向の新規建てを止める側へ倒れる（「損切りではない」と推定しない）。
    /// </para>
    /// </summary>
    public partial class AddApprovedOrderSource : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "Source",
                table: "approved_orders",
                type: "integer",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Source",
                table: "approved_orders");
        }
    }
}

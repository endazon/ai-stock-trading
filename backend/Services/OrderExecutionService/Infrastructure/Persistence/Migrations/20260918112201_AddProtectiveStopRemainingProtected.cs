using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OrderExecutionService.Infrastructure.Migrations
{
    /// <inheritdoc />
    // FR-10, ADR-0040 決定1（S1）, #820 の 4 巡目監査, IADR-0344 追記(4): 保護記録が「残保護数量」を状態として持つ。
    // 既存行（すべて S0）は逆指値が覆っている数量＝Quantity を初期値にする（null のままでも
    // ProtectedQuantity が Quantity へ落ちるが、値を持たせて「確定済み」を明示する）。
    public partial class AddProtectiveStopRemainingProtected : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "RemainingProtected",
                table: "protective_stop_orders",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "StalledNotifiedAt",
                table: "protective_stop_orders",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.Sql(
                @"UPDATE protective_stop_orders SET ""RemainingProtected"" = ""Quantity"" WHERE ""RemainingProtected"" IS NULL;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "RemainingProtected",
                table: "protective_stop_orders");

            migrationBuilder.DropColumn(
                name: "StalledNotifiedAt",
                table: "protective_stop_orders");
        }
    }
}

using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OrderExecutionService.Infrastructure.Migrations
{
    /// <inheritdoc />
    // 🔴 FR-10, FR-09, UC-06, #1395, IADR-0560: ソフトウェア逆指値（S1）が損切りの前にこの決済の取消を求めた時刻（取消を送る前に書く）。
    // **列の追加だけ**である（timestamptz NULL）。既存行は null ＝S1 は取消を求めていない（従来どおり「手仕舞いが約定せず終了」を出す側）。
    public partial class AddExecutedOrderSoftwareStopCancelRequestedAt : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "SoftwareStopCancelRequestedAt",
                table: "executed_orders",
                type: "timestamp with time zone",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "SoftwareStopCancelRequestedAt",
                table: "executed_orders");
        }
    }
}

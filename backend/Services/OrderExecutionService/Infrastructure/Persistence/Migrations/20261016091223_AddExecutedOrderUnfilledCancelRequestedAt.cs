using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OrderExecutionService.Infrastructure.Migrations
{
    /// <inheritdoc />
    // 🔴 FR-10, FR-19, ADR-0067 決定1・2, #1339, IADR-0539: 判断由来の決済の指値を系が取り消すと決めた時刻（取消を送る前に書く）。
    // **列の追加だけ**である（timestamptz NULL）。既存行は null ＝系が取消を求めたことが無い（既定は無効なので、配備しても書かれない）。
    public partial class AddExecutedOrderUnfilledCancelRequestedAt : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "UnfilledCancelRequestedAt",
                table: "executed_orders",
                type: "timestamp with time zone",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "UnfilledCancelRequestedAt",
                table: "executed_orders");
        }
    }
}

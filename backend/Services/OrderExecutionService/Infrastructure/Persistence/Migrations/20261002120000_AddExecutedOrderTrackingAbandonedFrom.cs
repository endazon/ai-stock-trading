using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OrderExecutionService.Infrastructure.Migrations
{
    /// <inheritdoc />
    // 🔴 FR-10, FR-11, #1048, IADR-0481 決定3: 約定追跡の打ち切りを監査へ記録した「追跡の起点」を発注結果に残す。
    // **列の追加だけ**である（timestamptz NULL）。既存行は null ＝「打ち切りを記録していない」で読まれ、追跡上限を過ぎた
    // 非終端の既存行は、配備後の最初の巡回で 1 回ずつ打ち切りが記録される（既存行を埋める移行はしない）。
    public partial class AddExecutedOrderTrackingAbandonedFrom : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "TrackingAbandonedFrom",
                table: "executed_orders",
                type: "timestamp with time zone",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "TrackingAbandonedFrom",
                table: "executed_orders");
        }
    }
}

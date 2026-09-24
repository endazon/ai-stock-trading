using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OrderExecutionService.Infrastructure.Migrations
{
    /// <inheritdoc />
    // FR-10, ADR-0040 決定1（S1）, #833 項目2, IADR-0344 追記(14): S1 の決済が続けて売れないときの**行ごとの待ち時間**。
    // CloseFailures（連続失敗の回数）・NextCloseAttemptAt（次の成行を送ってよい最早時刻）・
    // LastTriggerSeenAt（最後に受けた到達の検知時刻。間が空いた到達を新しい窓として扱う）。
    // **列の追加だけ**である。既存行は 0 / null / null（待ち時間なし・次の到達は新しい窓）で読まれ、行の書き換えは無い。
    public partial class AddSoftwareStopCloseBackoff : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "CloseFailures",
                table: "protective_stop_orders",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "LastTriggerSeenAt",
                table: "protective_stop_orders",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "NextCloseAttemptAt",
                table: "protective_stop_orders",
                type: "timestamp with time zone",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "CloseFailures",
                table: "protective_stop_orders");

            migrationBuilder.DropColumn(
                name: "LastTriggerSeenAt",
                table: "protective_stop_orders");

            migrationBuilder.DropColumn(
                name: "NextCloseAttemptAt",
                table: "protective_stop_orders");
        }
    }
}

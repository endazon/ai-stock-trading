using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OrderExecutionService.Infrastructure.Migrations
{
    /// <inheritdoc />
    // FR-10, ADR-0040 決定1（S1）, #820 の 8 巡目監査, IADR-0344 追記(8): 超過が**消えた**ことの連続観測回数（確定と対称の失効）と、
    // **帳簿では守っているのに 1 株も動かせない**状態になった時刻・Critical で知らせた時刻（1 行 1 回）。
    // 追記(7) の観測値は単調で確定か完了でしか消えず、建玉照会が 1 巡回だけ過少に返っただけで
    // その行の損切りが**二度と出なくなっていた**（行は Active・帳簿も無傷なのでどの検査も通る）。
    // 既存行は 0 / null（失効の観測なし・実効 0 の記録なし）で始まる。
    public partial class AddProtectiveStopSuspension : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "ExternalReductionAbsences",
                table: "protective_stop_orders",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "ProtectionSuspendedNotifiedAt",
                table: "protective_stop_orders",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "ProtectionSuspendedSince",
                table: "protective_stop_orders",
                type: "timestamp with time zone",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ExternalReductionAbsences",
                table: "protective_stop_orders");

            migrationBuilder.DropColumn(
                name: "ProtectionSuspendedNotifiedAt",
                table: "protective_stop_orders");

            migrationBuilder.DropColumn(
                name: "ProtectionSuspendedSince",
                table: "protective_stop_orders");
        }
    }
}

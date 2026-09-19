using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OrderExecutionService.Infrastructure.Migrations
{
    /// <inheritdoc />
    // FR-10, ADR-0040 決定1（S1）, #820 の 10 巡目監査, IADR-0344 追記(9) 決定3:
    // 「**どの保護記録も主張していない建玉**」を最後に知らせた株数と時刻。群につき 1 行（S1 の行のうち
    // 作成が最も新しいもの）が代表して持ち、**同じ状態で毎巡回鳴らさない**ため・**再起動で再送しない**ために使う。
    // 武装の前提条件は武装の時点しか見ないため、武装より後に他人の建玉が現れる経路と、
    // 受理後に 0 約定で取り消された決済の残りは、これまでどのイベントも出さないまま無保護で残っていた。
    // 既存行は null / null（まだ知らせていない）で始まる。
    public partial class AddProtectiveStopUnattributedNotice : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "UnattributedNotifiedAt",
                table: "protective_stop_orders",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "UnattributedNotifiedQuantity",
                table: "protective_stop_orders",
                type: "integer",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "UnattributedNotifiedAt",
                table: "protective_stop_orders");

            migrationBuilder.DropColumn(
                name: "UnattributedNotifiedQuantity",
                table: "protective_stop_orders");
        }
    }
}

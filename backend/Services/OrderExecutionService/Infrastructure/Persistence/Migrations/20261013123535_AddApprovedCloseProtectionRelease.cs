using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OrderExecutionService.Infrastructure.Migrations
{
    /// <inheritdoc />
    // 🔴 FR-10, #1330, IADR-0534 決定1・決定2: 確定した保護の縮小を「承認済みの決済の約定」と「外部要因」に分けるための 2 列。**列の追加だけ**である。
    //   - protective_stop_orders.ExternalReductionObservedAt（timestamptz NULL）: 今の未確定の観測値を最初に観測した巡回の時刻（窓の前の端）。
    //     既存行は null ＝確定の時刻で代える。
    //   - executed_orders.ProtectionReleasedQuantity（integer NOT NULL 既定 0）: 承認経路の決済の約定のうち、保護の縮小へ既に帰属させた株数。
    //     🔴 **既存の決済行は FilledQuantity へ揃える**——過去の約定はこれまでの確定（すべて外部要因として扱われた）で既に吸収されており、
    //     揃えないと、これからの外部要因の減少を過去の利確の約定で「承認済みの決済」と誤って説明してしまう（人手で確かめるべき減少を隠す側）。
    public partial class AddApprovedCloseProtectionRelease : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "ExternalReductionObservedAt",
                table: "protective_stop_orders",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "ProtectionReleasedQuantity",
                table: "executed_orders",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            // 既存の行の約定は既に吸収済みとして扱う（上の注記）。新規建ての行は使わないが、揃えても害は無い（決済の行だけを対象にする）。
            // 🔴 "PositionEffect" = 1 は、列 executed_orders.PositionEffect が enum の序数（integer）で保存され、
            // PositionEffect.Close の序数が 1（Open=0 / Close=1。Shared.Contracts/Trading/PositionEffect.cs）であることを前提にしている。
            // 序数が変われば対象を取り違える（ApprovedCloseReleasePersistenceTests が序数を固定している）。
            migrationBuilder.Sql(
                "UPDATE executed_orders SET \"ProtectionReleasedQuantity\" = \"FilledQuantity\" "
                + "WHERE \"PositionEffect\" = 1 AND \"FilledQuantity\" > 0;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ExternalReductionObservedAt",
                table: "protective_stop_orders");

            migrationBuilder.DropColumn(
                name: "ProtectionReleasedQuantity",
                table: "executed_orders");
        }
    }
}

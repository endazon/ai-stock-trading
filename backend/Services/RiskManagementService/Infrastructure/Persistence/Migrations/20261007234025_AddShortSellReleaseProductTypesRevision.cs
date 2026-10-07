using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace RiskManagementService.Infrastructure.Migrations
{
    /// <summary>
    /// FR-19, FR-20, ADR-0034 決定5 契機2, #1220, IADR-0511: 空売り実弾解禁 verdict の相乗り列に
    /// <c>ShortSellReleaseProductTypesRevision</c>（発行時点の商品種別設定の改訂番号）を足す。
    /// <para>
    /// nullable。段階遷移の行は null。**既存の verdict の行も null のまま残す（書き換えない）**——判定は番号の無い
    /// verdict を「変わっていない」と読まず <c>ProductTypesUnknown</c>（無効）へ倒すため、デプロイ直後に既存の
    /// verdict は無効になり、解禁を続けるには利用者の再発行が要る（発注審査は今日まだ verdict を受け取らないため、
    /// 発注の挙動は変わらない。IADR-0281 決定6）。起動時の自動移行で適用される。
    /// </para>
    /// </summary>
    public partial class AddShortSellReleaseProductTypesRevision : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "ShortSellReleaseProductTypesRevision",
                table: "stage_transitions",
                type: "bigint",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ShortSellReleaseProductTypesRevision",
                table: "stage_transitions");
        }
    }
}

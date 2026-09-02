using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace RiskManagementService.Infrastructure.Migrations
{
    // FR-20, ADR-0016 決定14（2026-08-07 確定）, #388, IADR-0281: 空売り実弾解禁 verdict の列を足す。
    //
    // stage_transitions の 2 列は**verdict の相乗り**である（裁定「別記録にしない」。専用テーブルは作らない）。
    // 段階遷移の行では null であるため nullable。stage_performance の 2 列は backtest verdict 由来の
    // 判定入力であり、既定（false / 空文字）は fail-safe＝空売りは解禁されない。
    /// <inheritdoc />
    public partial class AddShortSellReleaseVerdict : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ShortSellReleaseSourceFingerprint",
                table: "stage_transitions",
                type: "character varying(512)",
                maxLength: 512,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ShortSellReleaseStrategyId",
                table: "stage_transitions",
                type: "character varying(256)",
                maxLength: 256,
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "BacktestIncludesShortSelling",
                table: "stage_performance",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "BacktestStrategyId",
                table: "stage_performance",
                type: "character varying(256)",
                maxLength: 256,
                nullable: false,
                defaultValue: "");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ShortSellReleaseSourceFingerprint",
                table: "stage_transitions");

            migrationBuilder.DropColumn(
                name: "ShortSellReleaseStrategyId",
                table: "stage_transitions");

            migrationBuilder.DropColumn(
                name: "BacktestIncludesShortSelling",
                table: "stage_performance");

            migrationBuilder.DropColumn(
                name: "BacktestStrategyId",
                table: "stage_performance");
        }
    }
}

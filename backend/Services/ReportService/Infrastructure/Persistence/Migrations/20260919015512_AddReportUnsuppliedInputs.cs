using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ReportService.Infrastructure.Migrations
{
    // FR-06, FR-07, #840, IADR-0352 決定 5: 供給が届かないまま生成された入力の記録（ReportInput 名のカンマ区切り）。
    // 既存行は NULL＝欠けた入力の記録なし（読み出し時は空列）。
    /// <inheritdoc />
    public partial class AddReportUnsuppliedInputs : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "UnsuppliedInputs",
                table: "reports",
                type: "character varying(1024)",
                maxLength: 1024,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "UnsuppliedInputs",
                table: "reports");
        }
    }
}

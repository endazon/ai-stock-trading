using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OrderExecutionService.Infrastructure.Migrations
{
    /// <inheritdoc />
    // 🔴 FR-10, #833 項目3, IADR-0396: 保護記録の楽観並行の版番号（アプリ側で加算・EF の並行トークン）。
    // **列の追加だけ**である（integer NOT NULL DEFAULT 0）。既存行は版 0 で読まれ、行の書き換え・インデックスの作り直しは無い。
    // 以後の更新は「WHERE Version = 読んだ時点の版」で行われ、古い写しからの上書きは 0 行で止まる。
    public partial class AddProtectiveStopVersion : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "Version",
                table: "protective_stop_orders",
                type: "integer",
                nullable: false,
                defaultValue: 0);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Version",
                table: "protective_stop_orders");
        }
    }
}

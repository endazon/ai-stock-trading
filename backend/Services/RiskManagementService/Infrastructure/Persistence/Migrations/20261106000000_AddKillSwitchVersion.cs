using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace RiskManagementService.Infrastructure.Migrations
{
    /// <summary>
    /// FR-10, #1534, IADR-0620: kill switch の単一行 <c>kill_switch</c> に、書くたびに 1 つ進む版（<c>Version</c>・並行トークン）を足す。
    /// **追加のみ・既存行は 0**（照会の版は "0" から始まり、次の書き込みで 1 になる）。起動時の自動移行で適用される。
    /// </summary>
    public partial class AddKillSwitchVersion : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "Version",
                table: "kill_switch",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Version",
                table: "kill_switch");
        }
    }
}

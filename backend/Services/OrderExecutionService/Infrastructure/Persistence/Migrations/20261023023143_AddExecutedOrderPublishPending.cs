using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OrderExecutionService.Infrastructure.Migrations
{
    /// <inheritdoc />
    // 🔴 NFR, FR-05, FR-10, #1442, IADR-0573 決定2: 約定追跡が記録を更新したが、その状態の OrderExecuted をまだ発行し終えていない印。
    // **列の追加だけ**である（boolean NOT NULL DEFAULT false）。既存行は false ＝発行済みとして扱う（過去の約定を撃ち直さない）。
    public partial class AddExecutedOrderPublishPending : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "OrderExecutedPublishPending",
                table: "executed_orders",
                type: "boolean",
                nullable: false,
                defaultValue: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "OrderExecutedPublishPending",
                table: "executed_orders");
        }
    }
}

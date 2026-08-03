using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AiStockTrading.CostControl.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddProcessedMessagesRetentionIndex : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "IX_processed_messages_ProcessedAt",
                table: "processed_messages",
                column: "ProcessedAt");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_processed_messages_ProcessedAt",
                table: "processed_messages");
        }
    }
}

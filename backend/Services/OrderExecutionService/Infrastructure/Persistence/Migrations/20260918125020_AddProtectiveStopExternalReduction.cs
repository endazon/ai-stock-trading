using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OrderExecutionService.Infrastructure.Migrations
{
    /// <inheritdoc />
    // FR-10, ADR-0040 決定1（S1）, #820 の 5 巡目監査, IADR-0344 追記(5): 外部要因で削ったが**まだ確定していない**株数と、
    // その連続観測回数。建玉照会は銘柄単位の純額でしかなく 1 巡回だけ過少に返り得るため、1 回の観測で行を失わせない。
    // 既存行は 0（未確定の削りなし）で始まる。
    public partial class AddProtectiveStopExternalReduction : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "ExternalReductionObservations",
                table: "protective_stop_orders",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "PendingExternalReduction",
                table: "protective_stop_orders",
                type: "integer",
                nullable: false,
                defaultValue: 0);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ExternalReductionObservations",
                table: "protective_stop_orders");

            migrationBuilder.DropColumn(
                name: "PendingExternalReduction",
                table: "protective_stop_orders");
        }
    }
}

using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CostControlService.Infrastructure.Migrations
{
    /// <summary>
    /// 🔴 NFR-13, ADR-0057 決定3, #1461, IADR-0584 決定2: 既存の行の費用計上月（UTC 由来の "yyyy-MM"）を、記録の時刻（RecordedAt）の
    /// <b>JST の年月</b>で引き直す。当月の累計に JST の 1 日 0:00〜9:00 の計上が入るようにする（受容ではなく補正）。
    /// 記録の時刻は計上の時刻そのもの（EfCostLedger.Record の at ＝ clock.UtcNow）なので、引き直しは決定的である。スキーマは変えない。
    /// </summary>
    public partial class RekeyCostEntriesMonthToJst : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                """UPDATE cost_entries SET "Month" = to_char("RecordedAt" AT TIME ZONE 'Asia/Tokyo', 'YYYY-MM') """
                + """WHERE "Month" <> to_char("RecordedAt" AT TIME ZONE 'Asia/Tokyo', 'YYYY-MM');""");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                """UPDATE cost_entries SET "Month" = to_char("RecordedAt" AT TIME ZONE 'UTC', 'YYYY-MM') """
                + """WHERE "Month" <> to_char("RecordedAt" AT TIME ZONE 'UTC', 'YYYY-MM');""");
        }
    }
}

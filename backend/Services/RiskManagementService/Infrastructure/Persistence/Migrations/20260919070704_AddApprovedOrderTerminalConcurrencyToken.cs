using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace RiskManagementService.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// FR-09, FR-10, UC-06, #847, IADR-0357: <c>approved_orders.TerminalAt</c> を<b>並行トークン</b>にする。
    /// <para>
    /// 🔴 <b>Up / Down が空なのは書き忘れではない。</b> 並行トークンは PostgreSQL 側の DDL を 1 行も要さない
    /// （列はそのままで、EF が UPDATE の WHERE 句へ元の値を足すだけである）。実際、
    /// <c>dotnet ef migrations script</c> の出力は <c>__EFMigrationsHistory</c> への INSERT 1 行だけである。
    /// </para>
    /// <para>
    /// ではなぜ置くのか —— <b>モデルとスナップショットを一致させるため</b>である。
    /// 🔴 <c>dotnet ef migrations has-pending-model-changes</c> は<b>この差分を検出しない</b>（DDL を生まないため。実測）。
    /// 置かないまま放置すると、次に誰かが <c>migrations add</c> したときに<b>無関係な移行へこの注釈が黙って混ざる</b>。
    /// </para>
    /// <para>
    /// 対象は <c>approved_orders</c> の <c>TerminalAt</c> <b>だけ</b>である。
    /// 🔴 <c>order_activity</c> にも同名の列があるが<b>規約が違い</b>（あちらは
    /// <c>EfOrderActivityStore</c> が<b>無条件に上書きする</b>）、トークンを付けると正常な上書きが競合例外になり、
    /// 射影の終端判定（<c>EfWorkingEntryOrderSource</c>）が壊れる。付けていないことはスナップショットで確認済みである。
    /// </para>
    /// </summary>
    public partial class AddApprovedOrderTerminalConcurrencyToken : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // 意図的に空（上の要約を参照）。並行トークンは DDL を伴わない。
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // 意図的に空（上の要約を参照）。
        }
    }
}

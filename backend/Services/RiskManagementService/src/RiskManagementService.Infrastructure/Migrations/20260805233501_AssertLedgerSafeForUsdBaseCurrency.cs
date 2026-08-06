using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AiStockTrading.RiskManagement.Infrastructure.Migrations
{
    /// <summary>
    /// FR-10, FR-17, #364, 06_technical/05_trading-assumptions §3, IADR-0152 決定5:
    /// 判定の基準通貨を JPY から USD へ移行するにあたり、**既存の台帳行の意味が変わらないこと**を移行時に検査する。
    /// <para>
    /// <c>approved_orders.FxRateToBase</c> は「ローカル通貨 1 単位あたりの**基準通貨**額」であり、基準通貨が
    /// 変われば同じ数値が別の意味になる。<c>PriceInBase</c> は永続化されない計算値（<c>Price × FxRateToBase</c>）
    /// であるため、危険は本列（と市場）に限局する。移行後も意味が変わらない行は
    /// **米国市場（<c>Market = 1</c>）かつ <c>FxRateToBase</c> が 1 または NULL** の行だけである。
    /// </para>
    /// <para>
    /// それ以外の行が 1 行でもあれば <c>Up</c> は例外を送出して**移行を止める**。7 年保持で破棄できない
    /// 監査証跡・業務台帳（[#339](https://github.com/endazon/ai-stock-trading/issues/339) /
    /// [#346](https://github.com/endazon/ai-stock-trading/issues/346)）に「JPY 建ての行と USD 建ての行が
    /// 判別不能なまま混在する」状態を作るより、移行を止めて人間に判断させる方が安全である
    /// （統制のフェイルクローズ規律・IADR-0131 決定4 と同型）。
    /// </para>
    /// <para>
    /// スキーマは変更しない（データも書き換えない）。したがってモデルスナップショットは不変であり、
    /// <c>Down</c> は何もしない（<c>Up</c> が状態を変えないため、巻き戻す対象が存在しない）。
    /// </para>
    /// </summary>
    public partial class AssertLedgerSafeForUsdBaseCurrency : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Market は int で永続化される（Japan = 0 / UnitedStates = 1）。
            migrationBuilder.Sql("""
                DO $$
                DECLARE unsafe_rows bigint;
                BEGIN
                    SELECT count(*) INTO unsafe_rows
                    FROM approved_orders
                    WHERE NOT ("Market" = 1 AND ("FxRateToBase" IS NULL OR "FxRateToBase" = 1));

                    IF unsafe_rows > 0 THEN
                        RAISE EXCEPTION
                            '基準通貨の USD 移行（#364 / IADR-0152）を中止しました: approved_orders に、移行で意味が変わる行が % 件あります。FxRateToBase は「ローカル通貨 1 単位あたりの基準通貨額」であり、基準通貨を反転すると同じ数値が別の意味になります。台帳を退避・再解釈してから移行してください。',
                            unsafe_rows;
                    END IF;
                END $$;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Up は検査のみでスキーマ・データを変更しないため、巻き戻す対象が無い（意図的な no-op）。
        }
    }
}

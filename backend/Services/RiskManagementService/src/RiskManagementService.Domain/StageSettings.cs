using AiStockTrading.Shared.Contracts.Trading;
using AiStockTrading.Shared.Kernel.Trading;

namespace AiStockTrading.RiskManagement.Domain;

// FR-20, #334, IADR-0140: 段階ごとの**既定の発注先**（動作モード）と発注可能額の上限。
//
// 計画（FR-20・INDEX 決定 46）は「段階が定める動作モードは**既定の組み合わせを示すにとどまる**」と定める。
// よって本レコードの <see cref="Mode"/> は「その段階で通常選ぶ発注先」であり、**現在の発注先そのものではない**
// （現在値は RiskManagementSettings.BrokerProvider が独立の軸として保持する）。
//
// **プロパティ名 `Mode` は据え置く**（型のみ TradeMode → BrokerProvider）。本レコードは設定ストアの JSON
// （単一行）と HTTP 応答の双方で往復しており、名前を変えると旧行の `"mode"` が黙って enum 既定値 0 へ落ちる。
// 0（InternalPaper）は RiskEvaluator の実弾判定を**通してしまう**側の値であり、フェイルオープンになる
// （IADR-0140 決定3）。序数 0 / 1 は旧 TradeMode の Paper / Live と同義のため、型の入れ替えだけなら
// 既存行・既存イベントの意味は変わらない。
public record StageSettings(
    TradingStage Stage,
    BrokerProvider Mode,
    decimal CapitalCapRatio)
{
    /// <summary>
    /// FR-20, #333, 05_trading-assumptions §5「運用段階（Stage）」: 段階の発注可能額を equity（総資金）から解決する。
    /// <para>
    /// 計画は Stage 2 の発注可能額を「**総資金の 30%（$900）**。口座には総資金 $3,000 を入れ、**発注可能額を
    /// システム側の統制で 30% に制限する**（口座への入金額は制限しない）」と定める。固定額で持つと増資のたびに
    /// 書き換えが要り、書き換え漏れが「資金だけ増えて上限が据え置き」を生む
    /// （<see cref="RiskLimitSettings"/> と同じ規律。IADR-0130 決定1）。
    /// </para>
    /// <para>
    /// 比率から金額への解決は本メソッドだけを通す（呼び出し側で <c>equity × 比率</c> と書かない。
    /// equity の定義＝どの時点の値かが呼び出し側ごとにぶれるため）。
    /// </para>
    /// </summary>
    /// <param name="equity">
    /// 判定に用いる自己資金（equity）＝**前営業日終値時点の評価額**（計画 §5 注記）。
    /// 実装上は <c>PortfolioSnapshot.Capital</c>（当日中は不変）。
    /// </param>
    public decimal OrderableCapFor(decimal equity) => equity * CapitalCapRatio;
}

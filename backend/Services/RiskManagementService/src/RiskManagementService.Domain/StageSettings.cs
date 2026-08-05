using AiStockTrading.Shared.Contracts.Trading;

namespace AiStockTrading.RiskManagement.Domain;

// FR-20, ADR-0008: 運用段階（段階ゲート）。遷移は合格・撤退基準に基づき利用者の承認で行う。
// 数値は明示的に昇順で固定する。段階ゲートの遷移ロジック（StageGate: 昇格＝現段階の 1 段上、
// 方向判定＝数値比較）と設定の直列化（既定 JSON は enum を数値で往復）が、この連続昇順の割り当てに
// 依存するため、値の挿入・並べ替えを行わない（追加は末尾に連番で行う）。
//
// FR-20, #333: Stage 1 の呼称は **Simulate**（moomoo `SIMULATE`＝OpenD 経由のデモ環境）である。
// 内蔵 `paper`（擬似約定）は**デバッグ用**であり本段階の検証手段としない（06_daytrading-review §4 表）。
// **数値 1 は不変**である（旧 `Stage1Paper` と同値。永続化された遷移履歴の意味を変えない）。
// 発注先（Broker Provider）を段階から分離する 2 軸化は #334 の担当であり、本 enum は段階の軸のみを表す。
public enum TradingStage
{
    Stage0Verification = 0,
    Stage1Simulate = 1,
    Stage2MinimalLive = 2,
    Stage3ScaledLive = 3,
}

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

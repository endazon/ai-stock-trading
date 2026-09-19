extern alias RiskManagementWorker;

using RiskManagementWorker::RiskManagementService.Domain;
using AiStockTrading.Shared.Contracts.Trading;

namespace TradeDecisionService.Features.TradeDecision;

// FR-04, FR-10, IADR-0003/0017/0029: サイジングに必要な文脈（資金・リスク設定・段階/日次残枠・連敗/DD・動作モード）。
// 実データはリスク管理（#12）の GET /risk-controls/sizing-context を同期照会して供給する（HttpSizingContextProvider）。
// 同期 HTTP を sync-over-async にしないため非同期とする。依存先障害時は残枠 0 の安全既定（＝取引しない）に倒す。
public interface ISizingContextProvider
{
    Task<SizingContext> GetContextAsync(CancellationToken cancellationToken = default);
}

// サイジング文脈。availableCapital は段階資金残枠と日次発注残枠の小さい方を用いる（IADR-0017）。
// FR-10, #869, ADR-0041 決定2, IADR-0354: 🔴 **Capital / 残枠は null＝未供給**（ブローカーの口座照会に
// 由来する基準資金が取れていない）。**0 と読まない**——0 は「枠を使い切った」であり、null は「分からない」である。
// いずれの場合もサイジングは数量 0 ＝ 見送りに倒れ、発注審査側も CapitalBaselineUnavailable で新規建てを止める。
public record SizingContext(
    decimal? Capital,
    decimal? StageCapitalRemaining,   // CapitalCap − InvestedCapital（段階資金の残枠）
    decimal? DailyOrderRemaining,     // MaxDailyOrderAmount − DailyOrderedAmount（当日発注の残枠）
    int ConsecutiveLosses,
    decimal DrawdownRatio,
    BrokerProvider Mode,
    RiskLimitSettings Limits,
    // FR-04, FR-10, ADR-0040 決定1, #854, IADR-0351 決定1: 損切りの実行機構の**設定**（S0〜S3）。判断プロンプトの
    // 「保護の状態」の供給元。🔴 **null＝未供給（不明）**であり S0 と読まない——項目を持たない旧応答・照会失敗の
    // 安全既定（SafeDefault）・プレースホルダはいずれも null になり、プロンプトは「不明」と明示する。
    StopLossExecutionMethod? StopLossMethod = null);

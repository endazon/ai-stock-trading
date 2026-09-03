using AiStockTrading.Shared.Contracts.Trading;
using AiStockTrading.Shared.Kernel.Trading;

namespace RiskManagementService.Features.RiskManagement.GetRiskStatus;

// FR-10, UC-07, ADR-0009: `/status`（詳細設計07）が参照する稼働状態の集約ビュー。表示専用（設定変更は含まない）。
// 3 統制（kill switch / 日次損失ロックアウト / 一時停止）はいずれも新規建てのみを止める。優先順位は表示用で、
// 判定は OR（いずれか成立で NewEntriesBlocked=true）。
public sealed record RiskStatusView(
    // --- 3 統制の状態（優先順位: kill switch > 日次損失ロックアウト > 一時停止） ---
    bool KillSwitchEngaged,
    bool DailyLossLockoutActive,
    DateOnly? LockoutReleaseOn,
    bool TradingPaused,
    // 成立中で最優先の統制（表示の見出し用）。いずれも不成立なら None。
    ActiveTradingControl ActiveControl,
    // 新規建てが停止しているか（3 統制の OR）。手仕舞い・損切りは本フラグに関わらず継続する。
    bool NewEntriesBlocked,
    // --- 運用段階 ---
    TradingStage Stage,
    // FR-20, FR-12, SC-03, #334, IADR-0140: **発注先**（Broker Provider）。運用段階とは独立した軸であり、
    // 画面は 1 行に混ぜずに並べて表示する（05_screens「表示規約（共通）」）。SC-03 は参照専用。
    BrokerProvider BrokerProvider,
    // --- 当日損益（実現＋含み） ---
    decimal DailyRealizedPnl,
    decimal UnrealizedPnl,
    decimal DailyPnl,
    // --- 上限使用率の入力（残枠・上限） ---
    decimal Capital,
    decimal DailyOrderedAmount,
    // FR-10, FR-20, SC-02, #334: 1 注文あたりの発注金額上限（equity から解決した実額）。
    // 実弾切替の警告モーダル③「現在の equity と、それに対する統制値の実額」の提示に用いる。
    decimal MaxOrderAmount,
    decimal MaxDailyOrderAmount,
    decimal DrawdownRatio,
    decimal MaxDrawdownRatio,
    // --- ポジション ---
    int OpenPositionCount,
    int MaxOpenPositions);

// ADR-0009: 成立中で最優先の統制。重い順（kill switch > 日次損失ロックアウト > 一時停止）。
public enum ActiveTradingControl
{
    None,
    KillSwitch,
    DailyLossLockout,
    Pause,
}

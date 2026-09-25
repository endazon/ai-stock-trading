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
    // FR-10, #869, ADR-0041 決定2, IADR-0354: equity（基準資金）は**ブローカーの口座照会に由来する**。
    // 🔴 **null は「口座を照会できていない」を意味する**（新規建ては止まっている）。
    // 画面は 0 や「—」ではなく**未供給の表示**を出す（05_screens「供給が無い値の表示規約」・IADR-0162）。
    decimal? Capital,
    // FR-10, SC-03, #829, #832, IADR-0346 決定2: 当日の新規建ての**発注代金**の合計。約定に加えて、当日承認した
    // **未約定の新規建て注文**（取消・失効・見送りで終端していないもの）の残数量を承認価格で含む（計画 FR-10 の定義）。
    decimal DailyOrderedAmount,
    // FR-10, FR-20, SC-02, #334: 1 注文あたりの発注金額上限（equity から解決した実額）。
    // 実弾切替の警告モーダル③「現在の equity と、それに対する統制値の実額」の提示に用いる。
    // #869: equity が未供給なら**実額は解決できない**ため null（0 を出すと「上限 0」と読める）。
    decimal? MaxOrderAmount,
    decimal? MaxDailyOrderAmount,
    decimal DrawdownRatio,
    decimal MaxDrawdownRatio,
    // --- ポジション ---
    // FR-10, SC-03, #829, #832, IADR-0346 決定2: 保有建玉数。約定済みの建玉に加えて、**建玉の無い（銘柄, 市場）へ出した
    // 未約定の新規建て注文**も 1 件として含む（既存建玉への建て増しは数を増やさない）。
    int OpenPositionCount,
    int MaxOpenPositions,
    // FR-10, SC-03, ADR-0040 決定1, #819, IADR-0342 決定2: **損切りの実行機構**（S0〜S3）。計画は「どの手法を
    // 選んでいるかを SC-03 に出す」と定める（どの手法で走ったかが読めなければ観測結果を解釈できない）。
    // 表示は #823。末尾の既定値つき項目として足し、既存の生成箇所を変えない。
    StopLossExecutionMethod StopLossMethod = StopLossExecutionMethod.BrokerStopOrder,
    // FR-11, FR-06, SC-03, ADR-0041 決定 1, #870, IADR-0360 決定 5: **当日のシステム外売買の取り込み件数**。
    // 計画は「統制状態の参照に、当期の取り込みの件数を出す」と定める（参照のみ。本画面から取り込みは行わない）。
    // 🔴 **0 件は「取り込みが無かった」という事実**である（未供給ではない）——台帳は常に読めるため未供給の状態が無い。
    // 🔴 これらの取り込みは**実現損益を記録していない**（不明）。したがって当日損益・基準資金は実際の口座と
    // ずれ得る。件数を出す目的は「ずれていることを画面から知る手段」を与えることである。
    // 「当日」は**取り込みの市場の現地取引日**で判定する（当日実現損益・日次発注枠と同じ境界。IADR-0246）。
    // 末尾の既定値つき項目として足し、既存の生成箇所を変えない。
    int DriftAdoptionCountToday = 0);

// ADR-0009: 成立中で最優先の統制。重い順（kill switch > 日次損失ロックアウト > 一時停止）。
public enum ActiveTradingControl
{
    None,
    KillSwitch,
    DailyLossLockout,
    Pause,
}

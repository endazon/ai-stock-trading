using RiskManagementService.Domain;
using AiStockTrading.Shared.Kernel.Trading;

namespace RiskManagementService.Features.RiskManagement;

// FR-20, UC-06, IADR-0070: 段階ゲートの現況（GET /risk-controls/stage-gate の応答）。
// 現段階・その設定（モード・資金上限）・遷移履歴（監査）・昇格評価・撤退評価をまとめて返す。
public sealed record StageGateStatus(
    TradingStage CurrentStage,
    StageSettings CurrentSettings,
    IReadOnlyList<StageTransition> History,
    PromotionAssessment Promotion,
    WithdrawalAssessment Withdrawal,
    // FR-20, SC-03, #334, IADR-0142: Stage 1 の進捗（**moomoo SIMULATE の実績のみ**）と、
    // 内蔵 paper 稼働により算入されなかった営業日数。画面は「経過 42 / 60 営業日
    // （paper 稼働により 3 日を除外）」と併記する——**算入されなかった期間があること自体**が
    // 見えないと、進捗の数字を説明できなくなる（05_screens SC-03）。
    Stage1Progress Stage1Progress,
    // 目標営業日数 60 / 最小取引件数 100 / 打ち切り 120（06_daytrading-review §4.1〜§4.3）。
    // 画面が閾値を直書きすると計画の改訂に追随しないため、現況と同じ応答で返す。
    Stage1GateCriteria Stage1Criteria,
    // FR-20, SC-03, ADR-0016 決定14, #388, IADR-0281 決定5: 空売り実弾解禁 verdict の現況。
    ShortSellReleaseState ShortSellRelease);

/// <summary>
/// FR-20, SC-03, ADR-0016 決定14（2026-08-07 確定）, #388, IADR-0281 決定5:
/// 空売り実弾解禁 verdict の現況（<c>GET /risk-controls/stage-gate</c> の応答の一部）。
/// <para>
/// 🔴 **拒否理由 <c>StageShortSellReleaseUnmet</c> は「verdict 無効」と「その他の解禁条件未充足」を区別しない。**
/// 区別を担うのが本レコードである——状態（欠落 / 期限切れ / 情報源の変更 / 戦略の変更）と、
/// 突き合わせに使った**現在**の値の両方を返すため、「何が変わって無効になったのか」が読める。
/// </para>
/// </summary>
/// <param name="Status">verdict の有効性。<c>Valid</c> 以外は解禁されない。</param>
/// <param name="Verdict">承認記録に載っている最新の verdict（未承認なら <c>null</c>）。</param>
/// <param name="CurrentSourceFingerprint">**現在**の情報源フィンガープリント（借株照会・維持率の登録アダプタ名）。</param>
/// <param name="CurrentStrategyId">**現在**の戦略識別子（直近のバックテスト verdict が名乗る値）。</param>
/// <param name="ShortSellStrategyBacktestPassed">空売りを含む戦略で Stage 0 を再充足したか（解禁条件の別項）。</param>
/// <param name="ExpiresAtUtc">verdict の失効時刻（発行 + 30 日）。未承認なら <c>null</c>。</param>
public sealed record ShortSellReleaseState(
    ShortSellReleaseVerdictStatus Status,
    ShortSellReleaseVerdict? Verdict,
    string CurrentSourceFingerprint,
    string CurrentStrategyId,
    bool ShortSellStrategyBacktestPassed,
    DateTimeOffset? ExpiresAtUtc);

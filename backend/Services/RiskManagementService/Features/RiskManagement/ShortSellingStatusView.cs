using AiStockTrading.Shared.Contracts.Trading;

namespace RiskManagementService.Features.RiskManagement;

// FR-10, UC-06, SC-03, ADR-0016（決定3・決定7・決定9・決定15）, #340, IADR-0154:
// SC-03「維持率・空売りの現況」が参照する読み取り専用ビュー。表示専用（副作用なし）。
//
// **本ビューの存在理由は「値を運ぶこと」ではなく「値が供給されているかを宣言すること」である。**
// 維持率・借株料の累計・自動縮小の発動履歴は、現時点でブローカー照会の供給元が無く（IADR-0133 決定5・
// PoC 項目 3）、値そのものが存在しない。これを 0 や空列で運ぶと、画面は**正常な統制**として描いてしまう
// （#403 の ControlViolationCount 既定 0 が「違反なし」に見えた fail-open と同型）。
// 維持率は計画（05_screens SC-03）が「本画面の最上位に置く。マージンコールは口座を失う唯一の経路である」と
// 定めた指標であり、**未供給を正常のように見せることが最悪の失敗**である。
//
// 供給可否の判定は**サーバ側にしか置けない**。フロントに「維持率は未供給」と書き込むと、供給元が入った日に
// 画面が嘘をつき続ける（誰も気づかない・逆向きの同型事故）。
public sealed record ShortSellingStatusView(
    // ---- 維持率（ADR-0016 決定7・画面最上位） ----
    // **Stage 1 の全期間にわたって表示できない**（ADR-0016 決定7 の 2026-08-07 追記・ADR-0019 PoC 項目 3）。
    // moomoo SIMULATE では照会 API 自体が失敗し、実弾口座のヘッダを要するためである。
    // **これは実装の不具合ではなく、供給が無いという事実の正しい表現である**（05_screens 2026-08-07）。
    MetricAvailability MaintenanceMarginAvailability,
    // 現在の維持率（純資産 ÷ 建玉評価額）。Available のときのみ値を持つ。
    decimal? MaintenanceMarginRatio,
    // **実際に適用される**維持率閾値（自前 40% と規制要求の厳しい方＝保有建玉のうち最も厳しいもの。
    // IADR-0133 決定2）。**株価に依存する**ため、建玉スナップショットが無ければ決められず null になる。
    decimal? AppliedMaintenanceMarginThreshold,
    // 適用される回復目標（＝適用閾値 + 回復目標オフセット）。上と同じ理由で null になり得る。
    decimal? AppliedMaintenanceRecoveryTarget,
    // 設定上の維持率閾値（既定 0.40）。**設定値であり実測ではない。** 常に値を持つ。
    decimal ConfiguredMaintenanceMarginThreshold,
    // 回復目標のオフセット（既定 0.05 ＝ +5 ポイント。計画 §5）。常に値を持つ。
    decimal MaintenanceRecoveryTargetOffset,

    // ---- 空売り比率（ADR-0016 決定9） ----
    MetricAvailability ShortExposureAvailability,
    // 空売り建玉の合計 ÷ 建玉総額（いずれも時価）。Available のときのみ値を持つ。
    decimal? ShortExposureRatio,
    // 空売り比率の上限（既定 0.50）。設定値のため常に値を持つ。
    decimal ShortExposureRatioCap,

    // ---- 保有ポジション（建玉の方向・借株料の累計。ADR-0016 決定15） ----
    IReadOnlyList<ShortSellingPositionView> Positions,
    // 借株料の累計。**供給元がコードに存在しない**ため現状は常に NotSupplied。
    MetricAvailability BorrowFeeAvailability,
    decimal? TotalAccruedBorrowFeeUsd,

    // ---- 維持率割れによる自動縮小（FR-10・UC-06・05_screens 2026-08-02 追加） ----
    // 発動履歴の供給可否。**発火元（維持率）が無い間は NotSupplied** であり、空列＝「発動なし」と
    // 区別する。区別しないと「統制が働いていて発動が無かった」と読めてしまう。
    MetricAvailability ReductionHistoryAvailability,
    IReadOnlyList<MaintenanceMarginReductionRecordView> ReductionHistory,

    // ---- 強制買戻しの発生回数（ADR-0016 決定15・05_screens SC-03 の供給元の表・#424・IADR-0162 決定2） ----
    // **供給元が無いため現状は常に NotSupplied。0 件と表示してはならない**（計画が名指しで禁じた向き）。
    // 推定台帳（#419・IADR-0159）は入ったが、台帳は**推定が起きたときにしか行を書かない**ため、
    // 行数 0 は「観測した結果 0 件だった」と「ブローカ建玉の観測が一度も届いていない」を区別できない。
    MetricAvailability BuyInCountAvailability,
    int? BuyInCount);

// FR-10, SC-03, ADR-0016 決定15, #340: 保有ポジション 1 件の参照表示。
// **方向（ロング / ショート）**と**借株料の累計**を持つ（計画 SC-03 の表の列）。
public sealed record ShortSellingPositionView(
    string Symbol,
    Market Market,
    // 建玉の方向。Buy ＝ ロング、Sell ＝ ショート（符号付き在庫の向き）。
    TradeSide Side,
    int Quantity,
    // 平均取得単価（**ローカル通貨**。IADR-0107 決定1）。
    decimal AverageEntryPrice,
    // 評価額（USD）。現在値が供給されない建玉では NotSupplied。
    MetricAvailability MarketValueAvailability,
    decimal? MarketValueUsd,
    // 借株料の累計（USD）。供給元が無いため現状は常に NotSupplied。
    MetricAvailability BorrowFeeAvailability,
    decimal? AccruedBorrowFeeUsd);

// FR-10, UC-06, #330, IADR-0133 決定7, #340: 自動縮小 1 回分の発動記録（参照表示）。
// 日報 §4 と同じ 4 値（発動日時・決済前後の維持率・適用閾値・回復目標）に、決済した建玉の明細を添える。
public sealed record MaintenanceMarginReductionRecordView(
    DateTimeOffset ExecutedAt,
    decimal RatioBefore,
    decimal Threshold,
    decimal RecoveryTarget,
    decimal? RatioAfter,
    // 決済した建玉（**必要証拠金の降順**。UC-06。含み損の大きい順ではない）。
    IReadOnlyList<MaintenanceMarginReductionLegView> Legs);

public sealed record MaintenanceMarginReductionLegView(
    string Symbol,
    Market Market,
    TradeSide PositionSide,
    int Quantity,
    decimal RequiredMarginUsd);

// FR-10, SC-03, #340, IADR-0154: 指標 1 つの供給可否。
//
// **null だけでは 2 つの別事実を区別できない**——「供給元が無い（＝統制が働いていない）」と
// 「概念が成立しない（＝建玉が 1 件も無い）」である。前者は運用上の異常、後者は正常であり、
// 打つ手がまったく違う。リポジトリは既に同じ区別を MaintenanceMarginEvaluationStatus
// （NoActionRequired / SnapshotUntrusted。IADR-0133 決定8）で行っており、画面にも同じ規律を持ち込む。
//
// **序数は HTTP 経路で整数として往来する**（IADR-0086 決定4）。既存メンバの序数は不変とし、
// 新設は常に末尾へ追加する（IADR-0134 決定2 と同じ規律）。
public enum MetricAvailability
{
    /// <summary>値がある。</summary>
    Available,

    /// <summary>
    /// **供給元が無い、または取得できなかった。** 画面は「取得できていません」と明示し、
    /// 0 や「—」だけで正常値のように見せてはならない。
    /// </summary>
    NotSupplied,

    /// <summary>概念が成立しない（建玉が 1 件も無い等）。異常ではない。</summary>
    NotApplicable,
}

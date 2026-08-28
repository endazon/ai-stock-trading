namespace ReportService.Domain;

// FR-06, FR-15, FR-20, #338, 04_report-templates 月報 §5, 06_daytrading-review §4.1:
// バックテスト / SIMULATE（Stage 1）/ 実弾（Stage 2 以降）の三者比較。
//
// 計画の明文（§5 の記載要件）:
//   - 指標はすべて **USD 建て**で揃える
//   - **合否判定には使わない**（人間が読んで判断する材料）
//   - **列が埋まらない期間がある**（Stage 1 の間は実弾列、Stage 0 の間は SIMULATE 列も空欄）。
//     🔴 **「空欄」と「値が 0」を区別できる表記にする**
//   - 差分が大きい指標のみ考察を書く（全指標に書くと読まれない）
//
// 🔴 だから各セルは `decimal?` である。**0m へ倒さない。**「取引が 1 件も無かった（0 件）」と
// 「その段をまだ走らせていない（空欄）」は別の事実であり、混ぜると三者比較の目的（乖離の把握）が壊れる。

/// <summary>1 指標の三者の値。<c>null</c> ＝該当なし（空欄）であり、<c>0</c> ＝値が 0 である。</summary>
public sealed record ThreeWayMetric(decimal? Backtest, decimal? Simulate, decimal? Live);

/// <summary>月報 §5 の三者比較。</summary>
/// <param name="WinRate">勝率（0.0〜1.0）。</param>
/// <param name="AveragePnlUsd">平均損益（USD）。</param>
/// <param name="MaxDrawdown">最大ドローダウン（0.0〜1.0）。</param>
/// <param name="TradeCount">取引件数。</param>
/// <param name="DivergenceNote">
/// 差分が大きい指標の要因考察（① 証拠金条件の差 / ② 借株料の差 / ③ 執行の差）。
/// <b>数値ではなく人間の所見</b>であるため、供給が無ければ <c>null</c>（節は出るが考察行は出ない）。
/// </param>
public sealed record ThreeWayComparison(
    ThreeWayMetric WinRate,
    ThreeWayMetric AveragePnlUsd,
    ThreeWayMetric MaxDrawdown,
    ThreeWayMetric TradeCount,
    string? DivergenceNote = null);

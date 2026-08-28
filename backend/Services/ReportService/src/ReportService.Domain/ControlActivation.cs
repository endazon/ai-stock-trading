using AiStockTrading.Shared.Contracts.Events;

namespace ReportService.Domain;

// FR-06, FR-10, FR-20, #338, 04_report-templates 月報 §6「当月の統制作動状況」, 04_workflows/03 月報 3,
// IADR-0253: **「作動機会がなかった統制」を「統制違反 0 件」と分けて記載する**ための分類。
//
// 🔴 計画の明文（04_report-templates §6 の記載要件）:
//   「『作動機会があり作動しなかった統制』と『作動機会そのものが存在しなかった統制』を**分けて**記載する。
//    Stage 1 では発火機会が無く検証できない統制があり（維持率 40% の自動縮小・借株料上限・強制買戻しの検知）、
//    **どちらも「0 件」と報告すると検証されたものと検証されなかったものの区別が失われる**。
//    Stage 2 昇格の判断には後者の一覧が要る。」
//
// 🔴 04_workflows/03_reporting-cycle の明文:
//   「3 は『統制違反 0 件』と必ず分けて記載する。両者を混ぜると、統制が働いて違反が出なかったのか、
//    そもそも作動機会が無かっただけなのかを区別できなくなり、**Stage 昇格判定の根拠が失われる**。」

/// <summary>統制ごとの作動状況。<b>4 値であり、3 値へ潰してはならない。</b></summary>
public enum ControlActivationOutcome
{
    /// <summary>作動機会があり、<b>作動した</b>（当期間に発火した）。</summary>
    Activated,

    /// <summary>
    /// 作動機会があり、<b>作動しなかった</b>。
    /// <para>この統制についてだけ「統制違反 0 件」を主張できる——<b>機会があったうえで出なかった</b>ため。</para>
    /// </summary>
    OpportunityWithoutActivation,

    /// <summary>
    /// <b>作動機会そのものが存在しなかった</b>（＝未検証）。
    /// <para>🔴 <see cref="OpportunityWithoutActivation"/> と<b>同じ一覧へ並べてはならない</b>。</para>
    /// </summary>
    NoOpportunity,

    /// <summary>
    /// 判定に要る証拠が<b>照会できていない</b>。
    /// <para>
    /// 🔴 <see cref="NoOpportunity"/> へ倒さない。「記録が無い」を「機会が無かった」と書くのは、
    /// 為替・強制買戻しの節が一貫して避けてきた形と同型である。
    /// </para>
    /// </summary>
    NotSupplied,
}

/// <summary>統制 1 件の作動状況。</summary>
/// <param name="Name">統制の呼称（報告書へそのまま出す）。</param>
/// <param name="Outcome">分類。</param>
/// <param name="Evidence">その分類に至った根拠（人が事後に検証できるようにする）。</param>
public sealed record ControlActivation(string Name, ControlActivationOutcome Outcome, string Evidence);

/// <summary>
/// 当期間の統制作動状況の一覧。<b>分類ごとの取り出し口を型が持つ</b>ことで、
/// 描画側が 2 つの一覧を取り違えて 1 つに混ぜる書き方をしにくくする。
/// </summary>
public sealed record ControlActivationReport(IReadOnlyList<ControlActivation> Controls)
{
    /// <summary>作動機会があり作動した統制。</summary>
    public IReadOnlyList<ControlActivation> Activated =>
        [.. Controls.Where(c => c.Outcome == ControlActivationOutcome.Activated)];

    /// <summary>作動機会があり作動しなかった統制（＝この統制については「違反 0 件」を主張できる）。</summary>
    public IReadOnlyList<ControlActivation> OpportunityWithoutActivation =>
        [.. Controls.Where(c => c.Outcome == ControlActivationOutcome.OpportunityWithoutActivation)];

    /// <summary>作動機会そのものが存在しなかった統制（＝未検証）。</summary>
    public IReadOnlyList<ControlActivation> NoOpportunity =>
        [.. Controls.Where(c => c.Outcome == ControlActivationOutcome.NoOpportunity)];

    /// <summary>証拠が照会できず判定できなかった統制。</summary>
    public IReadOnlyList<ControlActivation> NotSupplied =>
        [.. Controls.Where(c => c.Outcome == ControlActivationOutcome.NotSupplied)];
}

// FR-06, FR-10, FR-20, #338, IADR-0253: 統制作動状況の判定（純関数・決定的・副作用なし）。
//
// 🔴 **新しい供給を要求しない。** 入力は報告書が既に持っている証拠だけである
//（維持率割れ自動縮小・強制買戻し推定・借株料・為替情報源の状態）。
// 供給を増やすと「供給が入るまで節が空」になり、計画が要求した一覧が当分出ない。
//
// 🔴 **「作動機会」は証拠から導き、推測しない。** 例えば借株料の計上が 1 件も無い期間は
// 空売り建玉が無かったのだから、空売り由来の統制は**作動し得なかった**（＝未検証）。
// これを「機会があったが作動しなかった」と書くと、Stage 2 昇格の判断材料が偽装される。
public static class ControlActivationCatalog
{
    /// <summary>維持率割れによる自動縮小（UC-06・ADR-0016 決定7・INDEX 決定45）。</summary>
    public const string MaintenanceMarginReduction = "維持率割れによる自動縮小";

    /// <summary>借株料の年率上限 20%（ADR-0016 決定3）。</summary>
    public const string BorrowFeeRateCap = "借株料の年率上限（20%）";

    /// <summary>強制買戻し（recall / buy-in）の検知（ADR-0016 決定4・決定15）。</summary>
    public const string BuyInDetection = "強制買戻し（recall / buy-in）の検知";

    /// <summary>為替レートの鮮度切れによる新規建て停止（ADR-0022 決定5）。</summary>
    public const string FxStalenessEntryBlock = "為替レートの鮮度切れによる新規建て停止";

    /// <summary>当期間の統制作動状況を判定する。</summary>
    public static ControlActivationReport Evaluate(
        IReadOnlyList<MaintenanceMarginReductionExecuted>? marginReductions,
        IReadOnlyList<BuyInInferred>? buyInInferences,
        BorrowFeeRecord? borrowFees,
        FxSourceStatus? fxSourceStatus)
    {
        var summary = borrowFees is null ? null : BorrowFeeAggregator.Aggregate(borrowFees);

        // 空売り建玉があったか。借株料の記録（計上・未計上のいずれか）が 1 件でもあれば建玉が存在した。
        // 🔴 **未計上（料率が取れなかった日）も「建玉があった」証拠である**——費用が計上できなかっただけで、
        // 建玉そのものは存在した。ここを計上だけで見ると、料率照会が落ちていた月が「建玉なし」になる。
        bool? hadShortPosition = borrowFees is null
            ? null
            : borrowFees.Accruals.Count > 0 || borrowFees.Unavailable.Count > 0;

        return new ControlActivationReport(
        [
            EvaluateMarginReduction(marginReductions, hadShortPosition),
            EvaluateBorrowFeeCap(summary),
            EvaluateBuyInDetection(buyInInferences, hadShortPosition),
            EvaluateFxStalenessBlock(fxSourceStatus),
        ]);
    }

    // 維持率割れの自動縮小: 機会＝維持率が問題になり得る建玉（信用・空売り）が存在した期間。
    private static ControlActivation EvaluateMarginReduction(
        IReadOnlyList<MaintenanceMarginReductionExecuted>? reductions, bool? hadShortPosition)
    {
        if (reductions is null)
            return new(MaintenanceMarginReduction, ControlActivationOutcome.NotSupplied, "発動の記録を照会できていない。");

        if (reductions.Count > 0)
            return new(MaintenanceMarginReduction, ControlActivationOutcome.Activated,
                $"当期間に {reductions.Count} 回発動した。");

        if (hadShortPosition is null)
            return new(MaintenanceMarginReduction, ControlActivationOutcome.NotSupplied,
                "発動は 0 件だが、**作動機会があったか**を判定する建玉の記録を照会できていない。");

        return hadShortPosition.Value
            ? new(MaintenanceMarginReduction, ControlActivationOutcome.OpportunityWithoutActivation,
                "当期間に建玉があり維持率の評価対象だったが、閾値割れによる発動は 0 件だった。")
            : new(MaintenanceMarginReduction, ControlActivationOutcome.NoOpportunity,
                "当期間に維持率の評価対象となる建玉が無く、**作動し得なかった**（未検証）。");
    }

    // 借株料の年率上限: 機会＝借株料の計上があった（＝料率が適用された）期間。
    private static ControlActivation EvaluateBorrowFeeCap(BorrowFeeSummary? summary)
    {
        if (summary is null)
            return new(BorrowFeeRateCap, ControlActivationOutcome.NotSupplied, "借株料の記録を照会できていない。");

        if (summary.MaxRateAnnual is not { } max)
            return new(BorrowFeeRateCap, ControlActivationOutcome.NoOpportunity,
                "当期間に借株料の計上が無く、料率上限は**作動し得なかった**（未検証）。");

        return max >= BorrowFeeAggregator.MaxAnnualRate
            ? new(BorrowFeeRateCap, ControlActivationOutcome.Activated,
                $"適用年率の最大 {Ratio(max)} が上限 {Ratio(BorrowFeeAggregator.MaxAnnualRate)} に達した。")
            : new(BorrowFeeRateCap, ControlActivationOutcome.OpportunityWithoutActivation,
                $"借株料の計上があり料率は評価されたが、最大 {Ratio(max)} で上限 {Ratio(BorrowFeeAggregator.MaxAnnualRate)} に届かなかった。");
    }

    // 強制買戻しの検知: 機会＝空売り建玉が存在した期間。
    private static ControlActivation EvaluateBuyInDetection(
        IReadOnlyList<BuyInInferred>? inferences, bool? hadShortPosition)
    {
        if (inferences is null)
            return new(BuyInDetection, ControlActivationOutcome.NotSupplied, "推定の記録を照会できていない。");

        if (inferences.Count > 0)
            return new(BuyInDetection, ControlActivationOutcome.Activated,
                $"当期間に {inferences.Count} 件を強制買戻しと推定した（**推定**であり確定した事実ではない）。");

        if (hadShortPosition is null)
            return new(BuyInDetection, ControlActivationOutcome.NotSupplied,
                "推定は 0 件だが、**作動機会があったか**を判定する空売り建玉の記録を照会できていない。");

        return hadShortPosition.Value
            ? new(BuyInDetection, ControlActivationOutcome.OpportunityWithoutActivation,
                "当期間に空売り建玉があり突合の対象だったが、強制買戻しの推定は 0 件だった。")
            : new(BuyInDetection, ControlActivationOutcome.NoOpportunity,
                "当期間に空売り建玉が無く、強制買戻しは**起こり得なかった**（未検証）。");
    }

    // 為替の鮮度切れによる新規建て停止: 機会＝為替レートを実際に使った期間。
    private static ControlActivation EvaluateFxStalenessBlock(FxSourceStatus? fx)
    {
        if (fx is null)
            return new(FxStalenessEntryBlock, ControlActivationOutcome.NotSupplied, "為替の情報源の状態を照会できていない。");

        var blocked = fx.StaleWarnings.Count(w => w.EntryBlocked);
        if (blocked > 0)
            return new(FxStalenessEntryBlock, ControlActivationOutcome.Activated,
                $"鮮度切れにより新規建てを {blocked} 件停止した。");

        return fx.UsedSourceNames.Count > 0
            ? new(FxStalenessEntryBlock, ControlActivationOutcome.OpportunityWithoutActivation,
                "当期間に為替レートを使用しており鮮度は評価されたが、停止域には達しなかった。")
            : new(FxStalenessEntryBlock, ControlActivationOutcome.NoOpportunity,
                "当期間に為替レートの使用記録が無く、鮮度統制は**作動し得なかった**（未検証）。");
    }

    private static string Ratio(decimal ratio) =>
        (ratio * 100m).ToString("0.##", System.Globalization.CultureInfo.InvariantCulture) + "%";
}

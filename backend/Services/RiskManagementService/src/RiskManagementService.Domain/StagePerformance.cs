namespace AiStockTrading.RiskManagement.Domain;

// FR-20, ADR-0008, 06_daytrading-review §4: 段階ゲートの合格・撤退基準を評価するための観測実績。
// FR-15 バックテスト verdict・実DD・統制違反数・スリッページ/費用実績は別コンポーネント（後続）で
// 判定・計測され、本ドメインには入力として渡す（IADR-0041: メトリクスは入力）。
//
// **既定はすべて fail-safe（昇格しない側）である。** 供給元が無いフィールドは 0 / false のままであり、
// その場合 Stage 1 → 2 の昇格は起こらない（#333・IADR-0137）。
public record StagePerformance
{
    /// <summary>Stage 0 合格ゲート: DSR 補正後もエッジが正・最大DDが許容内（FR-15 バックテスト verdict）。</summary>
    public bool BacktestPassed { get; init; }

    /// <summary>バックテスト時の最大ドローダウン比率（撤退基準 実DD ≥ 本値 × 1.5 の分母。ADR-0008）。</summary>
    public decimal BacktestMaxDrawdownRatio { get; init; }

    /// <summary>実運用/SIMULATE で観測した最大ドローダウン比率（撤退基準の実測値）。</summary>
    public decimal ObservedMaxDrawdownRatio { get; init; }

    /// <summary>
    /// FR-20, #333, 06_daytrading-review §4.2, INDEX 決定 34: Stage 1→2 合格ゲート（条件 2）。
    /// **実際に取引できた日数**の累計（経過日数ではない）。1 日として数える条件は
    /// <see cref="Stage1DayQualification.Qualifies"/>（その日の実際の通常取引時間の 50% 以上が稼働し、
    /// **かつ発注先が moomoo <c>SIMULATE</c>** であること。#334・IADR-0142）。
    /// <para>**供給元は未実装である**（日次の稼働分数を記録するドライバが無い）。既定 0 ＝ 昇格しない。</para>
    /// </summary>
    public int Stage1QualifiedTradingDays { get; init; }

    /// <summary>
    /// FR-20, #333, #386, 06_daytrading-review §4.1 条件 3 / §4.3, INDEX 決定 42: Stage 1→2 合格ゲート（条件 3）。
    /// Stage 1（moomoo <c>SIMULATE</c>）で成立した取引件数。**内蔵 <c>paper</c> の擬似約定は算入しない**
    /// （#334・IADR-0142）。**100 件に届かない限り期間だけでは昇格しない。**
    /// <para>
    /// **本フィールドは永続化されない**（IADR-0149 決定3）。供給元は約定の観測ログ
    /// （<c>stage1_fill_observations</c>・計上単位は「約定した新規建て注文 1 件」）であり、
    /// <c>StageGateService</c> が判定の直前に重ねる。実績行にも列として持たせると供給元が 2 つになり、
    /// 必ず食い違う。**書き忘れは 0 ＝ 昇格しない（fail-safe）に倒れる**ため、統制違反件数
    /// （#387・IADR-0148）のように必須引数へ追い出す必要は無い。
    /// </para>
    /// </summary>
    public int Stage1TradeCount { get; init; }

    // FR-20, #387, IADR-0148: Stage 1→2 合格ゲート（条件 1）「クラス C 統制違反 0 件」の件数は
    // **本レコードが持たない**。件数は発注審査の観測ログから集計され（ControlViolationAggregation）、
    // 判定へは ControlViolationTally? として StageGate へ直接渡される。
    //
    // 理由: 本レコードの他のフィールドは既定（0 / false）が fail-safe だが、**違反件数の 0 は
    // 「違反が無い＝条件充足」を意味し、唯一 fail-safe でない**。init プロパティとして持つと
    // 「書かなければ 0 ＝合格」になり、供給元の不在が合格として読まれる（#387 の fail-open）。
    // 判定関数の**必須引数**にすることで、渡し忘れがコンパイルを通らなくなる。

    /// <summary>Stage 2→3 合格ゲート: 実効スリッページ・費用が想定内か。</summary>
    public bool SlippageAndCostWithinExpected { get; init; }

    /// <summary>Stage 2→3 合格ゲート: 日次損失上限の運用実績（違反なし）か。</summary>
    public bool DailyLossLimitRespected { get; init; }

    /// <summary>
    /// FR-20, SC-03, #334, IADR-0142 決定3: 内蔵 <c>paper</c> 稼働により**算入されなかった**営業日数。
    /// 合格判定には用いず、進捗表示の併記（「経過 42 / 60 営業日〔<c>paper</c> 稼働により 3 日を除外〕」）に用いる。
    /// <para>**供給元は未実装である**（#386）。既定 0。</para>
    /// </summary>
    public int Stage1ExcludedInternalPaperDays { get; init; }

    /// <summary>FR-20, #333: Stage 1 の進捗（期間 × 件数）を <see cref="Stage1Gate"/> の入力へ束ねる。</summary>
    public Stage1Progress Stage1Progress =>
        new(Stage1QualifiedTradingDays, Stage1TradeCount, Stage1ExcludedInternalPaperDays);
}

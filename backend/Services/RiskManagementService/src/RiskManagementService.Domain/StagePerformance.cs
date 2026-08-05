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
    /// <see cref="Stage1DayQualification.Qualifies"/>（その日の実際の通常取引時間の 50% 以上が稼働）。
    /// <para>**供給元は未実装である**（日次の稼働分数を記録するドライバが無い）。既定 0 ＝ 昇格しない。</para>
    /// </summary>
    public int Stage1QualifiedTradingDays { get; init; }

    /// <summary>
    /// FR-20, #333, 06_daytrading-review §4.1 条件 3 / §4.3, INDEX 決定 42: Stage 1→2 合格ゲート（条件 3）。
    /// Stage 1（SIMULATE）で成立した取引件数。**100 件に届かない限り期間だけでは昇格しない。**
    /// <para>**供給元は未実装である**。既定 0 ＝ 昇格しない。</para>
    /// </summary>
    public int Stage1TradeCount { get; init; }

    /// <summary>
    /// Stage 1→2 合格ゲート（条件 1）: **クラス C 限定**の統制違反件数（0 件が合格条件）。
    /// クラス C ＝ <c>BannedSymbol</c> / <c>ManipulativeOrderPattern</c> を含む発注拒否であり、
    /// 計上単位は 1 回の発注拒否につき 1 件である（06_daytrading-review §4.1・
    /// <c>RejectionReasonClassification</c> が分類の単一情報源）。
    /// **空売りの拒否理由 9 種（クラス A）はここに計上しない。**
    /// </summary>
    public int ControlViolationCount { get; init; }

    /// <summary>Stage 2→3 合格ゲート: 実効スリッページ・費用が想定内か。</summary>
    public bool SlippageAndCostWithinExpected { get; init; }

    /// <summary>Stage 2→3 合格ゲート: 日次損失上限の運用実績（違反なし）か。</summary>
    public bool DailyLossLimitRespected { get; init; }

    /// <summary>FR-20, #333: Stage 1 の進捗（期間 × 件数）を <see cref="Stage1Gate"/> の入力へ束ねる。</summary>
    public Stage1Progress Stage1Progress => new(Stage1QualifiedTradingDays, Stage1TradeCount);
}

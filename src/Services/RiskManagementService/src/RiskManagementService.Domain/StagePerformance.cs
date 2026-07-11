namespace AiStockTrading.RiskManagement.Domain;

// FR-20, ADR-0008, 06_daytrading-review §4: 段階ゲートの合格・撤退基準を評価するための観測実績。
// FR-15 バックテスト verdict・実DD・統制違反数・スリッページ/費用実績は別コンポーネント（後続）で
// 判定・計測され、本ドメインには入力として渡す（IADR-0037: メトリクスは入力）。
public record StagePerformance
{
    /// <summary>Stage 0 合格ゲート: DSR 補正後もエッジが正・最大DDが許容内（FR-15 バックテスト verdict）。</summary>
    public bool BacktestPassed { get; init; }

    /// <summary>バックテスト時の最大ドローダウン比率（撤退基準 実DD ≥ 本値 × 1.5 の分母。ADR-0008）。</summary>
    public decimal BacktestMaxDrawdownRatio { get; init; }

    /// <summary>実運用/ペーパーで観測した最大ドローダウン比率（撤退基準の実測値）。</summary>
    public decimal ObservedMaxDrawdownRatio { get; init; }

    /// <summary>Stage 1→2 合格ゲート/Stage 1 撤退: バックテストとの乖離が説明可能な範囲か。</summary>
    public bool PaperDeviationExplained { get; init; }

    /// <summary>Stage 1→2 合格ゲート: 統制違反件数（0 件が合格条件）。</summary>
    public int ControlViolationCount { get; init; }

    /// <summary>Stage 2→3 合格ゲート: 実効スリッページ・費用が想定内か。</summary>
    public bool SlippageAndCostWithinExpected { get; init; }

    /// <summary>Stage 2→3 合格ゲート: 日次損失上限の運用実績（違反なし）か。</summary>
    public bool DailyLossLimitRespected { get; init; }
}

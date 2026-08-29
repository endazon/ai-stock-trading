namespace BacktestService.Domain;

// FR-15, ADR-0008, 06_daytrading-review §3.2, IADR-0044: Deflated Sharpe Ratio（Bailey & López de Prado）。
// 多数の戦略候補を試すこと（多重検定）と非正規性を補正し、「真 Sharpe が正である確率」を返す。
public static class DeflatedSharpeRatio
{
    // オイラー・マスケローニ定数。
    private const double EulerMascheroni = 0.5772156649015329;

    // 帰無仮説（全試行の真 Sharpe=0）下の期待最大 Sharpe。
    // SR0 = √V · [ (1−γ)·Z⁻¹(1−1/N) + γ·Z⁻¹(1−1/(N·e)) ]。試行 2 未満・分散非正は 0。
    public static double ExpectedMaxSharpe(double varianceOfTrialSharpes, int trials)
    {
        if (trials < 2 || varianceOfTrialSharpes <= 0d)
            return 0d;

        var std = Math.Sqrt(varianceOfTrialSharpes);
        var z1 = NormalDistribution.InverseCdf(1.0 - 1.0 / trials);
        var z2 = NormalDistribution.InverseCdf(1.0 - 1.0 / (trials * Math.E));
        return std * ((1.0 - EulerMascheroni) * z1 + EulerMascheroni * z2);
    }

    // DSR = Φ( (SR − SR0)·√(T−1) / √(1 − γ₃·SR + (γ₄−1)/4·SR²) )。標本長 2 未満・分母非正は 0（保守側）。
    public static double Compute(double observedSharpe, int sampleLength, double skew, double kurtosis, double expectedMaxSharpe)
    {
        if (sampleLength < 2)
            return 0d; // 標本不足は評価不能 → 保守側（合格させない）。

        var variance = 1.0 - skew * observedSharpe + (kurtosis - 1.0) / 4.0 * observedSharpe * observedSharpe;
        if (variance <= 0d)
            return 0d;

        var z = (observedSharpe - expectedMaxSharpe) * Math.Sqrt(sampleLength - 1) / Math.Sqrt(variance);
        return NormalDistribution.Cdf(z);
    }
}

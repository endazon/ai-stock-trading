namespace BacktestService.Domain;

// FR-15, IADR-0044/0039: リターン列の標本モーメント。DSR は 1 期間 Sharpe・歪度・尖度・標本長を要する。
// SampleStandardDeviation は標本標準偏差（n-1）、歪度・尖度は母集団標準化モーメント（尖度は非超過・正規で 3）。
public readonly record struct SampleMoments(
    double Mean,
    double SampleStandardDeviation,
    double Skewness,
    double Kurtosis,
    int Count)
{
    // 1 期間 Sharpe = 平均 / 標本SD。SD 0 は 0（ゼロ除算回避）。
    public double SharpePerPeriod => SampleStandardDeviation == 0d ? 0d : Mean / SampleStandardDeviation;
}

public static class SampleMomentsCalculator
{
    public static SampleMoments Compute(IReadOnlyList<double> values)
    {
        ArgumentNullException.ThrowIfNull(values);

        var n = values.Count;
        if (n == 0)
            return new SampleMoments(0d, 0d, 0d, 0d, 0);

        var mean = values.Average();

        double m2 = 0d, m3 = 0d, m4 = 0d;
        foreach (var v in values)
        {
            var d = v - mean;
            var d2 = d * d;
            m2 += d2;
            m3 += d2 * d;
            m4 += d2 * d2;
        }
        m2 /= n; // 母集団分散
        m3 /= n;
        m4 /= n;

        var sampleStd = n < 2 ? 0d : Math.Sqrt(values.Sum(v => (v - mean) * (v - mean)) / (n - 1));
        var popStd = Math.Sqrt(m2);
        var skew = popStd == 0d ? 0d : m3 / (popStd * popStd * popStd);
        var kurtosis = m2 == 0d ? 0d : m4 / (m2 * m2);

        return new SampleMoments(mean, sampleStd, skew, kurtosis, n);
    }
}

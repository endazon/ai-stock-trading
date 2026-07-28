namespace AiStockTrading.Backtest.Domain.Tests.Calibration;

// FR-15, FR-20, ADR-0008, #208, IADR-0110: Stage 0 のエッジ有意条件（DSR）に対する較正ハーネス。
//
// 真のエッジが 0 の候補を searchSize 個生成して IS Sharpe 最良を選び、台帳へ recordedTrials 件だけ記録した
// ときの DSR を求める。「探索したが記録しなかった」状況（過少申告）を再現できるのが要点で、
// MinTrials がこの過少申告をどこまで許すかが較正の対象である。
//
// 実市場データは使わない。ここで測るのは多重検定補正が働くかどうかという統計的性質であり、
// 真のエッジ 0 の合成標本で決まる（実データでの水準確認は #208 に残置・IADR-0110）。
internal static class Stage0NoiseCalibration
{
    /// <summary>1 回の探索結果。DSR・期待最大 Sharpe（補正項）・選ばれた候補の 1 期間 Sharpe。</summary>
    internal readonly record struct NoiseSearchOutcome(
        double DeflatedSharpe, double ExpectedMaxSharpe, double BestInSampleSharpe);

    /// <summary>期待最大 Sharpe の推定ばらつき。RelativeDispersion は変動係数（標準偏差 / 平均）。</summary>
    internal readonly record struct DispersionSummary(double Mean, double StandardDeviation)
    {
        public double RelativeDispersion => Mean == 0d ? double.PositiveInfinity : StandardDeviation / Mean;
    }

    /// <summary>
    /// 真のエッジ 0 の候補を searchSize 個試し、IS Sharpe 最良を選ぶ。台帳には最良候補を含む
    /// recordedTrials 件を記録する（recordedTrials &lt; searchSize が過少申告）。
    /// </summary>
    internal static NoiseSearchOutcome RunOnce(
        int searchSize, int recordedTrials, int sampleLength, DeterministicNormalSampler sampler)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(searchSize, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(recordedTrials, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(recordedTrials, searchSize);
        ArgumentOutOfRangeException.ThrowIfLessThan(sampleLength, 2);
        ArgumentNullException.ThrowIfNull(sampler);

        var candidates = new (double[] Returns, SampleMoments Moments)[searchSize];
        for (var i = 0; i < searchSize; i++)
        {
            var returns = sampler.NextSeries(sampleLength);
            candidates[i] = (returns, SampleMomentsCalculator.Compute(returns));
        }

        var bestIndex = 0;
        for (var i = 1; i < searchSize; i++)
        {
            if (candidates[i].Moments.SharpePerPeriod > candidates[bestIndex].Moments.SharpePerPeriod)
                bestIndex = i;
        }
        var best = candidates[bestIndex];

        // 台帳は「最良候補＋先頭から順に補充した候補」。過少申告時に最良だけが残る形（最も緩い記録）を再現する。
        var ledger = new TrialLedger();
        ledger.Record(new BacktestTrial($"best-{bestIndex}", best.Moments.SharpePerPeriod, 0d));
        for (var i = 0; i < searchSize && ledger.Count < recordedTrials; i++)
        {
            if (i == bestIndex)
                continue;
            ledger.Record(new BacktestTrial($"trial-{i}", candidates[i].Moments.SharpePerPeriod, 0d));
        }

        var expectedMax = DeflatedSharpeRatio.ExpectedMaxSharpe(ledger.InSampleSharpeVariance(), ledger.Count);
        var dsr = DeflatedSharpeRatio.Compute(
            best.Moments.SharpePerPeriod, best.Moments.Count, best.Moments.Skewness, best.Moments.Kurtosis, expectedMax);

        return new NoiseSearchOutcome(dsr, expectedMax, best.Moments.SharpePerPeriod);
    }

    /// <summary>真のエッジ 0 の探索が DSR ≥ dsrThreshold を通してしまう割合（偽陽性率）。</summary>
    internal static double FalsePositiveRate(
        int searchSize, int recordedTrials, int sampleLength, int repetitions, ulong seed, double dsrThreshold)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(repetitions, 1);

        var sampler = new DeterministicNormalSampler(seed);
        var passed = 0;
        for (var r = 0; r < repetitions; r++)
        {
            if (RunOnce(searchSize, recordedTrials, sampleLength, sampler).DeflatedSharpe >= dsrThreshold)
                passed++;
        }
        return (double)passed / repetitions;
    }

    /// <summary>真のエッジ 0 の性能行列に対する PBO の分布。ShareWithinThreshold は PBO ≤ threshold の割合。</summary>
    internal readonly record struct PboSummary(double Mean, double Median, double ShareWithinThreshold);

    /// <summary>
    /// 真のエッジ 0 の候補群（strategies 個）を blocks 個の区間で評価した性能行列から PBO を求め、
    /// その分布を返す。PBO の閾値（MaxProbabilityOfOverfitting）が雑音のどこに位置するかを測る。
    /// </summary>
    internal static PboSummary NoisePboDistribution(
        int strategies, int blocks, int partitions, int blockLength, int repetitions, ulong seed, double threshold,
        double annualSharpeOfFirstStrategy = 0d)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(strategies, 2);
        ArgumentOutOfRangeException.ThrowIfLessThan(blocks, partitions);
        ArgumentOutOfRangeException.ThrowIfLessThan(blockLength, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(repetitions, 1);

        // 年率 Sharpe を 1 期間あたりのドリフト（標準偏差 1 の系列に対する平均）へ換算する。
        var driftPerPeriod = annualSharpeOfFirstStrategy / Math.Sqrt(BacktestMetricsCalculator.TradingDaysPerYear);

        var sampler = new DeterministicNormalSampler(seed);
        var values = new double[repetitions];
        for (var r = 0; r < repetitions; r++)
        {
            var matrix = new double[blocks][];
            for (var b = 0; b < blocks; b++)
            {
                matrix[b] = new double[strategies];
                for (var s = 0; s < strategies; s++)
                {
                    // 区間平均リターン（順位付けの一貫性のみ要る）。戦略 0 にだけ既知のエッジを注入できる。
                    var drift = s == 0 ? driftPerPeriod : 0d;
                    matrix[b][s] = sampler.NextSeries(blockLength).Average() + drift;
                }
            }
            values[r] = ProbabilityOfBacktestOverfitting.Compute(matrix, partitions);
        }

        Array.Sort(values);
        var median = values.Length % 2 == 1
            ? values[values.Length / 2]
            : (values[(values.Length / 2) - 1] + values[values.Length / 2]) / 2d;

        return new PboSummary(values.Average(), median, values.Count(v => v <= threshold) / (double)values.Length);
    }

    /// <summary>正直に記録した場合の期待最大 Sharpe（補正項）の推定ばらつき。台帳が小さいほど不安定になる。</summary>
    internal static DispersionSummary ExpectedMaxSharpeDispersion(
        int searchSize, int sampleLength, int repetitions, ulong seed)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(repetitions, 2);

        var sampler = new DeterministicNormalSampler(seed);
        var values = new double[repetitions];
        for (var r = 0; r < repetitions; r++)
            values[r] = RunOnce(searchSize, searchSize, sampleLength, sampler).ExpectedMaxSharpe;

        var mean = values.Average();
        var sd = Math.Sqrt(values.Sum(v => (v - mean) * (v - mean)) / (values.Length - 1));
        return new DispersionSummary(mean, sd);
    }
}

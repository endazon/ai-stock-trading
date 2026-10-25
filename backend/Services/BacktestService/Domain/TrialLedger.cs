namespace BacktestService.Domain;

// FR-15, ADR-0008, IADR-0044: 1 試行（戦略構成候補）の記録。IS Sharpe・OOS 実績。
//
// 🔴 FR-15, ADR-0039 決定1, #1381, IADR-0554: IS Sharpe は **1 期間（日次）の Sharpe** で持つ（年率ではない）。
// DSR は観測値（`SampleMoments.SharpePerPeriod`）から SR0（台帳の分散から求める期待最大 Sharpe）を差し引く。
// 両者の単位が違うと SR0 は √252 倍に膨らみ、試行 2 本以上で DSR が事実上必ず不合格になる。
// 単位を名前に持たせ、記録は <see cref="FromMetrics"/> に寄せる（判定器の観測値と同じ計算を通す）。
public readonly record struct BacktestTrial(string Name, double InSampleSharpePerPeriod, double OutOfSampleReturn)
{
    // FR-15, #1381, IADR-0554: バックテスト結果から試行を作る。IS Sharpe は日次リターン列の 1 期間 Sharpe
    // （`Stage0GateService` が DSR の観測値に使うのと同じ `SampleMomentsCalculator`）であり、
    // `BacktestMetrics.SharpeRatio`（年率・√252 倍）を記録しない。
    public static BacktestTrial FromMetrics(string name, BacktestMetrics metrics, double outOfSampleReturn)
    {
        ArgumentNullException.ThrowIfNull(metrics);
        var perPeriod = SampleMomentsCalculator.Compute(metrics.DailyReturns).SharpePerPeriod;
        return new BacktestTrial(name, perPeriod, outOfSampleReturn);
    }
}

// FR-15, 06_daytrading-review §3.2: 試行台帳。試行数 N・IS Sharpe の分散・最良候補を提供し、DSR/PBO の入力にする。
// 「何回試したか」を明示的に記録することが過剰適合補正（多重検定）の前提。
public sealed class TrialLedger
{
    private readonly List<BacktestTrial> _trials = [];

    public int Count => _trials.Count;

    public IReadOnlyList<BacktestTrial> Trials => _trials;

    public void Record(BacktestTrial trial) => _trials.Add(trial);

    // IS Sharpe が最大の試行。空なら null。
    public BacktestTrial? BestByInSampleSharpe()
    {
        if (_trials.Count == 0)
            return null;
        var best = _trials[0];
        foreach (var trial in _trials)
        {
            if (trial.InSampleSharpePerPeriod > best.InSampleSharpePerPeriod)
                best = trial;
        }
        return best;
    }

    // IS Sharpe（1 期間）の標本分散（n-1）。試行 2 未満は 0（DSR の期待最大 Sharpe の入力。単位は日次の Sharpe の 2 乗）。
    public double InSampleSharpeVariance()
    {
        if (_trials.Count < 2)
            return 0d;
        var mean = _trials.Average(t => t.InSampleSharpePerPeriod);
        return _trials.Sum(t => (t.InSampleSharpePerPeriod - mean) * (t.InSampleSharpePerPeriod - mean)) / (_trials.Count - 1);
    }
}

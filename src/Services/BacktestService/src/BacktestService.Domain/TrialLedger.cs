namespace AiStockTrading.Backtest.Domain;

// FR-15, ADR-0008, IADR-0044: 1 試行（戦略構成候補）の記録。IS Sharpe・OOS 実績。
public readonly record struct BacktestTrial(string Name, double InSampleSharpe, double OutOfSampleReturn);

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
            if (trial.InSampleSharpe > best.InSampleSharpe)
                best = trial;
        }
        return best;
    }

    // IS Sharpe の標本分散（n-1）。試行 2 未満は 0（DSR の期待最大 Sharpe の入力）。
    public double InSampleSharpeVariance()
    {
        if (_trials.Count < 2)
            return 0d;
        var mean = _trials.Average(t => t.InSampleSharpe);
        return _trials.Sum(t => (t.InSampleSharpe - mean) * (t.InSampleSharpe - mean)) / (_trials.Count - 1);
    }
}

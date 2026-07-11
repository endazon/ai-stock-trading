using AiStockTrading.Backtest.Domain;

namespace AiStockTrading.Backtest.Application;

// FR-15, IADR-0037: バックテスト実行要求。ユニバース（PIT）・期間・戦略・設定。
public sealed record BacktestRequest(
    SecurityUniverse Universe,
    DateOnly From,
    DateOnly To,
    IBacktestStrategy Strategy,
    BacktestConfig Config);

// FR-15, IADR-0037: 過去データ供給の抽象からバーを取得し、PIT ユニバースで生存者バイアスを排して
// 決定的シミュレーションを実行するオーケストレータ。
public sealed class BacktestRunner
{
    private readonly IBarDataSource _dataSource;

    public BacktestRunner(IBarDataSource dataSource)
    {
        ArgumentNullException.ThrowIfNull(dataSource);
        _dataSource = dataSource;
    }

    public BacktestRun Run(BacktestRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        // データ源から期間内のバーを取得し、PIT ユニバースで当時未上場・上場廃止後のバーを除外する
        // （生存者バイアス排除・ルックアヘッド排除の前段。06_daytrading-review §3.2）。
        var candidate = _dataSource.GetBars(request.From, request.To);
        var filtered = candidate
            .Where(b => request.Universe.MembersAsOf(b.Date).Contains((b.Symbol, b.Market)))
            .ToList();

        return BacktestSimulator.Run(filtered, request.Strategy, request.Config);
    }
}

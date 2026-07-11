using AiStockTrading.Backtest.Domain;
using AiStockTrading.Shared.Contracts.Trading;

namespace AiStockTrading.Backtest.Application;

// FR-15, IADR-0043: バックテスト実行要求。ユニバース（PIT）・期間・戦略・設定。
public sealed record BacktestRequest(
    SecurityUniverse Universe,
    DateOnly From,
    DateOnly To,
    IBacktestStrategy Strategy,
    BacktestConfig Config);

// FR-15, IADR-0043: 過去データ供給の抽象からバーを取得し、PIT ユニバースで生存者バイアスを排して
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
        // メンバーシップは日付単位で 1 回だけ算出し（HashSet 化）、バー単位の線形再計算を避ける（#99 レビュー指摘）。
        var candidate = _dataSource.GetBars(request.From, request.To);
        var membersByDate = new Dictionary<DateOnly, HashSet<(string Symbol, Market Market)>>();
        var filtered = new List<PriceBar>();
        foreach (var bar in candidate)
        {
            if (!membersByDate.TryGetValue(bar.Date, out var members))
            {
                members = [.. request.Universe.MembersAsOf(bar.Date)];
                membersByDate[bar.Date] = members;
            }
            if (members.Contains((bar.Symbol, bar.Market)))
                filtered.Add(bar);
        }

        return BacktestSimulator.Run(filtered, request.Strategy, request.Config);
    }
}

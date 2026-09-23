using MarketMonitorService.Common.Abstractions;
using MarketMonitorService.Features.MarketMonitor;
using AiStockTrading.Shared.Contracts.Ports;
using AiStockTrading.Shared.Contracts.Trading;

namespace MarketMonitorService.Tests;

// NFR/IADR-0263 決定4 の member 粒度拡張と同型の移送実務: 旧 Application.Tests/TestDoubles.cs と
// 旧 Infrastructure.Tests/TestDoubles.cs は FakeClock/FakeMarketDataSource を重複定義していた
// （旧構成では別アセンブリだったため CS0101 を起こさなかった）。Tests 統合（IADR-0259 決定4）で
// 同一アセンブリ・同一名前空間になるため、本ファイルへ 1 定義に統合した（内容は等価。移送のみで
// 挙動は変えていない）。旧 Infrastructure.Tests 固有の FakeSchedule はそのまま本ファイルへ合流させた。

// テスト用の固定クロック。
internal sealed class FakeClock(DateTimeOffset utcNow) : IClock
{
    public DateTimeOffset UtcNow { get; set; } = utcNow;
}

// テスト用の市場カレンダー（開場/閉場を固定できる）。旧 Infrastructure.Tests/TestDoubles.cs 由来。
internal sealed class FakeSchedule(bool open) : IMarketSchedule
{
    /// <summary>全市場の開閉（従来の 1 引数ポートと同じ操作感）。</summary>
    public bool Open { get; set; } = open;

    /// <summary>#909, IADR-0380 決定2: 個別に閉場させる市場（Open=true でもここに入っていれば閉場）。</summary>
    public HashSet<Market> ClosedMarkets { get; } = [];

    /// <summary>#909, IADR-0380 決定3: NextOpen が返す値（既定は「見通せない」）。</summary>
    public DateTimeOffset? NextOpenAt { get; set; }

    public bool IsOpen(Market market, DateTimeOffset instant) => Open && !ClosedMarkets.Contains(market);

    public DateTimeOffset? NextOpen(Market market, DateTimeOffset instant) => NextOpenAt;
}

// テスト用の価格ソース。銘柄→価格の辞書で応答し、未登録は null（取得失敗）を返す。
internal sealed class FakeMarketDataSource : IMarketDataSource
{
    private readonly Dictionary<(string, Market), decimal> _prices = [];

    /// <summary>#909, IADR-0380 決定2: 実際に照会した銘柄（閉場中は 1 件も照会しないことを固定するため）。</summary>
    public List<(string Symbol, Market Market)> Requested { get; } = [];

    public FakeMarketDataSource Set(string symbol, Market market, decimal price)
    {
        _prices[(symbol, market)] = price;
        return this;
    }

    public Task<Quote?> GetLatestQuoteAsync(string symbol, Market market, CancellationToken cancellationToken = default)
    {
        Requested.Add((symbol, market));
        return Task.FromResult(_prices.TryGetValue((symbol, market), out var price)
            ? new Quote(symbol, market, price, DateTimeOffset.UtcNow)
            : null);
    }
}

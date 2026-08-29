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
    public bool Open { get; set; } = open;

    public bool IsOpen(DateTimeOffset instant) => Open;
}

// テスト用の価格ソース。銘柄→価格の辞書で応答し、未登録は null（取得失敗）を返す。
internal sealed class FakeMarketDataSource : IMarketDataSource
{
    private readonly Dictionary<(string, Market), decimal> _prices = [];

    public FakeMarketDataSource Set(string symbol, Market market, decimal price)
    {
        _prices[(symbol, market)] = price;
        return this;
    }

    public Task<Quote?> GetLatestQuoteAsync(string symbol, Market market, CancellationToken cancellationToken = default)
    {
        return Task.FromResult(_prices.TryGetValue((symbol, market), out var price)
            ? new Quote(symbol, market, price, DateTimeOffset.UtcNow)
            : null);
    }
}

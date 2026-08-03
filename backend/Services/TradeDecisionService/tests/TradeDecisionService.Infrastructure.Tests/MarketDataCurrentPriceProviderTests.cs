using AiStockTrading.TradeDecision.Application.Ports;
using AiStockTrading.TradeDecision.Application.State;
using AiStockTrading.TradeDecision.Infrastructure.Composable.Adapters;
using AiStockTrading.Shared.Contracts.Ports;
using AiStockTrading.Shared.Contracts.Trading;
using AwesomeAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AiStockTrading.TradeDecision.Infrastructure.Tests;

// FR-02, FR-04, FR-10, IADR-0068/0099: 現在値供給アダプタの検証。共有 IMarketDataSource を包み、鮮度を検査して
// 権威ある現在値を判断サービスへ渡す。未有効化（no-op）は実取得せず null。取得不可・鮮度切れは現在値なしへ倒す。
public class MarketDataCurrentPriceProviderTests
{
    private static readonly DateTimeOffset Now = new(2026, 7, 24, 3, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan MaxStaleness = TimeSpan.FromSeconds(300);

    private sealed class FakeClock : IClock { public DateTimeOffset UtcNow => Now; }

    private sealed class FakeMarketData(Quote? quote) : IMarketDataSource
    {
        public int Calls { get; private set; }

        public Task<Quote?> GetLatestQuoteAsync(string symbol, Market market, CancellationToken ct = default)
        {
            Calls++;
            return Task.FromResult(quote);
        }
    }

    private sealed class ThrowingMarketData : IMarketDataSource
    {
        public Task<Quote?> GetLatestQuoteAsync(string symbol, Market market, CancellationToken ct = default) =>
            throw new InvalidOperationException("現在値ソースが呼ばれてはならない");
    }

    private static MarketDataCurrentPriceProvider Create(IMarketDataSource source, bool enabled) =>
        new(source, enabled, new FakeClock(), MaxStaleness,
            NullLogger<MarketDataCurrentPriceProvider>.Instance);

    private static DecisionTrigger Trigger() => DecisionTrigger.Scheduled("AAPL", Market.UnitedStates);

    [Fact]
    public void 実結線ならIsEnabledはtrue_未結線ならfalse()
    {
        Create(new FakeMarketData(null), enabled: true).IsEnabled.Should().BeTrue();
        Create(new FakeMarketData(null), enabled: false).IsEnabled.Should().BeFalse();
    }

    [Fact]
    public async Task 有効かつ鮮度内の現在値はそのまま返す()
    {
        var quote = new Quote("AAPL", Market.UnitedStates, 1_234.5m, Now.AddSeconds(-10)); // 鮮度内
        var provider = Create(new FakeMarketData(quote), enabled: true);

        (await provider.GetCurrentPriceAsync(Trigger())).Should().Be(1_234.5m);
    }

    [Fact]
    public async Task 有効でも鮮度切れの現在値はnullへ倒す()
    {
        var quote = new Quote("AAPL", Market.UnitedStates, 1_234.5m, Now.AddSeconds(-600)); // 期限 300s 超過
        var provider = Create(new FakeMarketData(quote), enabled: true);

        (await provider.GetCurrentPriceAsync(Trigger())).Should().BeNull();
    }

    [Fact]
    public async Task 有効でも取得不可ならnull()
    {
        var provider = Create(new FakeMarketData(null), enabled: true);

        (await provider.GetCurrentPriceAsync(Trigger())).Should().BeNull();
    }

    [Fact]
    public async Task 未有効化なら実ソースを呼ばずnull()
    {
        // 未有効化（no-op）は実取得を試みない。ソースが呼ばれれば例外＝呼ばれていないことを担保する。
        var provider = Create(new ThrowingMarketData(), enabled: false);

        (await provider.GetCurrentPriceAsync(Trigger())).Should().BeNull();
    }
}

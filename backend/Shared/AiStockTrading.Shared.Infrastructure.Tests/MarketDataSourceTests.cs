using AiStockTrading.Shared.Contracts.Ports;
using AiStockTrading.Shared.Contracts.Trading;
using AiStockTrading.Shared.Infrastructure.Composable.Adapters.MarketData;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AiStockTrading.Shared.Infrastructure.Tests;

// #81, FR-10, IADR-0065: 現在値ソースの既定 no-op と、未取得時の前回値フォールバック（TTL 付き）を検証する。
public class MarketDataSourceTests
{
    private static readonly DateTimeOffset Now = new(2026, 7, 17, 9, 0, 0, TimeSpan.FromHours(9));

    // IADR-0064/0065: FakeTimeProvider（Microsoft.Extensions.Time.Testing）は中央パッケージ管理に未登録のため、
    // 本テスト内の最小の偽装で足す（TimeProvider は Shared.Infrastructure の既存慣行＝PaperBrokerAdapter と同じ）。
    private sealed class TestTimeProvider(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset _now = now;

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan delta) => _now = _now.Add(delta);
    }

    // 呼び出しごとに返す値を差し替えられるスタブ（null＝取得不可）。
    private sealed class StubSource : IMarketDataSource
    {
        public Quote? Next { get; set; }
        public int Calls { get; private set; }

        public Task<Quote?> GetLatestQuoteAsync(string symbol, Market market, CancellationToken cancellationToken = default)
        {
            Calls++;
            return Task.FromResult(Next);
        }
    }

    private static Quote Quote(decimal price, string symbol = "AAPL") =>
        new(symbol, Market.UnitedStates, price, Now);

    [Fact]
    public async Task 既定の_no_op_ソースは常に取得不可を返す()
    {
        var source = new NoOpMarketDataSource(NullLogger<NoOpMarketDataSource>.Instance);

        (await source.GetLatestQuoteAsync("AAPL", Market.UnitedStates)).Should().BeNull();
        (await source.GetLatestQuoteAsync("7203", Market.Japan)).Should().BeNull();
    }

    [Fact]
    public async Task 取得成功時は内側の現在値をそのまま返す()
    {
        var inner = new StubSource { Next = Quote(1_100m) };
        var sut = new LastKnownQuoteSource(inner, new QuoteCache(), new TestTimeProvider(Now), TimeSpan.FromMinutes(5));

        var quote = await sut.GetLatestQuoteAsync("AAPL", Market.UnitedStates);

        quote!.Price.Should().Be(1_100m);
    }

    [Fact]
    public async Task 取得失敗時は保持期限内の前回値を返す()
    {
        var time = new TestTimeProvider(Now);
        var inner = new StubSource { Next = Quote(1_100m) };
        var sut = new LastKnownQuoteSource(inner, new QuoteCache(), time, TimeSpan.FromMinutes(5));

        await sut.GetLatestQuoteAsync("AAPL", Market.UnitedStates); // 前回値を取得・保持

        inner.Next = null;             // 市況断
        time.Advance(TimeSpan.FromMinutes(4)); // TTL 以内

        var quote = await sut.GetLatestQuoteAsync("AAPL", Market.UnitedStates);

        quote!.Price.Should().Be(1_100m);
    }

    [Fact]
    public async Task 取得失敗時に前回値が保持期限を超えていれば取得不可を返す()
    {
        var time = new TestTimeProvider(Now);
        var inner = new StubSource { Next = Quote(1_100m) };
        var sut = new LastKnownQuoteSource(inner, new QuoteCache(), time, TimeSpan.FromMinutes(5));

        await sut.GetLatestQuoteAsync("AAPL", Market.UnitedStates);

        inner.Next = null;
        time.Advance(TimeSpan.FromMinutes(6)); // TTL 超過 → 古い価格を信じない（含みは 0 へ倒す）

        (await sut.GetLatestQuoteAsync("AAPL", Market.UnitedStates)).Should().BeNull();
    }

    [Fact]
    public async Task 取得失敗時に前回値が無ければ取得不可を返す()
    {
        var inner = new StubSource { Next = null };
        var sut = new LastKnownQuoteSource(inner, new QuoteCache(), new TestTimeProvider(Now), TimeSpan.FromMinutes(5));

        (await sut.GetLatestQuoteAsync("AAPL", Market.UnitedStates)).Should().BeNull();
    }

    [Fact]
    public async Task 前回値は銘柄と市場の組ごとに保持する()
    {
        var time = new TestTimeProvider(Now);
        var inner = new StubSource { Next = Quote(1_100m, "AAPL") };
        var sut = new LastKnownQuoteSource(inner, new QuoteCache(), time, TimeSpan.FromMinutes(5));

        await sut.GetLatestQuoteAsync("AAPL", Market.UnitedStates);

        inner.Next = null;

        // 別銘柄の前回値は無いため取得不可。AAPL の前回値が漏れない。
        (await sut.GetLatestQuoteAsync("MSFT", Market.UnitedStates)).Should().BeNull();
        // 同じ銘柄コードでも市場が異なれば別物として扱う。
        (await sut.GetLatestQuoteAsync("AAPL", Market.Japan)).Should().BeNull();
        (await sut.GetLatestQuoteAsync("AAPL", Market.UnitedStates))!.Price.Should().Be(1_100m);
    }

    [Fact]
    public async Task 前回値は取得成功のたびに更新される()
    {
        var time = new TestTimeProvider(Now);
        var inner = new StubSource { Next = Quote(1_100m) };
        var sut = new LastKnownQuoteSource(inner, new QuoteCache(), time, TimeSpan.FromMinutes(5));

        await sut.GetLatestQuoteAsync("AAPL", Market.UnitedStates);
        time.Advance(TimeSpan.FromMinutes(4));
        inner.Next = Quote(1_200m);
        await sut.GetLatestQuoteAsync("AAPL", Market.UnitedStates); // 保持時刻もここへ更新される

        time.Advance(TimeSpan.FromMinutes(4)); // 直前の取得から 4 分＝TTL 以内
        inner.Next = null;

        (await sut.GetLatestQuoteAsync("AAPL", Market.UnitedStates))!.Price.Should().Be(1_200m);
    }
}

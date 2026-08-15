using AiStockTrading.Shared.Contracts.Ports;
using AiStockTrading.Shared.Contracts.Trading;
using AiStockTrading.TradeDecision.Infrastructure.Composable.Adapters;
using AwesomeAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AiStockTrading.TradeDecision.Infrastructure.Tests;

// FR-10, FR-17, #381, ADR-0022 決定2, IADR-0194 決定3: 為替レート源の**順位つき**フォールバック。
// 「第一が駄目なら次」であり、情報収集側の CompositeInformationSource（全ソースを合算）とは別物である。
public class FallbackFxRateSourceTests
{
    private static readonly DateTimeOffset Recent = new(2026, 8, 12, 0, 0, 0, TimeSpan.FromHours(9));

    // 受け入れ基準: 日銀 → FRED の順で試す。
    [Fact]
    public async Task 第一の源が解決できればそれを採り次の源を呼ばない()
    {
        var first = new StubSource(Rate(1m / 159.38m, Recent));
        var second = new StubSource(Rate(1m / 152.35m, Recent));
        var source = Create(("boj", first), ("fred", second));

        var rate = await source.GetRateToBaseAsync(Currency.Jpy);

        rate!.Rate.Should().Be(1m / 159.38m);
        first.Calls.Should().Be(1);
        // 🔴 第一で足りたなら次は**呼ばない**。呼ぶと不要な外部通信とレート予算の消費が生じる。
        second.Calls.Should().Be(0);
    }

    [Fact]
    public async Task 第一がnullなら次の源へ縮退する()
    {
        var first = new StubSource(null);
        var second = new StubSource(Rate(1m / 152.35m, Recent));
        var source = Create(("boj", first), ("fred", second));

        var rate = await source.GetRateToBaseAsync(Currency.Jpy);

        rate!.Rate.Should().Be(1m / 152.35m);
        first.Calls.Should().Be(1);
        second.Calls.Should().Be(1);
    }

    [Fact]
    public async Task 全ての源がnullならレート無し()
    {
        var source = Create(("boj", new StubSource(null)), ("fred", new StubSource(null)));

        (await source.GetRateToBaseAsync(Currency.Jpy)).Should().BeNull();
    }

    // 🔴 **否定形・IADR-0194 決定3**: 鮮度による切替はしない。
    //
    // 順位は「取れたか」で決まり、「どちらが新しいか」では決まらない。ADR-0022 決定1 が日銀を第一と
    // 定めたのは**公表頻度の構造**によるものであり、個別の観測日で入れ替わってよいものではない。
    // 鮮度判定（5 日超＝警告／30 日超＝停止）は CachingFxRateSource が**本合成の外側**で行う。
    [Fact]
    public async Task 第一の値が次の源より古くても入れ替えない()
    {
        var stale = Rate(1m / 159.38m, new DateTimeOffset(2026, 7, 1, 0, 0, 0, TimeSpan.FromHours(9)));
        var fresher = Rate(1m / 152.35m, Recent);
        var first = new StubSource(stale);
        var second = new StubSource(fresher);
        var source = Create(("boj", first), ("fred", second));

        var rate = await source.GetRateToBaseAsync(Currency.Jpy);

        rate!.Rate.Should().Be(1m / 159.38m, "順位は取得可否で決まり、観測日の新しさでは決まらない");
        second.Calls.Should().Be(0);
    }

    // 取り消しを「源の失敗」と読み替えて次を試すと、取り消したのに外部通信が続く。
    [Fact]
    public async Task キャンセルは次の源を試さずそのまま伝播する()
    {
        var first = new ThrowingSource(new OperationCanceledException());
        var second = new StubSource(Rate(1m / 152.35m, Recent));
        var source = Create(("boj", first), ("fred", second));

        var act = () => source.GetRateToBaseAsync(Currency.Jpy);

        await act.Should().ThrowAsync<OperationCanceledException>();
        second.Calls.Should().Be(0);
    }

    [Fact]
    public async Task 源が1件でも動く()
    {
        var only = new StubSource(Rate(1m / 159.38m, Recent));

        var rate = await Create(("boj", only)).GetRateToBaseAsync(Currency.Jpy);

        rate!.Rate.Should().Be(1m / 159.38m);
    }

    private static FxRate Rate(decimal rate, DateTimeOffset asOf) =>
        new(Currency.Jpy, MarketCurrency.Base, rate, asOf);

    private static FallbackFxRateSource Create(params (string Name, IFxRateSource Source)[] sources) =>
        new(sources, NullLogger<FallbackFxRateSource>.Instance);

    private sealed class StubSource(FxRate? rate) : IFxRateSource
    {
        public int Calls { get; private set; }

        public Task<FxRate?> GetRateToBaseAsync(Currency quote, CancellationToken cancellationToken = default)
        {
            Calls++;
            return Task.FromResult(rate);
        }
    }

    private sealed class ThrowingSource(Exception exception) : IFxRateSource
    {
        public Task<FxRate?> GetRateToBaseAsync(Currency quote, CancellationToken cancellationToken = default) =>
            throw exception;
    }
}

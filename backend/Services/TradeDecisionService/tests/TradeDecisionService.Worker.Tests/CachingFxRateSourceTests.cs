using System.Globalization;
using AiStockTrading.Shared.Contracts.Ports;
using AiStockTrading.Shared.Contracts.Trading;
using AiStockTrading.TradeDecision.Worker.Composable.Adapters;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AiStockTrading.TradeDecision.Worker.Tests;

// FR-10, #257, IADR-0107 決定5: 取得回数の抑制（TTL）と、古すぎる観測を使わない鮮度上限。
// いずれも「そのレートを使ってよいか」を決める判断であり、同じ装飾に置く。
public class CachingFxRateSourceTests
{
    private static readonly DateTimeOffset Now = new(2026, 7, 27, 3, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task TTL内は取得済みレートを返し外部へ再要求しない()
    {
        var inner = new CountingSource(Rate(asOf: Now.AddDays(-1)));
        var (source, _) = Create(inner);

        var first = await source.GetRateToBaseAsync(Currency.Usd);
        var second = await source.GetRateToBaseAsync(Currency.Usd);

        first!.Rate.Should().Be(second!.Rate);
        inner.Calls.Should().Be(1);
    }

    [Fact]
    public async Task TTL経過後は再取得する()
    {
        var inner = new CountingSource(Rate(asOf: Now.AddDays(-1)));
        var (source, time) = Create(inner, ttl: TimeSpan.FromHours(6));

        await source.GetRateToBaseAsync(Currency.Usd);
        time.Advance(TimeSpan.FromHours(6.5));
        await source.GetRateToBaseAsync(Currency.Usd);

        inner.Calls.Should().Be(2);
    }

    [Fact]
    public async Task 鮮度上限を超えた観測は採らない()
    {
        // DEXJPUS は営業日次のため週末・祝日で数日空くが、上限を超えたレートで発注はしない（レート無し＝見送り）。
        var inner = new CountingSource(Rate(asOf: Now.AddDays(-8)));
        var (source, _) = Create(inner, maxAge: TimeSpan.FromDays(7));

        (await source.GetRateToBaseAsync(Currency.Usd)).Should().BeNull();
    }

    [Fact]
    public async Task 鮮度上限内の観測は採る()
    {
        var inner = new CountingSource(Rate(asOf: Now.AddDays(-3)));
        var (source, _) = Create(inner, maxAge: TimeSpan.FromDays(7));

        (await source.GetRateToBaseAsync(Currency.Usd)).Should().NotBeNull();
    }

    // FR-10, #271, IADR-0112 決定1: 既定の鮮度上限は FRED DEXJPUS の公表周期を賄う。系列は営業日次だが公表は
    // H.10 週次リリース（月曜 16:15 ET ≒ 20:15 UTC・前週金曜まで一括収載／月曜が祝日なら火曜）であり、最新観測の齢は
    // 「公表間隔 7 日 ＋ 公表ラグ（金→月）3 日 ＋ 祝日ずれ 最大 2 日 ＋ 公表時刻」として積み上がる。
    // 既定 7 日では予定どおりの公表でも毎週必ず超過し、米国株が週明け・連休明けに全件見送りになっていた。
    [Theory]
    [InlineData("2026-07-17", "2026-07-27T20:00:00Z", true, "通常＝次の月曜公表の直前（10.84 日・#271 の実測）")]
    [InlineData("2026-07-17", "2026-07-28T20:00:00Z", true, "月曜が祝日で火曜公表（11.84 日）")]
    [InlineData("2026-07-16", "2026-07-28T20:00:00Z", true, "対象週の金曜が休場で直近観測が木曜＋翌月曜が祝日（12.84 日）")]
    [InlineData("2026-07-17", "2026-08-03T20:00:00Z", false, "週次リリースが 1 回丸ごと欠落（17.84 日）＝系列側の異常")]
    public async Task 既定の鮮度上限は公表周期の遅延を吸収し系列の異常は見送る(
        string observedOn, string evaluatedAt, bool accepted, string scenario)
    {
        // 既定値そのものを回帰対象にするため、上限は構成の実効解決（ResolveMaxRateAge）から採る。
        var (source, _) = Create(
            new CountingSource(Rate(asOf: Instant($"{observedOn}T00:00:00Z"))),
            maxAge: FxRateSourceFactory.ResolveMaxRateAge(new FxOptions()),
            now: Instant(evaluatedAt));

        var rate = await source.GetRateToBaseAsync(Currency.Usd);

        (rate is not null).Should().Be(accepted, scenario);
    }

    [Fact]
    public async Task 取得できなかった結果はキャッシュせず次回に再取得する()
    {
        // 一時障害を TTL のあいだ引きずると、回復後もレート無し＝見送りが続く。
        var inner = new CountingSource(null);
        var (source, _) = Create(inner);

        await source.GetRateToBaseAsync(Currency.Usd);
        await source.GetRateToBaseAsync(Currency.Usd);

        inner.Calls.Should().Be(2);
    }

    [Fact]
    public async Task 鮮度切れで棄却したレートもキャッシュしない()
    {
        var inner = new CountingSource(Rate(asOf: Now.AddDays(-30)));
        var (source, _) = Create(inner, maxAge: TimeSpan.FromDays(7));

        await source.GetRateToBaseAsync(Currency.Usd);
        await source.GetRateToBaseAsync(Currency.Usd);

        inner.Calls.Should().Be(2);
    }

    private static FxRate Rate(DateTimeOffset asOf) => new(Currency.Usd, Currency.Jpy, 152.35m, asOf);

    private static DateTimeOffset Instant(string iso8601) =>
        DateTimeOffset.Parse(iso8601, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal);

    private static (CachingFxRateSource Source, TestTimeProvider Time) Create(
        IFxRateSource inner, TimeSpan? ttl = null, TimeSpan? maxAge = null, DateTimeOffset? now = null)
    {
        var time = new TestTimeProvider(now ?? Now);
        var source = new CachingFxRateSource(
            inner,
            ttl ?? TimeSpan.FromHours(6),
            maxAge ?? TimeSpan.FromDays(7),
            time,
            NullLogger<CachingFxRateSource>.Instance);
        return (source, time);
    }

    // IADR-0064/0066 と同じ理由（FakeTimeProvider は中央パッケージ管理に未登録）で最小の偽装を置く。
    private sealed class TestTimeProvider(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset _now = now;

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan delta) => _now = _now.Add(delta);
    }

    private sealed class CountingSource(FxRate? rate) : IFxRateSource
    {
        public int Calls { get; private set; }

        public Task<FxRate?> GetRateToBaseAsync(Currency quote, CancellationToken cancellationToken = default)
        {
            Calls++;
            return Task.FromResult(rate);
        }
    }
}

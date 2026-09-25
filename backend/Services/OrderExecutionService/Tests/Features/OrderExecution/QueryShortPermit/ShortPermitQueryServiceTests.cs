using AiStockTrading.Shared.Contracts.Trading;
using AwesomeAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using OrderExecutionService.Common.Abstractions;
using OrderExecutionService.Features.OrderExecution.QueryShortPermit;
using Xunit;

namespace OrderExecutionService.Tests;

// FR-10, UC-06, ADR-0016 決定3（2026-08-06 改訂・同追記）, #967, IADR-0144 決定5, IADR-0425 決定1・3（T-10-1030〜T-10-1032）:
// 借株可否の照会を節約して返すこと。ブローカーの上限は 30 秒あたり 10 回で、**失敗した照会も枠を消費する**（IADR-0144 決定5 の実測）。
public class ShortPermitQueryServiceTests
{
    private static readonly DateTimeOffset T0 = new(2026, 9, 25, 14, 0, 0, TimeSpan.Zero);

    private static ShortPermitQueryService Service(IShortPermitSource? source, MutableClock clock) =>
        new(clock, NullLogger<ShortPermitQueryService>.Instance, source);

    /// <summary>T-10-1030: 照会の答え（許可／不許可／欄の欠落）を 3 つの状態として返す。答えを得た時刻を載せる。</summary>
    [Theory]
    [InlineData(true, ShortPermitStatus.Permitted, null)]
    [InlineData(false, ShortPermitStatus.NotPermitted, null)]
    [InlineData(null, ShortPermitStatus.Unknown, ShortPermitUnknownReasons.FieldMissing)]
    public async Task 照会の答えを許可不許可分からないとして返す(bool? answer, ShortPermitStatus expected, string? reason)
    {
        var source = new CountingSource(_ => answer);

        var view = await Service(source, new MutableClock(T0))
            .QueryAsync("AAPL", Market.UnitedStates, TestContext.Current.CancellationToken);

        view.Should().Be(new ShortPermitView(
            "AAPL", Market.UnitedStates, expected, reason, expected == ShortPermitStatus.Unknown ? null : T0));
        source.Calls.Should().Be(1);
    }

    /// <summary>T-10-1030: 照会の失敗（SIMULATE 口座ではこれが常態）は「分からない」。例外を外へ出さない。</summary>
    [Fact]
    public async Task 照会の失敗は分からないとして返す()
    {
        var source = new CountingSource(_ => throw new InvalidOperationException(
            "moomoo GetMarginRatio 失敗 retType=-1: Get Margin Trading Data does not support Stocks in US Market"));

        var view = await Service(source, new MutableClock(T0))
            .QueryAsync("AAPL", Market.UnitedStates, TestContext.Current.CancellationToken);

        view.Status.Should().Be(ShortPermitStatus.Unknown);
        view.UnknownReason.Should().Be(ShortPermitUnknownReasons.QueryFailed);
        view.ObservedAt.Should().BeNull();
    }

    /// <summary>T-10-1030: 米国株以外・照会を持たない発注先（内蔵 paper）は照会せず「分からない」。</summary>
    [Fact]
    public async Task 米国株以外と照会を持たない発注先は照会しない()
    {
        var source = new CountingSource(_ => true);

        var japan = await Service(source, new MutableClock(T0))
            .QueryAsync("7203", Market.Japan, TestContext.Current.CancellationToken);
        var paper = await Service(null, new MutableClock(T0))
            .QueryAsync("AAPL", Market.UnitedStates, TestContext.Current.CancellationToken);

        japan.UnknownReason.Should().Be(ShortPermitUnknownReasons.MarketNotSupported);
        paper.UnknownReason.Should().Be(ShortPermitUnknownReasons.BrokerNotSupported);
        source.Calls.Should().Be(0, "空売りの対象市場ではない銘柄にブローカーの枠を使わない");
    }

    /// <summary>T-10-1031: 答えは 60 秒、失敗は 30 秒キャッシュし、その間は照会し直さない（失敗時に即時リトライしない）。</summary>
    [Fact]
    public async Task 答えは60秒失敗は30秒キャッシュし照会し直さない()
    {
        var clock = new MutableClock(T0);
        var failNext = true;
        var source = new CountingSource(symbol => symbol == "GME" && failNext
            ? throw new InvalidOperationException("失敗")
            : true);
        var service = Service(source, clock);
        var ct = TestContext.Current.CancellationToken;

        (await service.QueryAsync("AAPL", Market.UnitedStates, ct)).Status.Should().Be(ShortPermitStatus.Permitted);
        (await service.QueryAsync("GME", Market.UnitedStates, ct)).Status.Should().Be(ShortPermitStatus.Unknown);
        source.Calls.Should().Be(2);

        clock.UtcNow = T0.AddSeconds(29);
        failNext = false;
        (await service.QueryAsync("AAPL", Market.UnitedStates, ct)).ObservedAt.Should().Be(T0, "キャッシュの答えは照会した時刻のまま");
        (await service.QueryAsync("GME", Market.UnitedStates, ct)).Status.Should().Be(ShortPermitStatus.Unknown, "失敗は 30 秒の間照会し直さない");
        source.Calls.Should().Be(2);

        clock.UtcNow = T0.AddSeconds(30);
        (await service.QueryAsync("GME", Market.UnitedStates, ct)).Status.Should().Be(ShortPermitStatus.Permitted, "30 秒で照会し直す");
        (await service.QueryAsync("AAPL", Market.UnitedStates, ct)).ObservedAt.Should().Be(T0, "答えは 60 秒まで持つ");
        source.Calls.Should().Be(3);

        clock.UtcNow = T0.AddSeconds(60);
        (await service.QueryAsync("AAPL", Market.UnitedStates, ct)).ObservedAt.Should().Be(T0.AddSeconds(60));
        source.Calls.Should().Be(4);
    }

    /// <summary>T-10-1032: 30 秒あたり 9 回（上限 10 回より 1 回少なく）の予算を失敗も含めて数え、超えたら照会せず「分からない」。</summary>
    [Fact]
    public async Task 予算は失敗も含めて30秒あたり9回で超えたら照会しない()
    {
        var clock = new MutableClock(T0);
        var source = new CountingSource(symbol => symbol.StartsWith('F') ? throw new InvalidOperationException("失敗") : true);
        var service = Service(source, clock);
        var ct = TestContext.Current.CancellationToken;

        // 失敗 3 回 ＋ 成功 6 回 ＝ 9 回（別々の銘柄＝キャッシュに当たらない）。
        foreach (var symbol in new[] { "F1", "F2", "F3", "S1", "S2", "S3", "S4", "S5", "S6" })
            await service.QueryAsync(symbol, Market.UnitedStates, ct);
        source.Calls.Should().Be(ShortPermitQueryService.BudgetPerWindow);

        var limited = await service.QueryAsync("S7", Market.UnitedStates, ct);
        limited.Status.Should().Be(ShortPermitStatus.Unknown);
        limited.UnknownReason.Should().Be(ShortPermitUnknownReasons.RateLimited);
        source.Calls.Should().Be(9, "予算切れでは照会しない");

        (await service.QueryAsync("S1", Market.UnitedStates, ct)).Status
            .Should().Be(ShortPermitStatus.Permitted, "キャッシュに当たる要求は予算を使わない");

        clock.UtcNow = T0.AddSeconds(30);
        (await service.QueryAsync("S7", Market.UnitedStates, ct)).Status
            .Should().Be(ShortPermitStatus.Permitted, "窓が過ぎれば照会する（予算切れはキャッシュしない）");
        source.Calls.Should().Be(10);
    }

    private sealed class CountingSource(Func<string, bool?> answer) : IShortPermitSource
    {
        public int Calls { get; private set; }

        public Task<bool?> GetShortPermitAsync(string symbol, Market market, CancellationToken cancellationToken = default)
        {
            Calls++;
            return Task.FromResult(answer(symbol));
        }
    }

    private sealed class MutableClock(DateTimeOffset utcNow) : IClock
    {
        public DateTimeOffset UtcNow { get; set; } = utcNow;
    }
}

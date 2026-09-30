using AiStockTrading.Shared.Contracts.Observability;
using AiStockTrading.Shared.Contracts.Trading;
using AiStockTrading.TestSupport.Metrics;
using AwesomeAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using OrderExecutionService.Common.Abstractions;
using OrderExecutionService.Features.OrderExecution.QueryDailyBars;
using Xunit;

namespace OrderExecutionService.Tests;

// FR-04, FR-15, ADR-0048 決定 2・3, #1118, IADR-0467 決定 2・5（T-10-1837・T-10-1839）: 日足の照会（送り手のサービス）。
// 🔴 米国株以外・paper では OpenD を撃たない。失敗は Unavailable で返し例外を外へ出さない。撃つたびに要求の件数と取得枠を計器へ出す。
public class DailyBarsQueryServiceTests
{
    private static readonly DateTimeOffset T0 = new(2026, 9, 29, 14, 0, 0, TimeSpan.Zero);
    private static readonly DateOnly From = new(2026, 8, 14);
    private static readonly DateOnly To = new(2026, 9, 28);
    private static readonly DailyBarView Bar1 = new(new DateOnly(2026, 9, 25), 1m, 2m, 0.5m, 1.5m, 100);
    private static readonly DailyBarView Bar2 = new(new DateOnly(2026, 9, 28), 1.5m, 2m, 1m, 1.75m, 200);

    private static DailyBarsQueryService Service(IDailyKLineSource? source, MutableClock clock, BusinessMetrics metrics) =>
        new(clock, metrics, NullLogger<DailyBarsQueryService>.Instance, source);

    [Fact]
    public async Task 取得できた足を昇順で返し_要求の件数と取得枠を計器へ出す()
    {
        var meterName = MeterCapture.NewIsolatedMeterName();
        using var capture = new MeterCapture(meterName);
        using var metrics = BusinessMetrics.WithMeterName(meterName);
        var source = new FakeSource(_ => new DailyKLineFetch(true, [Bar2, Bar1], null, 7, 293));

        var view = await Service(source, new MutableClock(T0), metrics)
            .QueryAsync("AAPL", Market.UnitedStates, From, To, TestContext.Current.CancellationToken);

        view.Should().BeEquivalentTo(new DailyBarsView(
            "AAPL", Market.UnitedStates, DailyBarsStatus.Available, null, From, To, [Bar1, Bar2]), o => o.WithStrictOrdering());
        source.Requests.Should().ContainSingle().Which.Should().Be(("AAPL", From, To));
        capture.TagValuesOf(BusinessMetricNames.KLineDailyRequests, BusinessMetricNames.TagOutcome).Should().Equal("succeeded");
        capture.ValuesOf(BusinessMetricNames.KLineQuotaUsed).Select(m => m.Value).Should().Equal(7d);
        capture.ValuesOf(BusinessMetricNames.KLineQuotaRemaining).Select(m => m.Value).Should().Equal(293d);
    }

    [Theory]
    [InlineData("非成功")]
    [InlineData("例外")]
    public async Task 失敗はUnavailableで返し例外を外へ出さない(string kind)
    {
        var meterName = MeterCapture.NewIsolatedMeterName();
        using var capture = new MeterCapture(meterName);
        using var metrics = BusinessMetrics.WithMeterName(meterName);
        var source = new FakeSource(_ => kind == "例外"
            ? throw new TimeoutException("reply timeout")
            : new DailyKLineFetch(false, [], "ret--1", null, null));

        var view = await Service(source, new MutableClock(T0), metrics)
            .QueryAsync("AAPL", Market.UnitedStates, From, To, TestContext.Current.CancellationToken);

        view.Status.Should().Be(DailyBarsStatus.Unavailable);
        view.UnavailableReason.Should().Be(DailyBarsUnavailableReasons.QueryFailed);
        view.Bars.Should().BeEmpty();
        capture.TagValuesOf(BusinessMetricNames.KLineDailyRequests, BusinessMetricNames.TagOutcome)
            .Should().Equal(kind == "例外" ? "failed" : "non-success");
        capture.ValuesOf(BusinessMetricNames.KLineQuotaUsed).Should().BeEmpty("欄の無い枠を 0 で埋めない");
    }

    [Fact]
    public async Task 米国株以外と発注先がOpenDを持たないときは撃たない()
    {
        using var metrics = BusinessMetrics.WithMeterName(MeterCapture.NewIsolatedMeterName());
        var source = new FakeSource(_ => new DailyKLineFetch(true, [Bar1], null, 1, 299));
        var ct = TestContext.Current.CancellationToken;

        var japan = await Service(source, new MutableClock(T0), metrics).QueryAsync("7203", Market.Japan, From, To, ct);
        var paper = await Service(null, new MutableClock(T0), metrics).QueryAsync("AAPL", Market.UnitedStates, From, To, ct);

        japan.UnavailableReason.Should().Be(DailyBarsUnavailableReasons.MarketNotSupported);
        paper.UnavailableReason.Should().Be(DailyBarsUnavailableReasons.BrokerNotSupported);
        source.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task 自制の予算を使い切ったら撃たず_窓が過ぎたら撃つ()
    {
        using var metrics = BusinessMetrics.WithMeterName(MeterCapture.NewIsolatedMeterName());
        var clock = new MutableClock(T0);
        var source = new FakeSource(_ => new DailyKLineFetch(true, [Bar1], null, null, null));
        var service = Service(source, clock, metrics);
        var ct = TestContext.Current.CancellationToken;

        for (var i = 0; i < DailyBarsQueryService.BudgetPerWindow; i++)
            (await service.QueryAsync($"S{i}", Market.UnitedStates, From, To, ct)).Status.Should().Be(DailyBarsStatus.Available);
        var limited = await service.QueryAsync("OVER", Market.UnitedStates, From, To, ct);
        clock.UtcNow = T0 + DailyBarsQueryService.BudgetWindow;
        var after = await service.QueryAsync("AFTER", Market.UnitedStates, From, To, ct);

        limited.UnavailableReason.Should().Be(DailyBarsUnavailableReasons.RateLimited);
        after.Status.Should().Be(DailyBarsStatus.Available);
        source.Requests.Should().HaveCount(DailyBarsQueryService.BudgetPerWindow + 1);
    }

    [Fact]
    public async Task キャンセルは伝える()
    {
        using var metrics = BusinessMetrics.WithMeterName(MeterCapture.NewIsolatedMeterName());
        using var cts = new CancellationTokenSource();
        var source = new FakeSource(_ =>
        {
            cts.Cancel();
            throw new OperationCanceledException(cts.Token);
        });

        var act = () => Service(source, new MutableClock(T0), metrics).QueryAsync("AAPL", Market.UnitedStates, From, To, cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    internal sealed class FakeSource(Func<(string Symbol, DateOnly From, DateOnly To), DailyKLineFetch> answer) : IDailyKLineSource
    {
        public List<(string Symbol, DateOnly From, DateOnly To)> Requests { get; } = [];

        public Task<DailyKLineFetch> FetchForwardAdjustedAsync(
            string symbol, DateOnly from, DateOnly to, CancellationToken cancellationToken = default)
        {
            Requests.Add((symbol, from, to));
            return Task.FromResult(answer((symbol, from, to)));
        }
    }

    private sealed class MutableClock(DateTimeOffset utcNow) : IClock
    {
        public DateTimeOffset UtcNow { get; set; } = utcNow;
    }
}

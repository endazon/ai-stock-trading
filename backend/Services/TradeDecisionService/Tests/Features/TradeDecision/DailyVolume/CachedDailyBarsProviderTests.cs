using AiStockTrading.Shared.Contracts.Trading;
using AwesomeAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using TradeDecisionService.Features.TradeDecision;
using TradeDecisionService.Infrastructure.ExternalServices;
using Xunit;
using static TradeDecisionService.Tests.DailyBarsTestData;

namespace TradeDecisionService.Tests;

// FR-04, FR-02, ADR-0048 決定 2・3, #1118, IADR-0467 決定 3: 前営業日までの確定足を銘柄 × 取引日で 1 回だけ取る。
// 🔴 窓の両端（作業仕様書の窓の表）: 当日の未確定足を捨てる（P1）・日付をまたいだら取り直す（P2）・前営業日との突き合わせ（P3 は計算側）。
public class CachedDailyBarsProviderTests
{
    private static CachedDailyBarsProvider Provider(FakeSource source, ManualTimeProvider time) =>
        new(source, time, NullLogger<CachedDailyBarsProvider>.Instance);

    // ---- T-10-1830: 当日の未確定足を捨てる・要求の範囲 ----
    [Fact]
    public async Task 応答に当日の足が混ざっても捨て_前営業日までを昇順で返す()
    {
        // OpenD は当日を含む範囲に未確定の当日足を返す（#1117 の実測: 出来高が平常の約 1/3）。
        var bars = Bars(Monday, Repeat(9_000, 21));
        var intraday = new DailyBar(Tuesday, 100m, 100m, 100m, 100m, 3_000);
        var source = new FakeSource(_ => [intraday, .. Enumerable.Reverse(bars)]);
        using var provider = Provider(source, new ManualTimeProvider(TuesdayMorning));

        var confirmed = await provider.GetConfirmedBarsAsync("AAPL", Market.UnitedStates, TestContext.Current.CancellationToken);

        confirmed!.Bars.Should().Equal(bars, "当日の足を捨て、日付の昇順に並べる");
        confirmed.TradingDay.Should().Be(Tuesday);
        confirmed.ExpectedPreviousTradingDay.Should().Be(Monday);
        DailyVolumeContext.From(confirmed).PreviousDayVolume.Should().Be(9_000, "当日の 3,000 を前日として読まない");
    }

    [Fact]
    public async Task 要求は前営業日の45暦日前から当日の前日まで()
    {
        var source = new FakeSource(_ => []);
        using var provider = Provider(source, new ManualTimeProvider(TuesdayMorning));

        await provider.GetConfirmedBarsAsync("AAPL", Market.UnitedStates, TestContext.Current.CancellationToken);

        source.Requests.Should().ContainSingle().Which.Should().Be(
            ("AAPL", Market.UnitedStates, Monday.AddDays(-CachedDailyBarsProvider.LookbackCalendarDays), new DateOnly(2026, 9, 28)));
    }

    // ---- T-10-1831: キャッシュ（同じ取引日は 1 回・日付をまたぐと取り直す・失敗は 15 分おく・銘柄ごと・日本株は取らない） ----
    [Fact]
    public async Task 同じ取引日は銘柄ごとに1回だけ取る()
    {
        var source = new FakeSource(_ => Bars(Monday, Repeat(9_000, 21)));
        var time = new ManualTimeProvider(TuesdayMorning);
        using var provider = Provider(source, time);
        var ct = TestContext.Current.CancellationToken;

        var first = await provider.GetConfirmedBarsAsync("AAPL", Market.UnitedStates, ct);
        time.Now = TuesdayMorning.AddHours(6); // 16:00 ET（同じ取引日）
        var second = await provider.GetConfirmedBarsAsync("aapl", Market.UnitedStates, ct);
        await provider.GetConfirmedBarsAsync("MSFT", Market.UnitedStates, ct);

        second.Should().BeSameAs(first);
        source.Requests.Select(r => r.Symbol).Should().Equal("AAPL", "MSFT");
    }

    // 🔴 P2: 米国東部の日付が変わったら取り直す（前日の取得を翌日に使わない）。
    [Fact]
    public async Task 米国東部の日付が変わったら取り直す()
    {
        var source = new FakeSource(r => Bars(MarketTradingDays.PreviousTradingDay(Market.UnitedStates, r.To.AddDays(1)), Repeat(9_000, 21)));
        var time = new ManualTimeProvider(new DateTimeOffset(2026, 9, 30, 3, 30, 0, TimeSpan.Zero)); // 9/29 23:30 ET
        using var provider = Provider(source, time);
        var ct = TestContext.Current.CancellationToken;

        var before = await provider.GetConfirmedBarsAsync("AAPL", Market.UnitedStates, ct);
        time.Now = new DateTimeOffset(2026, 9, 30, 13, 45, 0, TimeSpan.Zero); // 9/30 09:45 ET
        var after = await provider.GetConfirmedBarsAsync("AAPL", Market.UnitedStates, ct);

        source.Requests.Should().HaveCount(2);
        before!.ExpectedPreviousTradingDay.Should().Be(Monday);
        after!.ExpectedPreviousTradingDay.Should().Be(Tuesday);
        DailyVolumeContext.From(after).PreviousDay.Should().Be(Tuesday, "翌日は前日（9/29）の確定足を前営業日として使う");
    }

    [Fact]
    public async Task 取得できなければ15分は撃ち直さず_過ぎたら撃ち直す()
    {
        var answers = new Queue<IReadOnlyList<DailyBar>?>([null, Bars(Monday, Repeat(9_000, 21))]);
        var source = new FakeSource(_ => answers.Dequeue());
        var time = new ManualTimeProvider(TuesdayMorning);
        using var provider = Provider(source, time);
        var ct = TestContext.Current.CancellationToken;

        (await provider.GetConfirmedBarsAsync("AAPL", Market.UnitedStates, ct)).Should().BeNull();
        time.Now = TuesdayMorning + CachedDailyBarsProvider.FailureRetryInterval - TimeSpan.FromSeconds(1);
        (await provider.GetConfirmedBarsAsync("AAPL", Market.UnitedStates, ct)).Should().BeNull();
        source.Requests.Should().HaveCount(1, "失敗の直後に判断のサイクルごとに叩かない");

        time.Now = TuesdayMorning + CachedDailyBarsProvider.FailureRetryInterval;
        (await provider.GetConfirmedBarsAsync("AAPL", Market.UnitedStates, ct)).Should().NotBeNull();
        source.Requests.Should().HaveCount(2);
    }

    [Fact]
    public async Task 日本株は要求せずnull()
    {
        var source = new FakeSource(_ => []);
        using var provider = Provider(source, new ManualTimeProvider(TuesdayMorning));

        (await provider.GetConfirmedBarsAsync("7203", Market.Japan, TestContext.Current.CancellationToken)).Should().BeNull();
        source.Requests.Should().BeEmpty();
    }

    // ---- T-10-1832: 例外は null（判断を止めない）・キャンセルは伝える ----
    [Fact]
    public async Task 取得元の例外はnullにして15分おく()
    {
        var source = new FakeSource(_ => throw new HttpRequestException("connection refused"));
        using var provider = Provider(source, new ManualTimeProvider(TuesdayMorning));
        var ct = TestContext.Current.CancellationToken;

        (await provider.GetConfirmedBarsAsync("AAPL", Market.UnitedStates, ct)).Should().BeNull();
        (await provider.GetConfirmedBarsAsync("AAPL", Market.UnitedStates, ct)).Should().BeNull();
        source.Requests.Should().HaveCount(1);
    }

    [Fact]
    public async Task キャンセルは伝える()
    {
        using var cts = new CancellationTokenSource();
        var source = new FakeSource(_ =>
        {
            cts.Cancel();
            cts.Token.ThrowIfCancellationRequested();
            return [];
        });
        using var provider = Provider(source, new ManualTimeProvider(TuesdayMorning));

        var act = () => provider.GetConfirmedBarsAsync("AAPL", Market.UnitedStates, cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task 無効の口は要求せずnullを返す()
    {
        var provider = new NoOpDailyBarsProvider();

        provider.IsEnabled.Should().BeFalse();
        (await provider.GetConfirmedBarsAsync("AAPL", Market.UnitedStates, TestContext.Current.CancellationToken)).Should().BeNull();
    }

    internal sealed class FakeSource(Func<(string Symbol, Market Market, DateOnly From, DateOnly To), IReadOnlyList<DailyBar>?> answer)
        : IDailyBarsSource
    {
        public List<(string Symbol, Market Market, DateOnly From, DateOnly To)> Requests { get; } = [];

        public Task<IReadOnlyList<DailyBar>?> FetchAsync(
            string symbol, Market market, DateOnly from, DateOnly to, CancellationToken cancellationToken = default)
        {
            var request = (symbol, market, from, to);
            Requests.Add(request);
            return Task.FromResult(answer(request));
        }
    }
}

using RiskManagementService.Application.Adapters;
using RiskManagementService.Application.Ports;
using RiskManagementService.Domain;
using RiskManagementService.Infrastructure.Steps;
using AiStockTrading.Shared.Contracts.Events;
using AiStockTrading.Shared.Contracts.Trading;
using AiStockTrading.TestSupport.Messaging;
using AwesomeAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Wolverine;
using Wolverine.Tracking;
using Xunit;

namespace RiskManagementService.Infrastructure.Tests;

// FR-20, FR-12, #385, 06_daytrading-review §4.2, IADR-0150:
// 稼働の観測（BrokerAvailabilityObserved）から Stage 1 の営業日数を作る**供給経路**の検証。
//
// #333（IADR-0137）は判定側を作ったが観測を生む仕組みが無く、営業日数は 0 のままだった。
// 本テスト群は「観測が届くようになったこと」と、**米国東部時間（DST 込み）へ正しく写ること**を固定する。
public class BrokerAvailabilityObservedConsumerTests
{
    private static readonly TimeSpan Interval = TimeSpan.FromMinutes(30);

    private static Task<IHost> BuildHostAsync(InMemoryStage1TradingDayObservationStore uptime) =>
        Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Services.AddSingleton<IStage1TradingDayObservationStore>(uptime);
                opts.Discovery.DisableConventionalDiscovery()
                    .IncludeType<BrokerAvailabilityObservedHandler>();
                opts.StubAllExternalTransports();
            })
            .StartAsync();

    // 場中（ET）を通して観測が届いた 1 日ぶんを送る。時刻は UTC で与える。
    private static async Task ObserveSessionAsync(
        IHost host,
        DateTimeOffset firstObservationUtc,
        BrokerProvider provider = BrokerProvider.MoomooSimulate,
        int count = 14)
    {
        for (var i = 0; i < count; i++)
        {
            await host.TrackActivityForTest().InvokeMessageAndWaitAsync(new BrokerAvailabilityObserved(
                provider, firstObservationUtc.AddMinutes(30 * i), Interval));
        }
    }

    // 正: 観測が営業日 1 日として積み上がる（供給が実際に働くこと）。
    // 2026-08-03（月）は EDT（UTC-4）であり、9:30 ET ＝ 13:30 UTC。30 分刻み 14 回で 16:00 ET に届く。
    [Fact]
    public async Task 場中を通した観測が営業日1日として積み上がる()
    {
        var uptime = new InMemoryStage1TradingDayObservationStore();
        using var host = await BuildHostAsync(uptime);

        await ObserveSessionAsync(host, new DateTimeOffset(2026, 8, 3, 13, 30, 0, TimeSpan.Zero));

        uptime.GetQualifiedTradingDayCount().Should().Be(1);

        await host.StopAsync();
    }

    // **否定形**: 供給が途絶えれば 0 日のまま（水増しされない）。
    [Fact]
    public async Task 観測が届かなければ営業日は増えない()
    {
        var uptime = new InMemoryStage1TradingDayObservationStore();
        using var host = await BuildHostAsync(uptime);

        uptime.GetQualifiedTradingDayCount().Should().Be(0);

        await host.StopAsync();
    }

    // **否定形**: 内蔵 paper の稼働は算入されない（許可制・IADR-0142 決定2）。除外日としては数える。
    [Fact]
    public async Task 内蔵paperの稼働は算入されず除外日として数えられる()
    {
        var uptime = new InMemoryStage1TradingDayObservationStore();
        using var host = await BuildHostAsync(uptime);

        await ObserveSessionAsync(
            host, new DateTimeOffset(2026, 8, 3, 13, 30, 0, TimeSpan.Zero), BrokerProvider.InternalPaper);

        uptime.GetQualifiedTradingDayCount().Should().Be(0);
        uptime.GetExcludedInternalPaperDayCount().Should().Be(1);

        await host.StopAsync();
    }

    // **否定形**: 同じ観測が再配送されても分数は増えない（Wolverine の再試行で二重計上しない）。
    [Fact]
    public async Task 同じ観測が再配送されても二重に積まない()
    {
        var uptime = new InMemoryStage1TradingDayObservationStore();
        using var host = await BuildHostAsync(uptime);

        // 13:30 UTC ＝ 9:30 EDT。1 回目は初回のため 0 分、2 回目（14:00 UTC）で 30 分ぶん積まれる。
        var first = new DateTimeOffset(2026, 8, 3, 13, 30, 0, TimeSpan.Zero);
        var second = first.AddMinutes(30);
        foreach (var instant in new[] { first, second, second, first })
        {
            await host.TrackActivityForTest().InvokeMessageAndWaitAsync(
                new BrokerAvailabilityObserved(BrokerProvider.MoomooSimulate, instant, Interval));
        }

        // 30 分では 50% に届かないため算入されないが、**二重計上されていないこと**が要点である。
        uptime.GetQualifiedTradingDayCount().Should().Be(0);

        await host.StopAsync();
    }

    // **境界値（DST）**: §4.2 は判定の基準時刻を米国東部時間と定める。
    // 同じ現地時刻（9:30 ET）は EDT でも EST でも同じ分（570）へ写らなければならない——
    // UTC の固定時刻で数えると切替日にだけ 1 時間ずれ、稼働分数が狂う。
    //
    // 実 tz データに依存するため、**境界値を名指しで置く**。
    //   2026-03-09（月）: 前日 3/8 に EDT へ切替済み → 9:30 ET ＝ 13:30 UTC
    //   2026-11-02（月）: 前日 11/1 に EST へ戻る    → 9:30 ET ＝ 14:30 UTC
    [Theory]
    [InlineData(2026, 3, 9, 13)]    // EDT（UTC-4）
    [InlineData(2026, 11, 2, 14)]   // EST（UTC-5）
    public async Task DSTの切替をまたいでも同じ現地時刻が同じ営業日として積まれる(
        int year, int month, int day, int utcHourOfOpen)
    {
        var uptime = new InMemoryStage1TradingDayObservationStore();
        using var host = await BuildHostAsync(uptime);

        await ObserveSessionAsync(
            host, new DateTimeOffset(year, month, day, utcHourOfOpen, 30, 0, TimeSpan.Zero));

        uptime.GetQualifiedTradingDayCount().Should().Be(
            1, "9:30〜16:00 ET を満たした日は、EST でも EDT でも 1 営業日として数える");

        await host.StopAsync();
    }

    // **否定形（DST）**: UTC 固定で数える実装への退行の検知。
    // 11 月（EST）に「EDT の前提」で 13:30 UTC から観測すると、現地は 8:30〜15:00 ET であり
    // 9:30 前の 1 時間は時間外として積まれない（390 分に 60 分足りない＝330 分）。
    // それでも通常日仮説 330 ÷ 390 ＝ 84.6% は閾値を超えるため、**分数そのもの**で退行を見る。
    [Fact]
    public async Task 東部時間へ写さない実装は稼働分数がずれる()
    {
        var uptime = new InMemoryStage1TradingDayObservationStore();
        using var host = await BuildHostAsync(uptime);

        await ObserveSessionAsync(host, new DateTimeOffset(2026, 11, 2, 13, 30, 0, TimeSpan.Zero));

        // 8:30〜15:00 ET ＝ 通常取引時間のうち 9:30〜15:00 の 330 分だけが積まれる。
        var (_, minuteOfDay) = EasternTradingDate.SessionMinuteOf(
            new DateTimeOffset(2026, 11, 2, 13, 30, 0, TimeSpan.Zero));
        minuteOfDay.Should().Be(8 * 60 + 30, "EST では 13:30 UTC は 8:30 ET である");

        uptime.GetQualifiedTradingDayCount().Should().Be(1, "330 ÷ 390 ＝ 84.6% で閾値は超える");

        await host.StopAsync();
    }
}

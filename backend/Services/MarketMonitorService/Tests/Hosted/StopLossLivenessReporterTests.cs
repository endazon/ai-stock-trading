using AiStockTrading.Shared.Contracts.Trading;
using AwesomeAssertions;
using MarketMonitorService.Domain;
using MarketMonitorService.Hosted;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Xunit;

namespace MarketMonitorService.Tests;

// FR-10, FR-03, ADR-0040 決定1（S1）, #902, IADR-0365 決定2・決定3: 損切り評価の生存要約と価格欠落の Warning。
// 時刻は引数で注入する（壁時計・実時間の待ちを使わない。#885/#900/#901 の教訓）。
public class StopLossLivenessReporterTests
{
    private static readonly DateTimeOffset T0 = new(2026, 9, 23, 14, 0, 0, TimeSpan.Zero);

    private static readonly MonitorOptions Defaults = new(); // 要約 300 秒・欠落しきい値 300 秒

    internal sealed class RecordingLogger<T> : ILogger<T>
    {
        public List<(LogLevel Level, string Message)> Entries { get; } = [];

        public IEnumerable<string> Informations => Entries.Where(e => e.Level == LogLevel.Information).Select(e => e.Message);

        public IEnumerable<string> Warnings => Entries.Where(e => e.Level == LogLevel.Warning).Select(e => e.Message);

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) =>
            Entries.Add((logLevel, formatter(state, exception)));
    }

    private static (StopLossLivenessReporter Reporter, RecordingLogger<StopLossLivenessReporter> Log) Create(
        MonitorOptions? options = null)
    {
        var log = new RecordingLogger<StopLossLivenessReporter>();
        return (new StopLossLivenessReporter(Options.Create(options ?? Defaults), log), log);
    }

    private static StopLossEvaluation Aapl(decimal? price, DateTimeOffset at) =>
        new("AAPL", Market.UnitedStates, TradeSide.Buy, 707, 338.51m, price, at);

    [Fact]
    public void T_10_622_保有を評価した最初の巡回で件数_銘柄_価格_ライン_評価時刻を含む要約を1行出す()
    {
        // T-10-622, FR-10, #902, IADR-0365 決定2
        var (reporter, log) = Create();

        reporter.Observe([Aapl(340.12m, T0)], T0);

        log.Informations.Should().ContainSingle();
        var line = log.Informations.Single();
        line.Should().Contain("保有 1 件");
        line.Should().Contain("AAPL/UnitedStates");
        line.Should().Contain("707株");
        line.Should().Contain("現在値=340.12");
        line.Should().Contain("ライン=338.51");
        line.Should().Contain("評価=2026-09-23T14:00:00.0000000+00:00");
        log.Warnings.Should().BeEmpty();
    }

    [Fact]
    public void T_10_623_間隔内は要約を重ねず_偽時計を間隔ぶん進めると再び出す()
    {
        // T-10-623, FR-10, #902, IADR-0365 決定2（毎巡回 60 秒 × 4 回は間隔 300 秒の内側）
        var (reporter, log) = Create();

        reporter.Observe([Aapl(340m, T0)], T0);
        for (var i = 1; i <= 4; i++)
            reporter.Observe([Aapl(340m + i, T0.AddSeconds(60 * i))], T0.AddSeconds(60 * i));
        reporter.Observe([Aapl(339m, T0.AddSeconds(299))], T0.AddSeconds(299));

        log.Informations.Should().HaveCount(1);

        reporter.Observe([Aapl(339.5m, T0.AddSeconds(300))], T0.AddSeconds(300));

        log.Informations.Should().HaveCount(2);
        log.Informations.Last().Should().Contain("現在値=339.5");
    }

    [Fact]
    public void T_10_624_価格欠落はしきい値以内なら警告せず_超えたら1回_以後は要約間隔に1回まで()
    {
        // T-10-624, FR-10, #902, IADR-0365 決定3（欠落は最後に価格が取れた時刻から数える）
        var (reporter, log) = Create();

        reporter.Observe([Aapl(340m, T0)], T0);
        reporter.Observe([Aapl(null, T0.AddSeconds(60))], T0.AddSeconds(60));
        reporter.Observe([Aapl(null, T0.AddSeconds(300))], T0.AddSeconds(300)); // ちょうどしきい値: まだ出さない

        log.Warnings.Should().BeEmpty();

        reporter.Observe([Aapl(null, T0.AddSeconds(301))], T0.AddSeconds(301)); // 超えた

        log.Warnings.Should().ContainSingle();
        var warning = log.Warnings.Single();
        warning.Should().Contain("AAPL/UnitedStates");
        warning.Should().Contain("ライン=338.51");
        warning.Should().Contain("最終価格 340");
        warning.Should().Contain("自動では決済しません");

        // 連続中は要約間隔（300 秒）に 1 回まで。
        reporter.Observe([Aapl(null, T0.AddSeconds(360))], T0.AddSeconds(360));
        reporter.Observe([Aapl(null, T0.AddSeconds(600))], T0.AddSeconds(600));
        log.Warnings.Should().HaveCount(1);

        reporter.Observe([Aapl(null, T0.AddSeconds(601))], T0.AddSeconds(601));
        log.Warnings.Should().HaveCount(2);
    }

    [Fact]
    public void T_10_624b_一度も価格が取れていない保有は最初の欠落から数え_要約は取得できずと示す()
    {
        // T-10-624, FR-10, #902, IADR-0365 決定3（再起動直後から欠落している場合）
        var (reporter, log) = Create();

        reporter.Observe([Aapl(null, T0)], T0);

        log.Informations.Should().ContainSingle().Which.Should().Contain("現在値=取得できず（最終 なし");
        log.Warnings.Should().BeEmpty();

        reporter.Observe([Aapl(null, T0.AddSeconds(301))], T0.AddSeconds(301));

        log.Warnings.Should().ContainSingle().Which.Should().Contain("最終取得 なし");
    }

    [Fact]
    public void T_10_625_価格が回復したらInformationを1回出し_欠落の連続を解く()
    {
        // T-10-625, FR-10, #902, IADR-0365 決定3
        var (reporter, log) = Create();

        reporter.Observe([Aapl(340m, T0)], T0);
        reporter.Observe([Aapl(null, T0.AddSeconds(400))], T0.AddSeconds(400));
        log.Warnings.Should().ContainSingle();

        reporter.Observe([Aapl(341m, T0.AddSeconds(460))], T0.AddSeconds(460));

        log.Informations.Should().Contain(m => m.Contains("価格取得が回復") && m.Contains("現在値=341"));

        // 連続が解けたので、次の欠落は新しい起点（最後の取得 460 秒）から数え直す。
        reporter.Observe([Aapl(null, T0.AddSeconds(700))], T0.AddSeconds(700));
        log.Warnings.Should().HaveCount(1);

        // 回復が 2 回目に出ることはない（警告していない取得は回復とみなさない）。
        reporter.Observe([Aapl(342m, T0.AddSeconds(720))], T0.AddSeconds(720));
        log.Informations.Count(m => m.Contains("価格取得が回復")).Should().Be(1);
    }

    [Fact]
    public void T_10_626_保有0件では何も出さず_次に保有が現れたら即時に要約する()
    {
        // T-10-626, FR-10, #902, IADR-0365 決定2
        var (reporter, log) = Create();

        reporter.Observe([], T0);
        log.Entries.Should().BeEmpty();

        reporter.Observe([Aapl(340m, T0.AddSeconds(60))], T0.AddSeconds(60));
        reporter.Observe([], T0.AddSeconds(120)); // 手仕舞い
        reporter.Observe([Aapl(339m, T0.AddSeconds(180))], T0.AddSeconds(180)); // 間隔内でも新しい保有は即時

        log.Informations.Should().HaveCount(2);
        log.Warnings.Should().BeEmpty();
    }

    [Fact]
    public void T_10_626b_評価から外れた銘柄は要約から消える()
    {
        // T-10-626, FR-10, #902, IADR-0365 決定2
        var (reporter, log) = Create();
        var msft = new StopLossEvaluation("MSFT", Market.UnitedStates, TradeSide.Buy, 10, 400m, 410m, T0);

        reporter.Observe([Aapl(340m, T0), msft], T0);
        log.Informations.Single().Should().Contain("保有 2 件").And.Contain("MSFT/UnitedStates");

        reporter.Observe([Aapl(340m, T0.AddSeconds(300))], T0.AddSeconds(300));

        log.Informations.Last().Should().Contain("保有 1 件").And.NotContain("MSFT");
    }
}

using AiStockTrading.Shared.Contracts.Trading;
using AwesomeAssertions;
using Microsoft.Extensions.Logging;
using OrderExecutionService.Common.Abstractions;
using OrderExecutionService.Domain;
using OrderExecutionService.Features.OrderExecution.GuardProtectiveStops;
using Xunit;

namespace OrderExecutionService.Tests;

// FR-10, ADR-0040 決定1（S1）, #902, IADR-0365 決定5: Active なソフトウェア逆指値（S1）の低頻度の要約。
// 時刻は偽時計で進める（壁時計・実時間の待ちを使わない。#885/#900/#901 の教訓）。
public class SoftwareStopLivenessReporterTests
{
    private static readonly DateTimeOffset T0 = new(2026, 9, 23, 14, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan Interval = TimeSpan.FromMinutes(5);

    internal sealed class MutableClock(DateTimeOffset now) : IClock
    {
        public DateTimeOffset UtcNow { get; set; } = now;
    }

    internal sealed class RecordingLogger<T> : ILogger<T>
    {
        public List<(LogLevel Level, string Message)> Entries { get; } = [];

        public IEnumerable<string> Informations => Entries.Where(e => e.Level == LogLevel.Information).Select(e => e.Message);

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            lock (Entries) Entries.Add((logLevel, formatter(state, exception)));
        }
    }

    internal static ProtectiveStopOrder SoftwareStop(
        string symbol = "AAPL", int? remaining = 707, decimal trigger = 338.51m, DateTimeOffset? triggeredAt = null)
    {
        var id = Guid.NewGuid();
        return new ProtectiveStopOrder(
            id, ProtectiveStopIds.SoftwareStopId(id), string.Empty, symbol, Market.UnitedStates, TradeSide.Buy,
            ProductType.Cash, BrokerProvider.MoomooSimulate, 707, trigger, 1m, 0, ProtectiveStopState.Active,
            T0.AddHours(-2), T0.AddHours(-2), StopLossExecutionMethod.SoftwareStop,
            triggeredAt, triggeredAt is null ? null : 338m, remaining);
    }

    private static ProtectiveStopOrder BrokerStop()
    {
        var id = Guid.NewGuid();
        return new ProtectiveStopOrder(
            id, ProtectiveStopIds.StopDecisionId(id, 1), "stop-s0", "MSFT", Market.UnitedStates, TradeSide.Buy,
            ProductType.Cash, BrokerProvider.MoomooSimulate, 10, 900m, 1m, 1, ProtectiveStopState.Active,
            T0.AddHours(-1), T0.AddHours(-1));
    }

    private static (SoftwareStopLivenessReporter Reporter, MutableClock Clock, RecordingLogger<SoftwareStopLivenessReporter> Log) Create()
    {
        var clock = new MutableClock(T0);
        var log = new RecordingLogger<SoftwareStopLivenessReporter>();
        return (new SoftwareStopLivenessReporter(clock, log, Interval), clock, log);
    }

    [Fact]
    public void T_10_629_ActiveなS1行があれば件数_銘柄_残保護_トリガー_到達状態を含む要約を出す()
    {
        // T-10-629, FR-10, #902, IADR-0365 決定5
        var (reporter, _, log) = Create();
        var aapl = SoftwareStop();
        var nvda = SoftwareStop("NVDA", remaining: null, trigger: 140m, triggeredAt: T0.AddMinutes(-1));

        reporter.ReportIfDue(() => [aapl, nvda, BrokerStop()]).Should().BeTrue();

        var line = log.Informations.Should().ContainSingle().Which;
        line.Should().Contain("Active 2 件");
        line.Should().Contain("AAPL/UnitedStates Buy 残保護=707株 トリガー=338.51 未到達");
        line.Should().Contain($"EntryDecisionId={aapl.EntryDecisionId}");
        line.Should().Contain("NVDA/UnitedStates Buy 残保護=未確定（エントリー約定待ち） トリガー=140 到達済み（2026-09-23T13:59:00.0000000+00:00 検知価格=338）");
        line.Should().NotContain("MSFT"); // S0 行は要約に含めない
    }

    [Fact]
    public void T_10_630_間隔内はストアを読まず出さない_偽時計を間隔ぶん進めると再び出す()
    {
        // T-10-630, FR-10, #902, IADR-0365 決定5・決定D（ストアは間隔に 1 回だけ読む）
        var (reporter, clock, log) = Create();
        var loads = 0;
        IReadOnlyList<ProtectiveStopOrder> Load()
        {
            loads++;
            return [SoftwareStop()];
        }

        reporter.ReportIfDue(Load).Should().BeTrue();
        for (var i = 1; i <= 9; i++) // ガードの既定巡回 30 秒 × 9 回 = 270 秒（間隔の内側）
        {
            clock.UtcNow = T0.AddSeconds(30 * i);
            reporter.ReportIfDue(Load).Should().BeFalse();
        }

        clock.UtcNow = T0.AddSeconds(299);
        reporter.ReportIfDue(Load).Should().BeFalse();
        loads.Should().Be(1);
        log.Informations.Should().HaveCount(1);

        clock.UtcNow = T0.AddSeconds(300);
        reporter.ReportIfDue(Load).Should().BeTrue();
        loads.Should().Be(2);
        log.Informations.Should().HaveCount(2);
    }

    [Fact]
    public void T_10_631_S1行が無ければ出さず_S1が無くてもストアは間隔に1回しか読まない()
    {
        // T-10-631, FR-10, #902, IADR-0365 決定5
        var (reporter, clock, log) = Create();
        var loads = 0;
        IReadOnlyList<ProtectiveStopOrder> Load()
        {
            loads++;
            return [BrokerStop()];
        }

        reporter.ReportIfDue(Load).Should().BeFalse();
        clock.UtcNow = T0.AddSeconds(30);
        reporter.ReportIfDue(Load).Should().BeFalse();

        loads.Should().Be(1);
        log.Entries.Should().BeEmpty();
    }

    // ---- T-10-2667, FR-10, #856, IADR-0538: 発注の記録が無いまま未確定で残る S1（幽霊の疑い）の識別 ----

    // 2026-10-05 の BeforeSend の AAPL と同じ形: 未確定・未到達・作成から 25 時間。
    private static ProtectiveStopOrder Ghost(DateTimeOffset? createdAt = null) =>
        SoftwareStop(remaining: null, trigger: 327.83m) with { CreatedAt = createdAt ?? T0.AddHours(-25) };

    private static IEnumerable<string> Warnings(RecordingLogger<SoftwareStopLivenessReporter> log) =>
        log.Entries.Where(e => e.Level == LogLevel.Warning).Select(e => e.Message);

    [Fact]
    public void T_10_2667_発注記録の無い古い未確定のS1は要約に印が付き後始末の案内つきのWarningが出る()
    {
        // T-10-2667, FR-10, #856, IADR-0538
        var (reporter, _, log) = Create();
        var ghost = Ghost();

        reporter.ReportIfDue(() => [ghost], _ => false).Should().BeTrue();

        log.Informations.Should().ContainSingle().Which.Should().Contain("発注記録なし（幽霊の疑い）");
        var warning = Warnings(log).Should().ContainSingle().Which;
        warning.Should().Contain($"EntryDecisionId={ghost.EntryDecisionId}");
        warning.Should().Contain("発注の記録が無いまま残った S1 の記録を閉じる", "Runbook の節へ案内する");
        warning.Should().Contain("自動で閉じません");
        ghost.State.Should().Be(ProtectiveStopState.Active);
    }

    [Theory]
    [InlineData("記録がある")]
    [InlineData("若い")]
    [InlineData("到達済み")]
    [InlineData("残保護が確定済み")]
    [InlineData("照会の口が無い")]
    public void T_10_2667_記録がある_若い_到達済み_確定済み_照会の口が無いS1には印を付けない(string kind)
    {
        // T-10-2667, FR-10, #856, IADR-0538: 印は「発注の記録が無い」かつ「経過済み」かつ「未確定・未到達」のときだけ。
        var (reporter, _, log) = Create();
        var stop = kind switch
        {
            "若い" => Ghost(T0.AddHours(-23)),
            "到達済み" => Ghost() with { TriggeredAt = T0.AddMinutes(-1), TriggeredPrice = 327m },
            "残保護が確定済み" => Ghost() with { RemainingProtected = 719 },
            _ => Ghost(),
        };
        Func<Guid, bool>? exists = kind switch
        {
            "記録がある" => _ => true,
            "照会の口が無い" => null,
            _ => _ => false,
        };

        reporter.ReportIfDue(() => [stop], exists).Should().BeTrue();

        log.Informations.Should().ContainSingle().Which.Should().NotContain("幽霊の疑い");
        Warnings(log).Should().BeEmpty();
    }

    [Fact]
    public void T_10_2667_幽霊の疑いのWarningは初回と24時間ごとだけ_該当しなくなったら記憶を消す()
    {
        // T-10-2667, FR-10, #856, IADR-0538: 要約は 5 分ごとだが、Warning は同じ行について 1 日 1 回（窓の両端）。
        var (reporter, clock, log) = Create();
        var ghost = Ghost();
        var recorded = false;

        for (var minutes = 0; minutes <= 24 * 60; minutes += 5)
        {
            clock.UtcNow = T0.AddMinutes(minutes);
            reporter.ReportIfDue(() => [ghost], _ => recorded);
        }

        Warnings(log).Should().HaveCount(2, "初回（0 分）と 24 時間後だけ。間隔を見ないと 289 回、時刻を見ないと 1 回");
        log.Informations.Should().HaveCount(289, "要約そのものは今までどおり 5 分ごと");

        // 該当しなくなった（発注記録が見えた）→ 記憶を消す → 再び該当したら初回として出す。
        recorded = true;
        clock.UtcNow = T0.AddMinutes(24 * 60 + 5);
        reporter.ReportIfDue(() => [ghost], _ => recorded);
        recorded = false;
        clock.UtcNow = T0.AddMinutes(24 * 60 + 10);
        reporter.ReportIfDue(() => [ghost], _ => recorded);

        Warnings(log).Should().HaveCount(3);
    }
}


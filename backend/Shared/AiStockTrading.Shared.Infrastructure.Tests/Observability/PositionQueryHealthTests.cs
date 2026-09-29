using AiStockTrading.Shared.Contracts.Events;
using AiStockTrading.Shared.Infrastructure.Composable.Observability;
using AwesomeAssertions;
using Xunit;

namespace AiStockTrading.Shared.Infrastructure.Tests.Observability;

// 🔴 NFR, FR-10, FR-11, #1092, IADR-0462 決定1〜3: 建玉照会・保有照会の状態は、**変わったときだけ**台帳へ出す。
// 周期ごとの成功（平常）・失敗の連続を洪水させず、再起動で状態が消えても最初の失敗は必ず出す。
public class PositionQueryHealthTests
{
    private static readonly DateTimeOffset T0 = new(2026, 9, 30, 17, 0, 0, TimeSpan.Zero);

    private sealed class ManualTime(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;

        public override DateTimeOffset GetUtcNow() => Now;
    }

    // ---- T-10-1767: 状態の遷移 ----
    [Fact]
    public void T_10_1767_起動直後の失敗は出し_失敗の連続は出さず_回復に始まりと回数を載せる()
    {
        var tracker = new PositionQueryHealthTracker();

        var first = tracker.Observe(PositionQuerySource.ProtectiveStopGuard, succeeded: false, "Transient", T0);
        first.Should().NotBeNull("再起動で状態が消えても、最初の失敗は必ず出す");
        first!.Event.Should().Be(new PositionQueryStatusChanged(
            PositionQuerySource.ProtectiveStopGuard, PositionQueryStatus.Failing, PositionQueryStatus.Unknown,
            "Transient", T0, 1, T0));

        tracker.Observe(PositionQuerySource.ProtectiveStopGuard, false, "Other", T0.AddSeconds(30))
            .Should().BeNull("失敗の連続は出さない（30 秒ごとに洪水させない）");
        tracker.Observe(PositionQuerySource.ProtectiveStopGuard, false, null, T0.AddSeconds(60)).Should().BeNull();

        var recovered = tracker.Observe(PositionQuerySource.ProtectiveStopGuard, true, null, T0.AddSeconds(90));
        recovered!.Event.Should().Be(new PositionQueryStatusChanged(
            PositionQuerySource.ProtectiveStopGuard, PositionQueryStatus.Healthy, PositionQueryStatus.Failing,
            null, T0, 3, T0.AddSeconds(90)), "回復には失敗の始まりと、続いた照会の回数を載せる");

        tracker.Observe(PositionQuerySource.ProtectiveStopGuard, true, null, T0.AddSeconds(120))
            .Should().BeNull("平常の周期的な成功は出さない");

        var again = tracker.Observe(PositionQuerySource.ProtectiveStopGuard, false, null, T0.AddSeconds(150));
        again!.Event.PreviousStatus.Should().Be(PositionQueryStatus.Healthy, "成功の後の失敗は、また出す");
        again.Event.FailedQueries.Should().Be(1);
    }

    // T-10-1767: 起動直後の成功は 1 回だけ出す（前のプロセスで始まった失敗の区間を、再起動の後に閉じる）。
    [Fact]
    public void T_10_1767_起動直後の成功は1回だけ出し_失敗の始まりは持たない()
    {
        var tracker = new PositionQueryHealthTracker();

        var first = tracker.Observe(PositionQuerySource.BrokerPositionSnapshot, true, null, T0);
        first!.Event.Should().Be(new PositionQueryStatusChanged(
            PositionQuerySource.BrokerPositionSnapshot, PositionQueryStatus.Healthy, PositionQueryStatus.Unknown,
            null, null, 0, T0));

        tracker.Observe(PositionQuerySource.BrokerPositionSnapshot, true, null, T0.AddMinutes(10)).Should().BeNull();
    }

    // ---- T-10-1768: 発生源ごとに独立する ----
    [Fact]
    public void T_10_1768_発生源ごとに状態を持ち_一方の失敗が他方を変えない()
    {
        var tracker = new PositionQueryHealthTracker();
        tracker.Observe(PositionQuerySource.ProtectiveStopGuard, true, null, T0);
        tracker.Observe(PositionQuerySource.BrokerPositionSnapshot, true, null, T0);

        tracker.Observe(PositionQuerySource.ProtectiveStopGuard, false, null, T0.AddSeconds(30))!
            .Event.Source.Should().Be(PositionQuerySource.ProtectiveStopGuard);

        tracker.Observe(PositionQuerySource.BrokerPositionSnapshot, true, null, T0.AddMinutes(10))
            .Should().BeNull("スナップショットは成功のまま（ガードの失敗で変わらない）");
        tracker.Observe(PositionQuerySource.BrokerPositionSnapshot, false, null, T0.AddMinutes(20))!
            .Event.PreviousStatus.Should().Be(PositionQueryStatus.Healthy, "スナップショット自身の失敗は、ガードと別に出す");
        tracker.Observe(PositionQuerySource.TradeDecisionHoldings, false, null, T0.AddMinutes(20))!
            .Event.PreviousStatus.Should().Be(PositionQueryStatus.Unknown, "観測していない発生源は起動直後のまま");
    }

    // ---- T-10-1769: 発行の失敗で状態を戻し、次の観測で出し直す。報告は例外を投げない ----
    [Fact]
    public async Task T_10_1769_発行に失敗したら状態を戻して次の照会で出し直し_例外を投げない()
    {
        var time = new ManualTime(T0);
        var published = new List<PositionQueryStatusChanged>();
        var fail = true;
        var reporter = new PositionQueryHealthReporter(
            e =>
            {
                if (fail)
                    throw new InvalidOperationException("発行先が壊れている");
                published.Add((PositionQueryStatusChanged)e);
                return ValueTask.CompletedTask;
            },
            time);

        await reporter.ReportAsync(PositionQuerySource.OrderDispatch, succeeded: false, "Other");
        published.Should().BeEmpty();

        fail = false;
        time.Now = T0.AddSeconds(5);
        await reporter.ReportAsync(PositionQuerySource.OrderDispatch, succeeded: false);

        var e = published.Should().ContainSingle("発行できなかった失敗は、次の失敗で出し直す（黙って Failing のまま沈まない）").Subject;
        e.Status.Should().Be(PositionQueryStatus.Failing);
        e.PreviousStatus.Should().Be(PositionQueryStatus.Unknown);
        e.OccurredAt.Should().Be(T0.AddSeconds(5));

        time.Now = T0.AddSeconds(10);
        fail = true;
        await reporter.ReportAsync(PositionQuerySource.OrderDispatch, succeeded: true);
        fail = false;
        time.Now = T0.AddSeconds(15);
        await reporter.ReportAsync(PositionQuerySource.OrderDispatch, succeeded: true);

        published.Should().HaveCount(2, "発行できなかった回復も、次の成功で出し直す");
        published[1].Status.Should().Be(PositionQueryStatus.Healthy);
        published[1].PreviousStatus.Should().Be(PositionQueryStatus.Failing);
        published[1].FailingSince.Should().Be(T0.AddSeconds(5), "戻した後も失敗の始まりを失わない");
        published[1].FailedQueries.Should().Be(1);
    }

    // T-10-1769: 戻すのは自分が進めた状態だけ（その後の別の変化を巻き戻さない）。
    [Fact]
    public void T_10_1769_発行の失敗を戻すのは_その後に別の変化が無いときだけ()
    {
        var tracker = new PositionQueryHealthTracker();
        var failed = tracker.Observe(PositionQuerySource.SoftwareStopClose, false, null, T0)!;
        var recovered = tracker.Observe(PositionQuerySource.SoftwareStopClose, true, null, T0.AddSeconds(1))!;
        recovered.Event.Status.Should().Be(PositionQueryStatus.Healthy);

        tracker.Revert(failed); // 古い変化の戻し。回復（新しい状態）を巻き戻してはならない。

        tracker.Observe(PositionQuerySource.SoftwareStopClose, true, null, T0.AddSeconds(2))
            .Should().BeNull("回復済みの状態は残っている");
    }
}

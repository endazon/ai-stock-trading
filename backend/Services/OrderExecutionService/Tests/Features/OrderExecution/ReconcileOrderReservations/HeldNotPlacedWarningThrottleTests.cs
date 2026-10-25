using AiStockTrading.Shared.Contracts.Trading;
using AwesomeAssertions;
using OrderExecutionService.Features.OrderExecution.ReconcileOrderReservations;
using Xunit;

namespace OrderExecutionService.Tests;

// FR-05, NFR-09, #856, IADR-0538: 据え置いた「未発注」判定の Warning の畳み込み。
// 窓の両端（前の端＝初回・状態変化で必ず出す／後の端＝再通知の間隔で出す）を 1 本ずつ固定する（規則 11）。
public class HeldNotPlacedWarningThrottleTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 6, 0, 46, 49, TimeSpan.Zero);
    private static readonly TimeSpan Reminder = TimeSpan.FromHours(24);
    private static readonly Guid Aapl = Guid.Parse("4908458f-7859-49a3-89bc-a743ec07d1bc");

    private static ReservationReconciliationVerdict Held(Guid id, BrokerProvider? provider = BrokerProvider.MoomooSimulate) =>
        new(id, provider, ReservationReconciliationVerdictKind.HeldNotPlaced);

    private static ReservationReconciliationVerdict Of(Guid id, ReservationReconciliationVerdictKind kind) =>
        new(id, BrokerProvider.MoomooSimulate, kind);

    [Fact]
    public void T_10_2664_同じ予約の据え置きが続くとWarningは初回と24時間ごとだけ()
    {
        // T-10-2664, FR-05, #856, IADR-0538: 毎時の巡回で同じ予約が据え置かれ続ける（2026-10-05 の BeforeSend の AAPL）。
        var throttle = new HeldNotPlacedWarningThrottle(Reminder);

        var warned = new List<int>();
        for (var hour = 0; hour <= 48; hour++)
        {
            var notice = throttle.Classify([Held(Aapl)], scanComplete: true, T0.AddHours(hour)).Should().ContainSingle().Which;
            notice.DecisionId.Should().Be(Aapl);
            notice.ReservationProvider.Should().Be(BrokerProvider.MoomooSimulate);
            notice.FirstWarnedAt.Should().Be(T0, "初回の時刻は再通知を挟んでも変わらない");
            if (notice.Warn)
                warned.Add(hour);
            else
                notice.NextWarnAt.Should().Be(T0.AddHours(hour < 24 ? 24 : 48));
        }

        // 🔴 後の端: 再通知の間隔を見ないと 24・48 時間後が空く（前の端だけの形）。畳まないと 49 回すべて出る。
        warned.Should().Equal(0, 24, 48);
    }

    [Fact]
    public void T_10_2665_不確定を挟むと次の据え置きは初回として警告する()
    {
        // T-10-2665, FR-05, #856, IADR-0538: 状態が変わったら畳まない（前の端）。
        var throttle = new HeldNotPlacedWarningThrottle(Reminder);
        throttle.Classify([Held(Aapl)], true, T0).Single().Warn.Should().BeTrue();
        throttle.Classify([Held(Aapl)], true, T0.AddHours(1)).Single().Warn.Should().BeFalse();

        throttle.Classify([Of(Aapl, ReservationReconciliationVerdictKind.Indeterminate)], true, T0.AddHours(2))
            .Should().BeEmpty("据え置き以外の判定は通知の対象ではない");

        var again = throttle.Classify([Held(Aapl)], true, T0.AddHours(3)).Single();
        again.Warn.Should().BeTrue("記憶を消さないと、解けた後の再発が畳まれる（後の端だけの形）");
        again.FirstWarnedAt.Should().Be(T0.AddHours(3));
    }

    [Fact]
    public void T_10_2665_取引環境が変わると警告する()
    {
        // T-10-2665, NFR-09, #1051, #856, IADR-0538: どちらの門の判断材料かが変わる。
        var throttle = new HeldNotPlacedWarningThrottle(Reminder);
        throttle.Classify([Held(Aapl, null)], true, T0).Single().Warn.Should().BeTrue();

        var changed = throttle.Classify([Held(Aapl, BrokerProvider.MoomooSimulate)], true, T0.AddHours(1)).Single();
        changed.Warn.Should().BeTrue();
        changed.FirstWarnedAt.Should().Be(T0.AddHours(1));
    }

    [Fact]
    public void T_10_2665_全件を走査した巡回に載らなかった予約は記憶を消す()
    {
        // T-10-2665, FR-05, #856, IADR-0538: 人の SQL で Reserved を出た・確定した予約が、後で同じ DecisionId で据え置かれたら初回。
        var throttle = new HeldNotPlacedWarningThrottle(Reminder);
        throttle.Classify([Held(Aapl)], true, T0).Single().Warn.Should().BeTrue();

        throttle.Classify([], scanComplete: true, T0.AddHours(1)).Should().BeEmpty();

        throttle.Classify([Held(Aapl)], true, T0.AddHours(2)).Single().Warn.Should().BeTrue();
    }

    [Fact]
    public void T_10_2665_バッチが満杯の巡回では載らなかった予約の記憶を消さない()
    {
        // T-10-2665, FR-05, #856, IADR-0538: 走査されなかっただけの予約を「解けた」と読まない。
        var throttle = new HeldNotPlacedWarningThrottle(Reminder);
        var other = Guid.NewGuid();
        throttle.Classify([Held(Aapl)], true, T0).Single().Warn.Should().BeTrue();

        throttle.Classify([Held(other)], scanComplete: false, T0.AddHours(1)).Single().Warn.Should().BeTrue();

        throttle.Classify([Held(Aapl), Held(other)], true, T0.AddHours(2))
            .Should().OnlyContain(n => !n.Warn, "どちらも途切れずに据え置かれている");
    }

    [Fact]
    public void T_10_2665_予約ごとに独立して畳む()
    {
        // T-10-2665, FR-05, #856, IADR-0538: 新しい予約の初回は、既に畳んでいる予約に引きずられない。
        var throttle = new HeldNotPlacedWarningThrottle(Reminder);
        throttle.Classify([Held(Aapl)], true, T0);

        var newcomer = Guid.NewGuid();
        var notices = throttle.Classify([Held(Aapl), Held(newcomer)], true, T0.AddHours(1));

        notices.Should().HaveCount(2);
        notices.Single(n => n.DecisionId == Aapl).Warn.Should().BeFalse();
        notices.Single(n => n.DecisionId == newcomer).Warn.Should().BeTrue();
    }

    [Theory]
    [InlineData(null, 24)]
    [InlineData(0, 1)]
    [InlineData(-5, 1)]
    [InlineData(6, 6)]
    [InlineData(168, 168)]
    [InlineData(1000, 168)]
    public void T_10_2669_再通知の間隔は既定24時間で1から168時間に収める(int? hours, int expected)
    {
        // T-10-2669, FR-05, #856, IADR-0538: 既定は 1 日 1 回。1 週間より長く黙らせない。
        var options = new ReconciliationOptions();
        if (hours is { } h)
            options.HeldNotPlacedReminderHours = h;

        options.HeldNotPlacedReminder.Should().Be(TimeSpan.FromHours(expected));
    }
}

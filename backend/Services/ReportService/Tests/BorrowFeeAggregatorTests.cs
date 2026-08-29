using ReportService.Domain;
using AiStockTrading.Shared.Contracts.Events;
using AiStockTrading.Shared.Contracts.Trading;
using AwesomeAssertions;
using Xunit;

namespace ReportService.Tests;

// FR-06, FR-16, #338, ADR-0016 決定3・決定15, ADR-0027 決定1・決定4, 04_report-templates 月報 §6.1:
// 借株料の集計（純関数）を固定する。
//
// 🔴 ADR-0027 決定4 の明文: 「**計上日の料率が取得できなかった日は、その日の計上を『未供給』として記録し、
// 0 として計上しない。0 を積むと『その日は費用が発生しなかった』と読めてしまう。**」
public class BorrowFeeAggregatorTests
{
    private static readonly DateTimeOffset T0 = new(2026, 8, 5, 3, 0, 0, TimeSpan.Zero);

    private static BorrowFeeAccrued Accrual(string symbol, decimal amountUsd, decimal rate, int day) =>
        new(symbol, Market.UnitedStates, new DateOnly(2026, 8, day), rate, 10_000m, amountUsd, T0);

    private static BorrowFeeAccrualUnavailable Unavailable(string symbol, int day) =>
        new(symbol, Market.UnitedStates, new DateOnly(2026, 8, day), "料率照会に失敗", T0);

    [Fact]
    public void 計上できた日の合計と銘柄別内訳を返す()
    {
        var s = BorrowFeeAggregator.Aggregate(new BorrowFeeRecord(
            [Accrual("AAPL", 1.50m, 0.06m, 1), Accrual("AAPL", 2.50m, 0.08m, 2), Accrual("TSLA", 4m, 0.12m, 1)],
            []));

        s.TotalUsd.Should().Be(8m);
        s.BySymbolUsd.Should().Equal(("AAPL", 4m, 0.08m), ("TSLA", 4m, 0.12m));
        s.MaxRateAnnual.Should().Be(0.12m);
        s.UnavailableDayCount.Should().Be(0);
    }

    // 🔴 **否定形**: 未計上の日が 0 円として合計へ混ざらない。
    // **対の肯定形**: 未計上の件数は必ず残る（合計から消えるだけで、記録からは消えない）。
    [Fact]
    public void 未計上の日を合計へ混ぜず件数として残す()
    {
        var s = BorrowFeeAggregator.Aggregate(new BorrowFeeRecord(
            [Accrual("AAPL", 3m, 0.06m, 1)],
            [Unavailable("AAPL", 2), Unavailable("TSLA", 2)]));

        s.TotalUsd.Should().Be(3m);          // 否定形: 未計上 2 件は 0 円として足されていない
        s.UnavailableDayCount.Should().Be(2); // 肯定形: 件数としては残っている
    }

    // 🔴 計上が 1 件も無い期間の適用年率を **0% と書かない**（「年率 0% が適用された」と読める）。
    [Fact]
    public void 計上が無ければ適用年率は該当なしであり0ではない()
    {
        var s = BorrowFeeAggregator.Aggregate(new BorrowFeeRecord([], [Unavailable("AAPL", 1)]));

        s.MaxRateAnnual.Should().BeNull();
        s.TotalUsd.Should().Be(0m);
        s.UnavailableDayCount.Should().Be(1);
    }

    // 空売り建玉が一度も無かった期間（計上も未計上も無い）は、いずれの列も空である。
    [Fact]
    public void 記録が空なら合計ゼロで件数もゼロである()
    {
        var s = BorrowFeeAggregator.Aggregate(new BorrowFeeRecord([], []));

        s.TotalUsd.Should().Be(0m);
        s.BySymbolUsd.Should().BeEmpty();
        s.MaxRateAnnual.Should().BeNull();
        s.UnavailableDayCount.Should().Be(0);
    }

    // ADR-0016 決定3: 借株料の年率上限は 20%。定数が動くと統制の判定がずれるため固定する。
    [Fact]
    public void 年率上限は20パーセントである()
    {
        BorrowFeeAggregator.MaxAnnualRate.Should().Be(0.20m);
    }
}

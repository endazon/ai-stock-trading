using AiStockTrading.RiskManagement.Domain;
using AiStockTrading.Shared.Contracts.Trading;
using AwesomeAssertions;
using Xunit;

namespace AiStockTrading.RiskManagement.Domain.Tests;

// FR-20, FR-12, SC-03, INDEX 決定 46, #334, IADR-0142:
// Stage 1 の合格集計から**内蔵 paper を構造的に排除する**ことの検証。
//
// 計画（FR-20）:
//   「Stage 1 の合格判定（経過営業日数・取引件数・統制違反件数）は `SIMULATE` の約定のみで集計し、
//     **内蔵 `paper` の約定・稼働日数を算入してはならない**（`paper` で稼働した営業日は除外日数として
//     別に数え、進捗表示に併記する）。内蔵 `paper` は外部へ一度も発注しないため、算入を許すと
//     60 営業日・100 件という合格証跡が擬似約定で積み上がる」
//
// **本テスト群の主眼は否定形である。** 混入しても「成功したように見える」失敗であり、
// 肯定形（SIMULATE なら数える）だけでは検知できない。
public class Stage1AggregationTests
{
    private const int FullDay = Stage1DayQualification.RegularSessionMinutesFullDay;   // 390 分

    private static Stage1TradingDayObservation Day(BrokerProvider provider, int day = 1, int operational = FullDay) =>
        new(new DateOnly(2026, 8, day), FullDay, operational, provider);

    // #386, IADR-0149 決定2: 計上単位は「約定した新規建て注文 1 件」（DecisionId で一意）。
    private static Stage1FillObservation Fill(
        BrokerProvider provider, int day = 1, PositionEffect effect = PositionEffect.Open, Guid? decisionId = null) =>
        new(decisionId ?? Guid.NewGuid(), new DateOnly(2026, 8, day), provider, effect);

    // ------------------------------------------------------------------
    // 否定形: 内蔵 paper は 1 件も 1 日も算入されない
    // ------------------------------------------------------------------

    // **稼働率 100% でも算入しない。** 内蔵 paper は外部へ一度も発注していない。
    [Fact]
    public void 内蔵paperの稼働日は稼働率100パーセントでも算入されない()
    {
        var day = Day(BrokerProvider.InternalPaper);

        Stage1DayQualification.UptimeRatio(day).Should().Be(1m, "稼働率そのものは満たしている");
        Stage1DayQualification.MeetsUptimeThreshold(day).Should().BeTrue();
        Stage1DayQualification.Qualifies(day).Should().BeFalse(
            "計画は内蔵 paper の稼働日数を算入してはならないと定める（FR-20）");
    }

    [Fact]
    public void 内蔵paperの約定は取引件数に算入されない()
    {
        var fills = Enumerable.Range(1, 200).Select(i => Fill(BrokerProvider.InternalPaper, day: 1 + (i % 28)));

        Stage1Aggregation.CountTrades(fills).Should().Be(
            0,
            "内蔵 paper の擬似約定を数えると 100 件という合格証跡がデバッグ稼働で積み上がる");
    }

    // 混在データ（本 issue の退行防止テストの中核）: paper を混ぜても SIMULATE の実績だけが残る。
    [Fact]
    public void paperの約定と稼働日を混ぜても合格判定は汚染されない()
    {
        Stage1TradingDayObservation[] days =
        [
            Day(BrokerProvider.MoomooSimulate, day: 1),
            Day(BrokerProvider.InternalPaper, day: 2),   // 除外（paper）
            Day(BrokerProvider.MoomooSimulate, day: 3),
            Day(BrokerProvider.InternalPaper, day: 4),   // 除外（paper）
            Day(BrokerProvider.MoomooSimulate, day: 5),
        ];
        Stage1FillObservation[] fills =
        [
            Fill(BrokerProvider.MoomooSimulate, day: 1),
            Fill(BrokerProvider.InternalPaper, day: 2),
            Fill(BrokerProvider.InternalPaper, day: 2),
            Fill(BrokerProvider.MoomooSimulate, day: 3),
        ];

        var progress = Stage1Aggregation.Aggregate(days, fills);

        progress.QualifiedTradingDays.Should().Be(3);
        progress.TradeCount.Should().Be(2);
        progress.ExcludedInternalPaperDays.Should().Be(2);
    }

    // FR-20:「`paper` で稼働した営業日は除外日数として**別に数え**、進捗表示に併記する」。
    [Fact]
    public void paper稼働日は除外日数として別掲される()
    {
        Stage1TradingDayObservation[] days =
        [
            Day(BrokerProvider.InternalPaper, day: 1),
            Day(BrokerProvider.InternalPaper, day: 2),
            Day(BrokerProvider.InternalPaper, day: 3),
        ];

        var progress = Stage1Aggregation.Aggregate(days, []);

        progress.QualifiedTradingDays.Should().Be(0);
        progress.ExcludedInternalPaperDays.Should().Be(
            3,
            "「経過 0 / 60 営業日（paper 稼働により 3 日を除外）」と説明できる必要がある（SC-03）");
    }

    // IADR-0142 決定3: 除外日数に数えるのは「paper であり、**かつ稼働率の条件は満たしていた**日」に限る。
    // 休場日・稼働不足の日はもともと算入されない日であり、paper を理由に除外されたわけではない。
    [Fact]
    public void 休場日と稼働不足の日はpaperでも除外日数に数えない()
    {
        Stage1TradingDayObservation[] days =
        [
            new(new DateOnly(2026, 8, 1), RegularSessionMinutes: 0, OperationalMinutes: 0, BrokerProvider.InternalPaper),
            new(new DateOnly(2026, 8, 2), FullDay, OperationalMinutes: 100, BrokerProvider.InternalPaper),
            Day(BrokerProvider.InternalPaper, day: 3),
        ];

        Stage1DayQualification.CountExcludedInternalPaperDays(days).Should().Be(
            1,
            "混ぜると「paper 稼働により N 日を除外」という画面の説明が嘘になる");
    }

    // ------------------------------------------------------------------
    // 許可制（IADR-0142 決定2）: 名指しで許可された発注先だけを数える
    // ------------------------------------------------------------------

    // 計画は `MoomooReal` の扱いを名指ししていない。**算入しない**（許可制）。
    // Stage 1 に実弾の約定が現れること自体が異常であり、合格証跡へ混ぜてはならない。
    [Fact]
    public void 実弾の約定と稼働日もStage1には算入されない()
    {
        Stage1DayQualification.Qualifies(Day(BrokerProvider.MoomooReal)).Should().BeFalse();
        Stage1Aggregation.CountTrades([Fill(BrokerProvider.MoomooReal)]).Should().Be(0);
    }

    // プロパティベース: 算入されるのは moomoo SIMULATE ちょうど 1 値に限る。
    // 発注先が増えたときに「黙って算入される」ことがないことを機械的に固定する。
    [Fact]
    public void 算入される発注先はmoomooSIMULATEだけである()
    {
        foreach (var provider in Enum.GetValues<BrokerProvider>())
        {
            var expected = provider == BrokerProvider.MoomooSimulate;

            Stage1Aggregation.IsCounted(provider).Should().Be(expected, "provider={0}", provider);
            Stage1DayQualification.Qualifies(Day(provider)).Should().Be(expected, "provider={0}", provider);
            Stage1Aggregation.CountTrades([Fill(provider)]).Should().Be(expected ? 1 : 0, "provider={0}", provider);
        }
    }

    // ------------------------------------------------------------------
    // 否定形: paper だけで 60 営業日・100 件を積んでも昇格しない（計画が名指しする最悪の失敗）
    // ------------------------------------------------------------------
    [Fact]
    public void paperだけで60営業日と100件を積んでも昇格可能にならない()
    {
        var days = Enumerable.Range(0, 60)
            .Select(i => new Stage1TradingDayObservation(
                new DateOnly(2026, 1, 1).AddDays(i), FullDay, FullDay, BrokerProvider.InternalPaper))
            .ToArray();
        var fills = Enumerable.Range(0, 100)
            .Select(i => Fill(BrokerProvider.InternalPaper, day: (i % 28) + 1))
            .ToArray();

        var progress = Stage1Aggregation.Aggregate(days, fills);

        Stage1Gate.Evaluate(progress, Stage1GateCriteria.Default).Should().Be(
            Stage1GateOutcome.InProgress,
            "内蔵 paper で 60 営業日・100 件を積んでも Stage 2 へは進めない（FR-20）");
        progress.ExcludedInternalPaperDays.Should().Be(60);
    }

    // 対比（肯定形）: 同じ量を moomoo SIMULATE で積めば昇格可能になる。
    // 否定形だけだと「常に InProgress を返す」実装でも緑になるため、対照を置く。
    [Fact]
    public void 同じ量をSIMULATEで積めば昇格可能になる()
    {
        var days = Enumerable.Range(0, 60)
            .Select(i => new Stage1TradingDayObservation(
                new DateOnly(2026, 1, 1).AddDays(i), FullDay, FullDay, BrokerProvider.MoomooSimulate))
            .ToArray();
        var fills = Enumerable.Range(0, 100)
            .Select(i => Fill(BrokerProvider.MoomooSimulate, day: (i % 28) + 1))
            .ToArray();

        var progress = Stage1Aggregation.Aggregate(days, fills);

        Stage1Gate.Evaluate(progress, Stage1GateCriteria.Default).Should().Be(Stage1GateOutcome.Promotable);
        progress.ExcludedInternalPaperDays.Should().Be(0);
    }

    // 除外日数は**合格判定に一切影響しない**（表示のための値である）。
    [Theory]
    [InlineData(0)]
    [InlineData(3)]
    [InlineData(1_000)]
    public void 除外日数は合格判定に影響しない(int excluded)
    {
        var progress = new Stage1Progress(
            QualifiedTradingDays: 60, TradeCount: 100, ExcludedInternalPaperDays: excluded);

        Stage1Gate.Evaluate(progress, Stage1GateCriteria.Default).Should().Be(Stage1GateOutcome.Promotable);
    }

    // 空入力は 0（供給元が繋がるまでの既定。fail-safe＝昇格しない）。
    [Fact]
    public void 観測が無ければ進捗はすべて0である()
    {
        var progress = Stage1Aggregation.Aggregate([], []);

        progress.Should().Be(new Stage1Progress(0, 0, 0));
    }
}

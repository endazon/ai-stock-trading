using AiStockTrading.Shared.Contracts.Trading;
using AwesomeAssertions;
using Xunit;

namespace AiStockTrading.RiskManagement.Domain.Tests;

// FR-20, FR-11, #387, 06_daytrading-review §4.1 条件1, IADR-0148:
// クラス C 統制違反件数の集計（発注審査の観測 → 件数 / 未供給）。
//
// 受け入れ基準（#387）:
// - クラス C の拒否を含む発注拒否が 1 件として計上される
// - 否定形: 1 回の拒否で複数のクラス C 理由が返っても 1 件
// - 否定形: クラス A / B の拒否が積み上がっても件数が増えない
// - 供給が無い状態と「違反 0 件」を区別できる
public class ControlViolationAggregationTests
{
    private static OrderScreeningObservation Simulate(params RejectionReason[] reasons) =>
        new(Guid.NewGuid(), BrokerProvider.MoomooSimulate, reasons);

    // FR-20, §4.1 条件1: クラス C を含む拒否は 1 件として計上される。
    [Theory]
    [InlineData(RejectionReason.BannedSymbol)]
    [InlineData(RejectionReason.ManipulativeOrderPattern)]
    public void クラスCの拒否は1件として計上される(RejectionReason classC)
    {
        var tally = ControlViolationAggregation.Tally([Simulate(classC)]);

        tally.Should().NotBeNull();
        tally!.Count.Should().Be(1);
        tally.BlocksPromotion.Should().BeTrue();
    }

    // **否定形**（§4.1）: **計上単位は 1 回の発注拒否につき 1 件**である。
    // 1 回の拒否で 2 つのクラス C 理由が返っても 2 件にはならない。
    [Fact]
    public void 拒否1回に複数のクラスC理由が含まれても1件である()
    {
        var oneRejection = Simulate(
            RejectionReason.BannedSymbol,
            RejectionReason.ManipulativeOrderPattern,
            RejectionReason.ShortSellDisabled);

        ControlViolationAggregation.Tally([oneRejection])!.Count.Should().Be(1);
    }

    // **否定形**（§4.1）: 同一の発注審査が再送で二重に観測されても 1 件である
    // （メッセージングの再送で違反件数が水増しされない）。
    [Fact]
    public void 同一DecisionIdの重複観測は1件である()
    {
        var observation = new OrderScreeningObservation(
            Guid.NewGuid(), BrokerProvider.MoomooSimulate, [RejectionReason.BannedSymbol]);

        ControlViolationAggregation.Tally([observation, observation, observation])!
            .Count.Should().Be(1);
    }

    // **否定形**（§4.1・ADR-0016 決定10）: クラス A（統制の正常作動）とクラス B（緊急停止中・段階制約）は
    // いくら積み上がっても件数を増やさない。クラス A を数えるとゲートが恒久ブロックになる。
    [Fact]
    public void クラスAとクラスBの拒否は100回積み上がっても0件である()
    {
        RejectionReason[][] nonC =
        [
            [RejectionReason.ShortSellDisabled, RejectionReason.BorrowUnavailable],
            [RejectionReason.BorrowCostExceeded],
            [RejectionReason.ShortExposureExceeded],
            [RejectionReason.MaintenanceMarginBreach],
            [RejectionReason.DividendRecordDateNear],
            [RejectionReason.ShortPriceFloorBreach],
            [RejectionReason.StopOrderRequired],
            [RejectionReason.BuyInBanned],
            [RejectionReason.PerOrderAmountExceeded],
            [RejectionReason.DailyLossLimitReached],
            [RejectionReason.KillSwitchActive],
            [RejectionReason.TradingPaused],
            [RejectionReason.StageProhibitsLiveTrading],
            [RejectionReason.StageCapitalCapExceeded],
            [RejectionReason.StageProductTypeProhibited],
            [RejectionReason.StageShortSellReleaseUnmet],
        ];

        var observations = Enumerable.Range(0, 100)
            .Select(i => Simulate(nonC[i % nonC.Length]))
            .ToList();

        var tally = ControlViolationAggregation.Tally(observations);

        // **供給はされている**（審査は動いている）が、計上される違反は 0 件である。
        tally.Should().NotBeNull("算入対象の審査が観測されている＝集計は供給されている");
        tally!.Count.Should().Be(0);
        tally.BlocksPromotion.Should().BeFalse();
    }

    // **本 issue の核心**（#387）: 観測が 1 件も無ければ **null（未供給）** を返す。
    // 0 件を返すと、供給元の不在が「違反 0 件＝合格」として読まれる。
    [Fact]
    public void 観測が無ければ未供給を返す()
    {
        ControlViolationAggregation.Tally([]).Should().BeNull();
    }

    // **否定形**（FR-20・IADR-0142 決定2）: 内蔵 paper / moomoo REAL の審査は
    // 件数にも「供給された」判定にも寄与しない。**許可制**（SIMULATE のみ算入）である。
    [Theory]
    [InlineData(BrokerProvider.InternalPaper)]
    [InlineData(BrokerProvider.MoomooReal)]
    public void 算入対象外の発注先は供給にも件数にも寄与しない(BrokerProvider provider)
    {
        var observations = new List<OrderScreeningObservation>
        {
            new(Guid.NewGuid(), provider, [RejectionReason.BannedSymbol]),
            new(Guid.NewGuid(), provider, []),
        };

        ControlViolationAggregation.Tally(observations).Should()
            .BeNull("算入対象外の発注先だけでは Stage 1 の合格証跡は作れない");
    }

    // 混在: 算入対象外の観測はクラス C を含んでいても件数へ入らない。
    // 算入対象の審査が 1 件でもあれば供給ありとして 0 件を主張できる。
    [Fact]
    public void 算入対象外のクラスC拒否は件数に混ざらない()
    {
        var observations = new List<OrderScreeningObservation>
        {
            new(Guid.NewGuid(), BrokerProvider.InternalPaper, [RejectionReason.BannedSymbol]),
            new(Guid.NewGuid(), BrokerProvider.MoomooSimulate, []),
        };

        var tally = ControlViolationAggregation.Tally(observations);

        tally.Should().NotBeNull();
        tally!.Count.Should().Be(0);
    }

    // 正の確認: 承認された審査（拒否理由が空）も「供給されている」根拠になる。
    // これが無いと、違反が 1 件も起きていない正常運用で永久に昇格できない。
    [Fact]
    public void 承認された審査だけでも供給ありとして0件を返す()
    {
        var tally = ControlViolationAggregation.Tally([Simulate()]);

        tally.Should().NotBeNull();
        tally!.Count.Should().Be(0);
    }

    // 複数回の独立した拒否は件数として積み上がる（計上単位が「1 回の拒否につき 1 件」であること）。
    [Fact]
    public void 別々の拒否はそれぞれ1件として積み上がる()
    {
        var observations = new List<OrderScreeningObservation>
        {
            Simulate(RejectionReason.BannedSymbol),
            Simulate(RejectionReason.ManipulativeOrderPattern),
            Simulate(RejectionReason.ShortSellDisabled),
            Simulate(),
        };

        ControlViolationAggregation.Tally(observations)!.Count.Should().Be(2);
    }

    // 分類の単一情報源（RejectionReasonClassification）に委ねていること＝規則を再実装していないこと。
    // クラス C の限定列挙が変われば本集計も自動的に追随する。
    [Fact]
    public void クラス分けは単一情報源に委ねている()
    {
        foreach (var reason in Enum.GetValues<RejectionReason>())
        {
            var expected = RejectionReasonClassification.ClassOf(reason) == RejectionReasonClass.C;

            ControlViolationAggregation.CountsAsViolation(Simulate(reason)).Should().Be(
                expected, $"{reason} の計上可否は RejectionReasonClassification が決める");
        }
    }

    // 負の件数は観測結果として成立しない（供給側の異常を黙って通さない）。
    [Fact]
    public void 負の件数は受け付けない()
    {
        var act = () => ControlViolationTally.Observed(-1);

        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    // **プロパティベース**（入力によらず成り立つ不変条件）:
    // 1) 算入対象（moomoo SIMULATE）の観測が 1 件も無ければ必ず未供給（null）
    // 2) あれば、件数は「算入対象かつクラス C を含む観測の DecisionId の異なり数」と常に一致する
    // 乱数は固定シードで再現可能にする（失敗時に同じ入力を再現できないテストは直せない）。
    [Fact]
    public void 集計は算入対象のクラスC拒否の異なりDecisionId数と常に一致する()
    {
        var random = new Random(387);
        var reasons = Enum.GetValues<RejectionReason>();
        var providers = Enum.GetValues<BrokerProvider>();

        for (var iteration = 0; iteration < 200; iteration++)
        {
            // 重複 DecisionId も混ぜる（計上単位の不変条件を同時に検査する）。
            var ids = Enumerable.Range(0, 1 + random.Next(4)).Select(_ => Guid.NewGuid()).ToArray();
            var observations = Enumerable.Range(0, random.Next(10))
                .Select(_ => new OrderScreeningObservation(
                    ids[random.Next(ids.Length)],
                    providers[random.Next(providers.Length)],
                    Enumerable.Range(0, random.Next(3))
                        .Select(_ => reasons[random.Next(reasons.Length)])
                        .ToArray()))
                .ToList();

            var counted = observations.Where(o => o.Provider == BrokerProvider.MoomooSimulate).ToList();
            var expectedIds = counted
                .Where(o => o.RejectionReasons.Any(r =>
                    RejectionReasonClassification.ClassOf(r) == RejectionReasonClass.C))
                .Select(o => o.DecisionId)
                .Distinct()
                .Count();

            var tally = ControlViolationAggregation.Tally(observations);

            if (counted.Count == 0)
            {
                tally.Should().BeNull("算入対象の観測が無い＝未供給（iteration={0}）", iteration);
            }
            else
            {
                tally.Should().NotBeNull("iteration={0}", iteration);
                tally!.Count.Should().Be(expectedIds, "iteration={0}", iteration);
                tally.BlocksPromotion.Should().Be(expectedIds > 0, "iteration={0}", iteration);
            }
        }
    }

    // **プロパティベース**: 算入されるのは moomoo SIMULATE ちょうど 1 値に限る（許可制・IADR-0142 決定2）。
    // 発注先が増えたときに「黙って算入される」ことがないことを機械的に固定する。
    [Fact]
    public void 算入される発注先はmoomooSIMULATEだけである()
    {
        foreach (var provider in Enum.GetValues<BrokerProvider>())
        {
            var expected = provider == BrokerProvider.MoomooSimulate;
            var observation = new OrderScreeningObservation(
                Guid.NewGuid(), provider, [RejectionReason.BannedSymbol]);

            ControlViolationAggregation.IsCounted(provider).Should().Be(expected, "provider={0}", provider);
            ControlViolationAggregation.CountsAsViolation(observation).Should()
                .Be(expected, "provider={0}", provider);
            ControlViolationAggregation.Tally([observation]).Should().Be(
                expected ? ControlViolationTally.Observed(1) : null, "provider={0}", provider);
        }
    }
}

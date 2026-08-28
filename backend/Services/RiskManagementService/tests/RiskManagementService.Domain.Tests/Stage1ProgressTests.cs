using RiskManagementService.Domain;
using AiStockTrading.Shared.Contracts.Trading;
using AwesomeAssertions;
using Xunit;

namespace RiskManagementService.Domain.Tests;

// FR-20, #333, 06_daytrading-review §4.2 / §4.3, INDEX 決定 34・42, IADR-0137:
// Stage 1（SIMULATE）の期間カウント規則と、期間 × 件数の 2 条件・120 営業日打ち切りの判定を固定する。
//
// 3 点セット: 境界値（稼働率 50%・59/60/61 日・99/100/101 件・119/120/121 日）／
// プロパティベース（稼働率の単調性・件数条件が期間で緩まない）／
// **否定形**（期間だけで昇格しない・打ち切りを回避できない・時間外稼働で算入を買えない）。
public class Stage1ProgressTests
{
    private const int FullDay = Stage1DayQualification.RegularSessionMinutesFullDay;   // 390 分（6.5 時間）
    private const int HalfDay = Stage1DayQualification.RegularSessionMinutesHalfDay;   // 210 分（3.5 時間）

    private static readonly Stage1GateCriteria Criteria = Stage1GateCriteria.Default;

    // #334, IADR-0142: 観測は発注先を必須で伴う。本テスト群（#333 の期間カウント規則）は
    // **算入され得る発注先＝moomoo SIMULATE** を既定に置き、稼働率の規則だけを対象にする。
    // 発注先による除外そのものは Stage1AggregationTests が扱う。
    private static Stage1TradingDayObservation Day(
        int regularSessionMinutes,
        int operationalMinutes,
        int day = 1,
        BrokerProvider provider = BrokerProvider.MoomooSimulate) =>
        new(new DateOnly(2026, 8, day), regularSessionMinutes, operationalMinutes, provider);

    // ------------------------------------------------------------------
    // 1. 期間カウント規則（§4.2）: 閾値・分母・除外
    // ------------------------------------------------------------------

    // **境界値**: 「その日の実際の通常取引時間の 50% 以上が稼働していること。50% 未満の日は算入しない」。
    // **ちょうど 50% は算入する**（INDEX 決定 34 が「50% 以上」と明記）。
    [Theory]
    [InlineData(194, false)]  // 49.74%
    [InlineData(195, true)]   // ちょうど 50%（390 ÷ 2）
    [InlineData(196, true)]   // 50.26%
    public void 通常日は稼働率50パーセントちょうどから算入する(int operationalMinutes, bool expected)
    {
        Stage1DayQualification.Qualifies(Day(FullDay, operationalMinutes)).Should().Be(expected);
    }

    // **境界値**: 分母は「その日の実際の通常取引時間」であり、**固定の 6.5 時間を用いない**（§4.2）。
    // 半日取引日（3.5 時間＝210 分）では 105 分の稼働で算入される。固定 390 分を分母にすると
    // 105 ÷ 390 ＝ 26.9% となり、**半日取引日はほぼ永久に算入されなくなる**。
    [Theory]
    [InlineData(104, false)]  // 49.52%
    [InlineData(105, true)]   // ちょうど 50%（210 ÷ 2）
    [InlineData(106, true)]   // 50.48%
    public void 半日取引日は分母が3時間半になる(int operationalMinutes, bool expected)
    {
        Stage1DayQualification.Qualifies(Day(HalfDay, operationalMinutes)).Should().Be(expected);

        // 固定 6.5 時間を分母にしていたら、105 分は算入されない（退行の検知）。
        if (expected)
        {
            Stage1DayQualification.Qualifies(Day(FullDay, operationalMinutes))
                .Should().BeFalse("固定 390 分を分母にすると半日取引日が算入されなくなる");
        }
    }

    // **否定形**（§4.2 の「除外」）: 市場休場日（通常取引時間 0）は算入しない。
    // 稼働分数が正でも算入させない——休場日に OpenD が動いていたことは「取引できた日」ではない。
    [Theory]
    [InlineData(0)]
    [InlineData(390)]
    public void 市場休場日は稼働していても算入しない(int operationalMinutes)
    {
        var holiday = Day(regularSessionMinutes: 0, operationalMinutes);

        Stage1DayQualification.UptimeRatio(holiday).Should().Be(0m);
        Stage1DayQualification.Qualifies(holiday).Should().BeFalse();
    }

    // **否定形**（§4.2）: 時間外（プレ／アフターマーケット）の稼働で算入を買えない。
    // 稼働分数が通常取引時間を超える入力は 1.0 に丸め、通常取引時間内が空でも算入されるような
    // 計算にはしない——分子だけが伸びる形の入力で 50% 判定をすり抜けさせないためである。
    [Fact]
    public void 稼働分数が通常取引時間を超えても稼働率は1を超えない()
    {
        Stage1DayQualification.UptimeRatio(Day(FullDay, operationalMinutes: 960)).Should().Be(1m);
    }

    // 異常入力（負の稼働分数）は 0 として扱い、算入しない（fail-safe）。
    [Fact]
    public void 負の稼働分数は0として扱う()
    {
        Stage1DayQualification.UptimeRatio(Day(FullDay, operationalMinutes: -60)).Should().Be(0m);
        Stage1DayQualification.Qualifies(Day(FullDay, operationalMinutes: -60)).Should().BeFalse();
    }

    // **プロパティベース**: 稼働分数が増えても算入の可否は反転しない（単調性）。
    // 「稼働が増えたのに算入されなくなる」ような不連続点が無いことを固定する。
    [Fact]
    public void 稼働分数の増加で算入の可否は反転しない()
    {
        var qualifiedSeen = false;
        for (var minutes = 0; minutes <= FullDay; minutes++)
        {
            var qualifies = Stage1DayQualification.Qualifies(Day(FullDay, minutes));
            if (qualifies)
            {
                qualifiedSeen = true;
            }
            else
            {
                qualifiedSeen.Should().BeFalse(
                    "一度算入された稼働水準より多く稼働した日が算入されないことはない（分 {0}）", minutes);
            }
        }

        qualifiedSeen.Should().BeTrue();
    }

    // §4.2: DST の切替・半日取引日は「その日の実際の通常取引時間」が観測値であることで吸収される。
    // 実装はタイムゾーン変換を行わず、米国東部時間での取引日と実際の通常取引時間をそのまま受ける。
    [Fact]
    public void 混在した営業日の並びから算入日数を数える()
    {
        Stage1TradingDayObservation[] observations =
        [
            Day(FullDay, 390, day: 3),   // 通常日・全時間稼働 → 算入
            Day(FullDay, 195, day: 4),   // 通常日・ちょうど 50% → 算入
            Day(FullDay, 100, day: 5),   // 通常日・25.6%（OpenD 停止） → 算入しない
            Day(HalfDay, 105, day: 6),   // 半日取引日・ちょうど 50% → 算入
            Day(0, 0, day: 7),           // 市場休場 → 算入しない
        ];

        Stage1DayQualification.CountQualifiedDays(observations).Should().Be(3);
    }

    // ------------------------------------------------------------------
    // 2. 期間 × 件数の 2 条件と 120 営業日打ち切り（§4.3・INDEX 決定 42）
    // ------------------------------------------------------------------

    // **境界値**: 目標営業日数 60 と最小件数 100 は**いずれもちょうどで充足**する。
    [Theory]
    [InlineData(59, 100, Stage1GateOutcome.InProgress)]
    [InlineData(60, 100, Stage1GateOutcome.Promotable)]
    [InlineData(61, 101, Stage1GateOutcome.Promotable)]
    [InlineData(60, 99, Stage1GateOutcome.Extended)]
    [InlineData(60, 101, Stage1GateOutcome.Promotable)]
    public void 期間と件数の境界で状態が決まる(int days, int trades, Stage1GateOutcome expected)
    {
        Stage1Gate.Evaluate(new Stage1Progress(days, trades), Criteria).Should().Be(expected);
    }

    // **境界値**（§4.3）: 累計 120 営業日で打ち切る。119 日は延長中、120 日で打ち切り。
    [Theory]
    [InlineData(119, Stage1GateOutcome.Extended)]
    [InlineData(120, Stage1GateOutcome.Exhausted)]
    [InlineData(121, Stage1GateOutcome.Exhausted)]
    public void 累計120営業日で打ち切る(int days, Stage1GateOutcome expected)
    {
        Stage1Gate.Evaluate(new Stage1Progress(days, TradeCount: 99), Criteria).Should().Be(expected);
    }

    // **否定形**（§4.3 の中心）: 期間（60 営業日）を満たしても、件数（100 件）に届かなければ昇格しない。
    // 「期間だけ満たして昇格する」案は計画が明示的に採らないと述べたものである。
    [Theory]
    [InlineData(60)]
    [InlineData(90)]
    [InlineData(119)]
    public void 期間だけを満たしても昇格可能にはならない(int days)
    {
        for (var trades = 0; trades < Criteria.MinimumTradeCount; trades += 25)
        {
            Stage1Gate.Evaluate(new Stage1Progress(days, trades), Criteria)
                .Should().NotBe(
                    Stage1GateOutcome.Promotable,
                    "期間と件数の両方を満たすまで昇格しない（件数 {0}）", trades);
        }
    }

    // **否定形**（§4.3）: 延長は無制限ではない。120 営業日を超えて件数不足が続く限り、
    // 状態が `Extended` へ戻ることはない（打ち切りを「待てば解ける」ものにしない）。
    [Fact]
    public void 打ち切り後は待っても延長状態へ戻らない()
    {
        for (var days = Criteria.MaximumTradingDays; days <= Criteria.MaximumTradingDays + 60; days++)
        {
            Stage1Gate.Evaluate(new Stage1Progress(days, TradeCount: 99), Criteria)
                .Should().Be(Stage1GateOutcome.Exhausted, "営業日 {0}", days);
        }
    }

    // **否定形**（§4.3 の表の読み違い防止）: 打ち切り事由は「120 営業日を経ても**件数に届かない**」ことであり、
    // 期間の超過そのものではない。件数を満たしていれば 120 営業日を超えても昇格できる。
    [Theory]
    [InlineData(120)]
    [InlineData(200)]
    public void 件数を満たしていれば120営業日を超えても昇格可能である(int days)
    {
        Stage1Gate.Evaluate(new Stage1Progress(days, Criteria.MinimumTradeCount), Criteria)
            .Should().Be(Stage1GateOutcome.Promotable);
    }

    // **プロパティベース**: 件数を固定したまま営業日数を増やしても、件数の条件は緩まない。
    // 「待てば件数の基準が下がる」経路が無いことを固定する（§4.3「件数の引き下げは行わない」）。
    [Fact]
    public void 営業日数を増やしても件数の条件は緩まない()
    {
        for (var days = 0; days <= Criteria.MaximumTradingDays + 30; days++)
        {
            Stage1Gate.Evaluate(new Stage1Progress(days, Criteria.MinimumTradeCount - 1), Criteria)
                .Should().NotBe(Stage1GateOutcome.Promotable, "営業日 {0}", days);
        }
    }

    // 計画の確定値（§4.1〜§4.3・INDEX 決定 34・42）を固定する退行防止テスト。
    [Fact]
    public void 合格条件の閾値は計画の確定値である()
    {
        Criteria.TargetTradingDays.Should().Be(60);     // §4.2「目標日数 60 営業日」
        Criteria.MinimumTradeCount.Should().Be(100);    // §4.1 条件 3「最小取引件数 100 件」
        Criteria.MaximumTradingDays.Should().Be(120);   // §4.3「累計 120 営業日で打ち切り」
        Stage1DayQualification.MinimumUptimeRatio.Should().Be(0.50m); // §4.2「50% 以上」
        Stage1DayQualification.RegularSessionMinutesFullDay.Should().Be(390); // 6.5 時間
        Stage1DayQualification.RegularSessionMinutesHalfDay.Should().Be(210); // 3.5 時間
    }
}

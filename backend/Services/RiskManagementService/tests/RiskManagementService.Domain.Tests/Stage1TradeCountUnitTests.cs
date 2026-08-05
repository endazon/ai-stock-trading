using AiStockTrading.RiskManagement.Domain;
using AiStockTrading.Shared.Contracts.Trading;
using AwesomeAssertions;
using Xunit;

namespace AiStockTrading.RiskManagement.Domain.Tests;

// FR-20, FR-12, #386, 06_daytrading-review §4.1 条件3 / §4.3, IADR-0149:
// Stage 1 の合格条件「最小取引件数 100 件」の**計上単位**を固定する。
//
// 計画は 100 件の単位を定義していない。実装は次を前提として採り、根拠を IADR-0149 決定2 に、
// 環流を feedback/20260805_fr20-stage1-trade-count-unit.md に残した。
//
//   1 件 ＝ 算入対象の発注先で約定が成立した**新規建て注文 1 件**（DecisionId で一意）
//
// **本テスト群の主眼は「膨らまないこと」である。** 件数が膨らむ側は「合格に早く届く」＝緩い側であり、
// しかも成功したように見える。分割約定・イベント再送・手仕舞いの 3 つが膨張の経路である。
public class Stage1TradeCountUnitTests
{
    private static Stage1FillObservation Fill(
        Guid decisionId,
        BrokerProvider provider = BrokerProvider.MoomooSimulate,
        PositionEffect effect = PositionEffect.Open) =>
        new(decisionId, new DateOnly(2026, 8, 5), provider, effect);

    // ------------------------------------------------------------------
    // 正: SIMULATE の新規建て約定は 1 件として数える
    // ------------------------------------------------------------------
    [Fact]
    public void SIMULATEの新規建て約定は1件として数える()
    {
        Stage1Aggregation.CountTrades([Fill(Guid.NewGuid()), Fill(Guid.NewGuid())]).Should().Be(2);
    }

    // ------------------------------------------------------------------
    // 否定形（膨張の経路 1）: 分割約定・再送で同じ注文が複数回観測されても 1 件
    // ------------------------------------------------------------------

    // OrderExecuted の FilledQuantity はブローカの**累積**値であり、moomoo は同一注文について
    // Accepted(0) → 部分約定 → 全量約定と複数回発行する（IADR-0113）。イベントを数えると 1 注文が 2〜3 件になる。
    [Fact]
    public void 同一注文の分割約定と再送は1件にしか数えない()
    {
        var decisionId = Guid.NewGuid();

        Stage1Aggregation.CountTrades([Fill(decisionId), Fill(decisionId), Fill(decisionId)])
            .Should().Be(1, "分割約定・再送は同じ 1 注文である（DecisionId で畳む）");
    }

    // ------------------------------------------------------------------
    // 否定形（膨張の経路 2）: 手仕舞い（Close）は数えない
    // ------------------------------------------------------------------

    // 計画が 100 件を比較した出典（個人デイトレーダーは 1 日 3〜5 件）と 05_trading-assumptions §5 の
    // 「1 注文上限いっぱいでも 6 件（＝2 回転）」は、いずれも**新規建ての件数**を数えている
    // （1 日あたりの発注金額上限は手仕舞いを算入しない）。手仕舞いを別件として数えると単位が食い違い、
    // 100 件に到達する速さが約 2 倍になる＝緩い側へ倒れる。
    [Fact]
    public void 手仕舞いの約定は取引件数に数えない()
    {
        Stage1Aggregation.CountsAsTrade(Fill(Guid.NewGuid(), effect: PositionEffect.Close))
            .Should().BeFalse();

        Stage1Aggregation.CountTrades([
            Fill(Guid.NewGuid(), effect: PositionEffect.Open),
            Fill(Guid.NewGuid(), effect: PositionEffect.Close),
            Fill(Guid.NewGuid(), effect: PositionEffect.Close),
        ]).Should().Be(1, "新規建て 1 件だけを数える");
    }

    // ------------------------------------------------------------------
    // 否定形（膨張の経路 3）: 発注先の混入
    // ------------------------------------------------------------------

    // **本 issue の最重要の否定形。** 内蔵 paper の擬似約定を混ぜても件数が汚染されない。
    [Fact]
    public void 内蔵paperの新規建て約定を混ぜても件数は汚染されない()
    {
        var fills = new List<Stage1FillObservation>();
        for (var i = 0; i < 100; i++)
        {
            fills.Add(Fill(Guid.NewGuid(), BrokerProvider.InternalPaper));
        }

        fills.Add(Fill(Guid.NewGuid(), BrokerProvider.MoomooSimulate));

        Stage1Aggregation.CountTrades(fills).Should().Be(
            1, "内蔵 paper の擬似約定は 1 件も算入されない（FR-20・IADR-0142 決定2）");
    }

    // 許可制（IADR-0142 決定2）: 名指しで許可された発注先だけを数える。新しい発注先が増えても既定では数えない。
    [Fact]
    public void 新規建てでも算入される発注先はmoomooSIMULATEだけである()
    {
        foreach (var provider in Enum.GetValues<BrokerProvider>())
        {
            var expected = provider == BrokerProvider.MoomooSimulate;
            Stage1Aggregation.CountsAsTrade(Fill(Guid.NewGuid(), provider))
                .Should().Be(expected, "provider={0}", provider);
        }
    }

    // ------------------------------------------------------------------
    // 否定形: 供給が途絶えても水増しされない
    // ------------------------------------------------------------------

    // 観測が 1 件も無ければ 0 件である。**0 は「条件未充足＝昇格しない」に倒れる fail-safe** であり、
    // 統制違反件数（#387・IADR-0148）と違って「未供給」を別状態として持つ必要が無い。
    [Fact]
    public void 観測が無ければ0件であり昇格しない()
    {
        Stage1Aggregation.CountTrades([]).Should().Be(0);

        Stage1Gate.Evaluate(new Stage1Progress(60, 0), Stage1GateCriteria.Default)
            .Should().Be(Stage1GateOutcome.Extended, "件数が 0 なら期間を満たしていても昇格しない");
    }

    // 対照（肯定形）: 同じ量を SIMULATE の新規建てで積めば 100 件に到達し、昇格可能になる。
    // 否定形だけでは「常に 0 を返す」実装でも緑になるため、対照を置く。
    [Fact]
    public void SIMULATEの新規建てを100件積めば昇格可能になる()
    {
        var fills = Enumerable.Range(0, 100).Select(_ => Fill(Guid.NewGuid())).ToArray();

        var count = Stage1Aggregation.CountTrades(fills);

        count.Should().Be(100);
        Stage1Gate.Evaluate(new Stage1Progress(60, count), Stage1GateCriteria.Default)
            .Should().Be(Stage1GateOutcome.Promotable);
    }
}

using AiStockTrading.Shared.Contracts.Backtest;
using AiStockTrading.Shared.Contracts.Trading;
using AiStockTrading.Shared.Kernel.Trading;
using AwesomeAssertions;
using BacktestService.Domain;
using Xunit;

namespace BacktestService.Tests.Domain;

// FR-04, FR-15, FR-20, ADR-0008, ADR-0033 決定2, #632, IADR-0318: 記録再生戦略の検証。
//
// 見るのは 3 点である。
//   1. **純関数である**（同一入力→同一出力。IADR-0043 の契約を覆さない）
//   2. **否定形**: 記録集合の期間外では 1 件も発注しない
//   3. **否定形**: 記録の無い日・見送り（Hold）の日は無発注である
public class RecordedDecisionReplayStrategyTests
{
    private static readonly DateOnly From = new(2026, 6, 1);
    private static readonly DateOnly To = new(2026, 6, 30);

    private static Stage0DecisionRecord Record(DateOnly asOf, string symbol, int signedQuantity) =>
        new(symbol, Market.UnitedStates, asOf, "fp", "claude-sonnet-5", VoteCount: 3,
            RawDecisions: [new Stage0RawDecision(1, Stage0DecisionAction.Buy, "根拠", 100m, 2m, 100, 20, false)],
            MajorityAction: signedQuantity switch
            {
                > 0 => Stage0DecisionAction.Buy,
                < 0 => Stage0DecisionAction.Sell,
                _ => Stage0DecisionAction.Hold,
            },
            MajorityRationale: "根拠", SignedQuantity: signedQuantity,
            CostJpy: 1m, InputTokens: 300, OutputTokens: 60);

    private static Stage0DecisionRecordSet SetOf(params Stage0DecisionRecord[] records) =>
        new(From, To, [new Stage0RecordedSymbol("AAPL", Market.UnitedStates)],
            new DateOnly(2026, 3, 31), DateTimeOffset.UnixEpoch, "claude-sonnet-5", "strategy-id", records);

    private static BacktestContext Context(DateOnly asOf) =>
        new(asOf, [], new Dictionary<(string Symbol, Market Market), InventoryLot>(), 1_000_000m);

    // 肯定形: 記録した判断が注文へ写る（符号付き数量をそのまま使う＝サイジングを再計算しない）。
    [Fact]
    public void 記録した判断を注文へ写す()
    {
        var strategy = new RecordedDecisionReplayStrategy(SetOf(
            Record(new DateOnly(2026, 6, 2), "AAPL", 10),
            Record(new DateOnly(2026, 6, 3), "AAPL", -4)));

        strategy.DecideOrders(Context(new DateOnly(2026, 6, 2)))
            .Should().ContainSingle().Which.Should().Be(new BacktestOrder("AAPL", Market.UnitedStates, 10));
        strategy.DecideOrders(Context(new DateOnly(2026, 6, 3)))
            .Should().ContainSingle().Which.Should().Be(new BacktestOrder("AAPL", Market.UnitedStates, -4));
    }

    // プロパティベース: 同じ文脈を何度渡しても同じ注文が返る（純関数＝IADR-0043 の契約）。
    // ウォークフォワード・コスト 2 倍感度・DSR/PBO は同じ戦略を何度も走らせるため、これが崩れると
    // 統計手続きそのものが意味を失う。
    [Fact]
    public void 同一入力に対して常に同一の出力を返す()
    {
        var strategy = new RecordedDecisionReplayStrategy(SetOf(
            Record(new DateOnly(2026, 6, 2), "AAPL", 10),
            Record(new DateOnly(2026, 6, 5), "AAPL", -3)));

        foreach (var day in Enumerable.Range(0, 30).Select(From.AddDays))
        {
            var first = strategy.DecideOrders(Context(day));
            var second = strategy.DecideOrders(Context(day));
            second.Should().Equal(first);
        }
    }

    // 🔴 **否定形（最重要）**: 記録集合の期間外では 1 件も発注しない。
    // ウォークフォワードの窓や感度分析で記録の無い期間のバーが渡ることがあり、そこで発注すれば
    // 「記録していない判断」が成績に混ざる。
    [Theory]
    [InlineData("2026-05-31")] // 始端の 1 日前
    [InlineData("2026-05-01")]
    [InlineData("2026-07-01")] // 終端の 1 日後
    [InlineData("2026-12-31")]
    public void 記録集合の期間外では発注しない(string asOf)
    {
        // 期間外に判断の記録があっても（記録の取り違え）、期間の検査が先に効く。
        var strategy = new RecordedDecisionReplayStrategy(SetOf(
            Record(DateOnly.Parse(asOf), "AAPL", 10),
            Record(new DateOnly(2026, 6, 2), "AAPL", 10)));

        strategy.DecideOrders(Context(DateOnly.Parse(asOf))).Should().BeEmpty();
    }

    // 境界値: 期間の両端は「内」である（両端含む）。
    [Theory]
    [InlineData("2026-06-01")]
    [InlineData("2026-06-30")]
    public void 期間の両端は記録が引かれる(string asOf)
    {
        var strategy = new RecordedDecisionReplayStrategy(SetOf(Record(DateOnly.Parse(asOf), "AAPL", 7)));

        strategy.DecideOrders(Context(DateOnly.Parse(asOf))).Should().ContainSingle();
    }

    // **否定形**: 記録の無い日は無発注（判断していない日に注文を発明しない）。
    [Fact]
    public void 記録の無い日は無発注である()
    {
        var strategy = new RecordedDecisionReplayStrategy(SetOf(Record(new DateOnly(2026, 6, 2), "AAPL", 10)));

        strategy.DecideOrders(Context(new DateOnly(2026, 6, 3))).Should().BeEmpty();
    }

    // **否定形**: 見送り（数量 0）は注文を作らない（無発注と「0 株の注文」を区別しない）。
    [Fact]
    public void 見送りの記録は注文を作らない()
    {
        var strategy = new RecordedDecisionReplayStrategy(SetOf(Record(new DateOnly(2026, 6, 2), "AAPL", 0)));

        strategy.DecideOrders(Context(new DateOnly(2026, 6, 2))).Should().BeEmpty();
    }

    // 記録が空でも壊れない（供給ポートが空の記録集合を返した場合。判定側は別途 fail-closed で弾く）。
    [Fact]
    public void 記録が空なら常に無発注である()
    {
        var strategy = new RecordedDecisionReplayStrategy(SetOf());

        strategy.DecideOrders(Context(new DateOnly(2026, 6, 2))).Should().BeEmpty();
    }

    // 同一 (銘柄, 市場, AsOf) の重複は後勝ちで畳む（BacktestSimulator の重複規則と揃える）。
    [Fact]
    public void 同一銘柄同一日の重複記録は1件に畳まれる()
    {
        var strategy = new RecordedDecisionReplayStrategy(SetOf(
            Record(new DateOnly(2026, 6, 2), "AAPL", 10),
            Record(new DateOnly(2026, 6, 2), "AAPL", 3)));

        strategy.DecideOrders(Context(new DateOnly(2026, 6, 2)))
            .Should().ContainSingle().Which.SignedQuantity.Should().Be(3);
    }

    // 戦略 ID は記録集合の値をそのまま名乗る（IADR-0281 決定3 の「戦略の変更」を機械判定する鍵）。
    [Fact]
    public void 戦略IDは記録集合の値を名乗る() =>
        new RecordedDecisionReplayStrategy(SetOf()).StrategyId.Should().Be("strategy-id");
}

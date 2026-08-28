using TradeDecisionService.Domain;
using AwesomeAssertions;
using Xunit;

namespace TradeDecisionService.Domain.Tests;

// FR-04, FR-11, ADR-0003, IADR-0039: 多数決の集約（純関数）の検証。
// 最多得票の action を採り、タイ・空は安全側 Hold。数値は勝利票の参照価格中央値を持つ代表票を一体採用。
public class DecisionAggregatorTests
{
    private static LlmDecision Buy(decimal price = 1_000m, decimal stop = 30m, string why = "買い") =>
        new(TradeAction.Buy, why, price, stop);

    private static LlmDecision Sell(decimal price = 1_000m, decimal stop = 30m, string why = "売り") =>
        new(TradeAction.Sell, why, price, stop);

    private static LlmDecision Hold(string why = "見送り") => new(TradeAction.Hold, why, 0m, 0m);

    [Fact]
    public void 最多得票のBuyを採る()
    {
        var result = DecisionAggregator.Aggregate([Buy(), Buy(), Sell()]);

        result.Decision.Action.Should().Be(TradeAction.Buy);
        result.TotalVotes.Should().Be(3);
        result.AgreementVotes.Should().Be(2);
    }

    [Fact]
    public void 最多得票のSellを採る()
    {
        DecisionAggregator.Aggregate([Sell(), Sell(), Buy()]).Decision.Action.Should().Be(TradeAction.Sell);
    }

    [Fact]
    public void 最多得票がHoldなら見送り()
    {
        DecisionAggregator.Aggregate([Buy(), Hold(), Hold()]).Decision.Action.Should().Be(TradeAction.Hold);
    }

    [Fact]
    public void 同数タイは安全側Holdに倒す_二者()
    {
        // ADR-0003: 不確実なら取引しない。Buy と Sell が同数 → Hold。
        DecisionAggregator.Aggregate([Buy(), Sell()]).Decision.Action.Should().Be(TradeAction.Hold);
    }

    [Fact]
    public void 同数タイは安全側Holdに倒す_首位が並ぶ()
    {
        // Buy 2・Sell 2 → 首位タイ → Hold。
        var result = DecisionAggregator.Aggregate([Buy(), Buy(), Sell(), Sell()]);

        result.Decision.Action.Should().Be(TradeAction.Hold);
    }

    [Fact]
    public void 空入力はHold()
    {
        var result = DecisionAggregator.Aggregate([]);

        result.Decision.Action.Should().Be(TradeAction.Hold);
        result.TotalVotes.Should().Be(0);
        result.AgreementVotes.Should().Be(0);
    }

    [Fact]
    public void 単一票はそのまま採用()
    {
        var only = Buy(1_200m, 40m, "唯一");

        var result = DecisionAggregator.Aggregate([only]);

        result.Decision.Should().Be(only);
        result.AgreementVotes.Should().Be(1);
    }

    [Fact]
    public void 勝利票の参照価格中央値を持つ代表票を一体採用する()
    {
        // IADR-0039: 合成値を作らず、勝利票の中で参照価格が中央（下側中央値）の 1 票を丸ごと採る。
        // 勝利=Buy の参照価格 {900, 1000, 1100} → 中央 1000 の票（stop=25・rationale="中央"）を採用。
        var votes = new[]
        {
            Buy(1_100m, 40m, "高値"),
            Buy(900m, 10m, "安値"),
            Buy(1_000m, 25m, "中央"),
            Sell(1_000m, 30m, "少数派"),
        };

        var result = DecisionAggregator.Aggregate(votes);

        result.Decision.Action.Should().Be(TradeAction.Buy);
        result.Decision.ReferencePrice.Should().Be(1_000m);
        result.Decision.StopLossDistancePerShare.Should().Be(25m);
        result.Decision.Rationale.Should().Be("中央");
        result.AgreementVotes.Should().Be(3);
    }

    [Fact]
    public void 勝利票が偶数個なら下側中央値の票を採る()
    {
        // 参照価格 {900, 1100} の 2 票 → 下側中央値 = 900 の票を採用（決定的）。
        var result = DecisionAggregator.Aggregate([Buy(1_100m, 40m, "高"), Buy(900m, 10m, "低")]);

        result.Decision.ReferencePrice.Should().Be(900m);
        result.Decision.Rationale.Should().Be("低");
    }

    // #247, IADR-0104 決定6: Hold 勝利時も「実在する 1 票を代表として一体で採る」規則を適用し根拠を保つ。
    // Hold は TradeDecisionMade を発行しないため、DecideAsync の FR-11 ログ 1 行が唯一の監査記録であり、
    // ここで根拠を捨てると LLM 由来の見送り理由（拒否等）が監査へ届かない。
    [Fact]
    public void Hold勝利時は代表票の根拠を保つ()
    {
        var result = DecisionAggregator.Aggregate([Hold("LLM が要求を拒否したため見送り")]);

        result.Decision.Action.Should().Be(TradeAction.Hold);
        result.Decision.Rationale.Should().Be("LLM が要求を拒否したため見送り");
        result.AgreementVotes.Should().Be(1);
    }

    [Fact]
    public void Hold勝利の代表票は決定的に選ばれる()
    {
        // 根拠の序数順（価格・損切り幅は Hold では 0）で下側中央値の票を採る＝入力順に依存しない。
        var forward = DecisionAggregator.Aggregate([Hold("A"), Hold("B"), Hold("C"), Buy()]);
        var reversed = DecisionAggregator.Aggregate([Buy(), Hold("C"), Hold("B"), Hold("A")]);

        forward.Decision.Rationale.Should().Be("B");
        reversed.Decision.Rationale.Should().Be("B");
    }

    [Fact]
    public void 首位タイと空入力は実在票の根拠を騙らず安全既定のHoldを返す()
    {
        // どの票も勝っていないため、特定の票の根拠を代表として採らない（定数 Hold のまま）。
        DecisionAggregator.Aggregate([Buy(), Sell()]).Decision.Should().Be(LlmDecision.Hold);
        DecisionAggregator.Aggregate([]).Decision.Should().Be(LlmDecision.Hold);
    }

    [Fact]
    public void 少数派の数値は集約に影響しない()
    {
        // 勝利=Buy の数値のみで代表票を選ぶ。負けた Sell 票の価格は無視される。
        var result = DecisionAggregator.Aggregate([Buy(1_000m, 30m, "本命"), Buy(1_000m, 30m, "本命2"), Sell(5_000m, 99m, "外れ値")]);

        result.Decision.Action.Should().Be(TradeAction.Buy);
        result.Decision.ReferencePrice.Should().Be(1_000m);
    }
}

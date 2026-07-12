using AiStockTrading.TradeDecision.Domain;
using FluentAssertions;
using Xunit;

namespace AiStockTrading.TradeDecision.Domain.Tests;

// FR-04, FR-11: LLM 構造化出力の解析（不正は Hold へ倒す安全側）の検証。
public class TradeDecisionParserTests
{
    [Fact]
    public void 正常なBuyのJSONを解析する()
    {
        var json = """{"action":"Buy","rationale":"上昇トレンド","referencePrice":1000,"stopLossDistancePerShare":30}""";

        var d = TradeDecisionParser.Parse(json);

        d.Action.Should().Be(TradeAction.Buy);
        d.Rationale.Should().Be("上昇トレンド");
        d.ReferencePrice.Should().Be(1000m);
        d.StopLossDistancePerShare.Should().Be(30m);
    }

    [Fact]
    public void 前後に散文を含むJSONからオブジェクトを抽出する()
    {
        var text = "判断します。\n```json\n{\"action\":\"Sell\",\"rationale\":\"反落\",\"referencePrice\":900,\"stopLossDistancePerShare\":20}\n```\n以上。";

        var d = TradeDecisionParser.Parse(text);

        d.Action.Should().Be(TradeAction.Sell);
        d.ReferencePrice.Should().Be(900m);
    }

    [Fact]
    public void JSON後の散文に閉じ括弧が含まれても正しく抽出する()
    {
        // 括弧深さで対応括弧を探すため、末尾の散文 "(参考: {1})" に壊されない。
        var text = """{"action":"Buy","rationale":"上昇","referencePrice":1000,"stopLossDistancePerShare":30} 以上です (参考: {1})""";

        var d = TradeDecisionParser.Parse(text);

        d.Action.Should().Be(TradeAction.Buy);
        d.ReferencePrice.Should().Be(1000m);
    }

    [Fact]
    public void Holdは見送りとして解析する()
    {
        TradeDecisionParser.Parse("""{"action":"Hold","rationale":"様子見"}""").Action.Should().Be(TradeAction.Hold);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not json at all")]
    [InlineData("""{"action":"Explode","rationale":"x","referencePrice":1,"stopLossDistancePerShare":1}""")]
    [InlineData("""{"broken json""")]
    public void 不正または未知の出力はHoldに倒す(string output)
    {
        TradeDecisionParser.Parse(output).Action.Should().Be(TradeAction.Hold);
    }

    [Theory]
    [InlineData(0, 30)]
    [InlineData(1000, 0)]
    [InlineData(-1, 30)]
    [InlineData(1000, 1000)] // IADR-0035: 損切り幅 = 参照価格 → 損切り価格 0（ロング監視不能）
    [InlineData(1000, 1500)] // IADR-0035: 損切り幅 > 参照価格 → 損切り価格 負（幻覚）
    public void Buyで価格または損切り幅が正でなければHoldに倒す(int price, int stop)
    {
        var json = $$"""{"action":"Buy","rationale":"x","referencePrice":{{price}},"stopLossDistancePerShare":{{stop}}}""";

        TradeDecisionParser.Parse(json).Action.Should().Be(TradeAction.Hold);
    }
}

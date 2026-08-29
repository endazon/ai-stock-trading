using TradeDecisionService.Domain;
using AwesomeAssertions;
using Xunit;

namespace TradeDecisionService.Tests;

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

    // FR-17, IADR-0076: 想定利益（採算評価の入力）を解析する。
    [Fact]
    public void 想定利益を解析する()
    {
        var json = """{"action":"Buy","rationale":"x","referencePrice":1000,"stopLossDistancePerShare":30,"expectedProfitPerShare":45}""";

        TradeDecisionParser.Parse(json).ExpectedProfitPerShare.Should().Be(45m);
    }

    // FR-17, IADR-0076: 想定利益の欠損・負値は 0 に正規化する（保守側＝採算ゲート有効時は Hold に倒れる）。
    [Theory]
    [InlineData("""{"action":"Buy","rationale":"x","referencePrice":1000,"stopLossDistancePerShare":30}""")]
    [InlineData("""{"action":"Buy","rationale":"x","referencePrice":1000,"stopLossDistancePerShare":30,"expectedProfitPerShare":-5}""")]
    public void 想定利益の欠損または負値は0に正規化する(string json)
    {
        TradeDecisionParser.Parse(json).ExpectedProfitPerShare.Should().Be(0m);
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

    // --- #337（#290 吸収）, IADR-0248: 解析不能と見送りの区別 ---

    [Theory]
    [InlineData("", TradeDecisionParseFailureKind.EmptyOutput)]
    [InlineData("   ", TradeDecisionParseFailureKind.EmptyOutput)]
    [InlineData("not json at all", TradeDecisionParseFailureKind.NoJsonObject)]
    [InlineData("""{"broken json""", TradeDecisionParseFailureKind.NoJsonObject)]
    [InlineData("""{"action": }""", TradeDecisionParseFailureKind.MalformedJson)]
    [InlineData("""{"rationale":"actionなし","referencePrice":1,"stopLossDistancePerShare":1}""", TradeDecisionParseFailureKind.UnknownAction)]
    [InlineData("""{"action":"Explode","rationale":"x","referencePrice":1,"stopLossDistancePerShare":1}""", TradeDecisionParseFailureKind.UnknownAction)]
    [InlineData("""{"action":"Buy","rationale":"x","referencePrice":0,"stopLossDistancePerShare":30}""", TradeDecisionParseFailureKind.InvalidValues)]
    [InlineData("""{"action":"Buy","rationale":"x","referencePrice":1000,"stopLossDistancePerShare":1000}""", TradeDecisionParseFailureKind.InvalidValues)]
    public void 解析不能は失敗種別つきでHoldに倒す(string output, TradeDecisionParseFailureKind expected)
    {
        var parsed = TradeDecisionParser.ParseDetailed(output);

        parsed.IsUnparseable.Should().BeTrue("解析不能は見送りと区別して記録する（#290）");
        parsed.Failure!.Kind.Should().Be(expected);
        // 安全既定は不変: 挙動は Hold（取引しない）のまま。
        parsed.Decision.Action.Should().Be(TradeAction.Hold);
    }

    [Fact]
    public void 解析できた見送りは解析不能ではない_対の肯定形()
    {
        var parsed = TradeDecisionParser.ParseDetailed(
            """{"action":"Hold","rationale":"方針外のため見送る"}""");

        parsed.IsUnparseable.Should().BeFalse("LLM が選んだ見送りは解析不能ではない");
        parsed.Failure.Should().BeNull();
        parsed.Decision.Action.Should().Be(TradeAction.Hold);
        parsed.Decision.Rationale.Should().Be("方針外のため見送る");
    }

    [Fact]
    public void 解析できたBuyは解析不能ではない_対の肯定形()
    {
        var parsed = TradeDecisionParser.ParseDetailed(
            """{"action":"Buy","rationale":"押し目","referencePrice":1000,"stopLossDistancePerShare":30}""");

        parsed.IsUnparseable.Should().BeFalse();
        parsed.Decision.Action.Should().Be(TradeAction.Buy);
    }

    [Fact]
    public void 互換APIのParseはParseDetailedのDecisionと一致する()
    {
        // Parse は ParseDetailed の互換ラッパ。区別の導入で既存経路の挙動（Hold へ倒す）を変えない。
        foreach (var output in new[]
                 {
                     "", "not json",
                     """{"action":"Buy","rationale":"x","referencePrice":1000,"stopLossDistancePerShare":30}""",
                     """{"action":"Hold","rationale":"見送り"}""",
                 })
        {
            TradeDecisionParser.Parse(output).Should().Be(TradeDecisionParser.ParseDetailed(output).Decision);
        }
    }
}

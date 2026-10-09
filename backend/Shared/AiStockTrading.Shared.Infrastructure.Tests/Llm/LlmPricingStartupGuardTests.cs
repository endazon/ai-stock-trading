using AiStockTrading.Shared.Infrastructure.Composable.Llm;
using AwesomeAssertions;
using Xunit;

namespace AiStockTrading.Shared.Infrastructure.Tests.Llm;

// NFR-13, FR-04, IADR-0499, #1197: LLM ゲートウェイが構成されているのに単価が実質 0 なら、配備（Production）では起動しない。
// 配備でない環境は警告に留める（#817）。ゲートウェイが無い・単価がある構成は何もしない（本番既定 values.yaml は前者）。
public class LlmPricingStartupGuardTests
{
    private static readonly LlmPriceTable Empty = LlmPriceTable.From([]);

    private static readonly LlmPriceTable PerModel =
        LlmPriceTable.From([("claude_sonnet_5_5", "0.327", "1.637")]);

    // T-10-2366: 判定の全組み合わせ（ゲートウェイ × 単価 × 環境）。
    [Theory]
    [InlineData(true, true, LlmPricingStartupVerdict.Refuse)]
    [InlineData(true, false, LlmPricingStartupVerdict.Warn)]
    [InlineData(false, true, LlmPricingStartupVerdict.Ok)]
    [InlineData(false, false, LlmPricingStartupVerdict.Ok)]
    public void 単価が無い表はゲートウェイと環境で扱いが決まる(
        bool gatewayConfigured, bool isProduction, LlmPricingStartupVerdict expected)
    {
        LlmPricingStartupGuard.Evaluate(Empty, gatewayConfigured, isProduction).Should().Be(expected);
    }

    // T-10-2367: 単価があればどの環境でも止めない・警告しない（モデル別の表・従来キーの片側だけでも）。
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void 単価があれば何もしない(bool isProduction)
    {
        LlmPricingStartupGuard.Evaluate(PerModel, true, isProduction).Should().Be(LlmPricingStartupVerdict.Ok);
        LlmPricingStartupGuard.Evaluate(LlmPriceTable.From([], "0.819", "4.093"), true, isProduction)
            .Should().Be(LlmPricingStartupVerdict.Ok);
        LlmPricingStartupGuard.Evaluate(LlmPriceTable.From([], "0.819", null), true, isProduction)
            .Should().Be(LlmPricingStartupVerdict.Ok);
    }

    // T-10-2368: 解析できない・非正の単価だけの構成は「単価が無い」と同じ（誤設定を 0 円で通さない）。
    [Fact]
    public void 不正な単価だけなら配備では止める()
    {
        var table = LlmPriceTable.From([("claude_sonnet_5_5", "abc", "0")], "-1", "");

        LlmPricingStartupGuard.Evaluate(table, true, true).Should().Be(LlmPricingStartupVerdict.Refuse);
    }

    // 文言は運用者がログを引く目印・投入手段・判断の記録を含む（Runbook と同じ語）。
    [Fact]
    public void 拒否の文言は目印と投入手段を含む()
    {
        LlmPricingStartupGuard.RefusalMessage.Should().StartWith(LlmPricingStartupGuard.Marker)
            .And.Contain("LlmPricing__PerModel__").And.Contain("IADR-0499");
        LlmPricingStartupGuard.WarningMessage.Should().StartWith(LlmPricingStartupGuard.Marker);
    }
}

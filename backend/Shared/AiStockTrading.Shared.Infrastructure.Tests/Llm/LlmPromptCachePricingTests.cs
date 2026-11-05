using AiStockTrading.Shared.Infrastructure.Composable.Llm;
using AwesomeAssertions;
using Xunit;

namespace AiStockTrading.Shared.Infrastructure.Tests.Llm;

// NFR（費用）, FR-04, NFR-13, #1523, IADR-0607 決定4〜6: キャッシュの書き・読みを、それぞれの単価で数える。
// 金額 = 残り × 入力 ＋ 書き × 書き単価 ＋ 読み × 読み単価 ＋ 出力 × 出力（残り = 全体 − 書き − 読み）。
// 単価は MSP の LlmGateway/appsettings.json の単価表（USD/百万トークン）を 163.71 円/USD で円/1k トークンへ換算した値
// （values-local.yaml と同じ）。Haiku 5.5 の段は全体の入力トークン数で選ぶ。
public class LlmPromptCachePricingTests
{
    // values-local.yaml の単価の行と同じ値（円/1k トークン）。
    private static readonly LlmPriceTable Table = LlmPriceTable.FromRows(
    [
        new LlmPriceRow("claude-opus-5-5", "0.655", "3.274", CacheWrite: "0.819", CacheRead: "0.0327"),
        new LlmPriceRow("claude-sonnet-5-5", "0.327", "1.637", CacheWrite: "0.409", CacheRead: "0.0164"),
        new LlmPriceRow("claude-haiku-5-5", "0.0164", "0.0819", "100000", "0.0819", "0.409",
            CacheWrite: "0.0205", CacheRead: "0.00164", LongContextCacheWrite: "0.102", LongContextCacheRead: "0.00819"),
    ]);

    [Fact]
    public void 単価はMSPの単価表を円毎1kトークンへ換算した値である()
    {
        Table.Resolve("claude-opus-5-5", 1000).Should().Be(new LlmPrice(0.655m, 3.274m, 0.819m, 0.0327m));
        Table.Resolve("claude-sonnet-5-5", 1000).Should().Be(new LlmPrice(0.327m, 1.637m, 0.409m, 0.0164m));
        Table.Resolve("claude-haiku-5-5", 100_000).Should().Be(new LlmPrice(0.0164m, 0.0819m, 0.0205m, 0.00164m));
        Table.Resolve("claude-haiku-5-5", 100_001).Should().Be(new LlmPrice(0.0819m, 0.409m, 0.102m, 0.00819m));
    }

    [Fact]
    public void 書きと読みと残りをそれぞれの単価で数える()
    {
        // sonnet: 全体 3,000（うち読み 2,048・書き 0）・出力 50。残り 952。
        var price = Table.Resolve("claude-sonnet-5-5", 3000);
        var amount = LlmPricing.Compute(3000, 0, 2048, 50, price);

        amount.Should().Be((952 / 1000m * 0.327m) + (2048 / 1000m * 0.0164m) + (50 / 1000m * 1.637m));
    }

    [Fact]
    public void 書きの呼び出しは書き単価で数える()
    {
        // opus: 全体 3,000（うち書き 2,048）・出力 10。
        var amount = LlmPricing.Compute(3000, 2048, 0, 10, Table.Resolve("claude-opus-5-5", 3000));

        amount.Should().Be((952 / 1000m * 0.655m) + (2048 / 1000m * 0.819m) + (10 / 1000m * 3.274m));
    }

    [Fact]
    public void Haikuの段は全体の入力トークン数で選ぶ()
    {
        // 全体 120,000（うち読み 110,000）。残りは 10,000 だが、段は全体で選ぶ（提供元の判定量）→ 第 2 段。
        var price = Table.Resolve("claude-haiku-5-5", 120_000);
        var amount = LlmPricing.Compute(120_000, 0, 110_000, 100, price);

        price.Should().Be(new LlmPrice(0.0819m, 0.409m, 0.102m, 0.00819m));
        amount.Should().Be((10_000 / 1000m * 0.0819m) + (110_000 / 1000m * 0.00819m) + (100 / 1000m * 0.409m));
    }

    [Fact]
    public void キャッシュを使わない呼び出しの金額は従前の式と同じ()
    {
        var price = Table.Resolve("claude-sonnet-5-5", 3000);

        LlmPricing.Compute(3000, 0, 0, 50, price).Should().Be(LlmPricing.Compute(3000, 50, price));
    }

    [Fact]
    public void 書きと読みの単価が無い行は書きを入力の125倍_読みを入力単価で数える()
    {
        var table = LlmPriceTable.FromRows([new LlmPriceRow("claude-sonnet-5-5", "0.327", "1.637")]);
        var price = table.Resolve("claude-sonnet-5-5", 3000);

        price.EffectiveCacheWritePer1kTokens.Should().Be(0.327m * 1.25m);
        price.EffectiveCacheReadPer1kTokens.Should().Be(0.327m);
        LlmPricing.Compute(3000, 1000, 1000, 0, price)
            .Should().Be((1000 / 1000m * 0.327m) + (1000 / 1000m * 0.327m * 1.25m) + (1000 / 1000m * 0.327m));
    }

    [Theory]
    [InlineData("abc")]
    [InlineData("0")]
    [InlineData("-1")]
    public void 書きと読みの単価が解析できない行は行を外さず導いた単価で数える(string bad)
    {
        var table = LlmPriceTable.FromRows([new LlmPriceRow("claude-sonnet-5-5", "0.327", "1.637", CacheWrite: bad, CacheRead: bad)]);
        var price = table.Resolve("claude-sonnet-5-5", 3000);

        price.InputPer1kTokens.Should().Be(0.327m, "行は表に残る（未知モデルの単価へ倒さない）");
        price.EffectiveCacheWritePer1kTokens.Should().Be(0.327m * 1.25m);
        price.EffectiveCacheReadPer1kTokens.Should().Be(0.327m);
    }

    [Fact]
    public void 未知モデルは未知モデルの計上単価から導いた書きと読みで数える()
    {
        var price = Table.Resolve("claude-unknown", 3000);

        price.Should().Be(LlmPriceTable.DefaultUnknownModelPrice);
        price.EffectiveCacheWritePer1kTokens.Should().Be(1.637m * 1.25m);
        price.EffectiveCacheReadPer1kTokens.Should().Be(1.637m);
    }

    [Fact]
    public void 書きと読みが全体を超える申告は残りを_0_とし_負のトークンは_0_とみなす()
    {
        var price = Table.Resolve("claude-sonnet-5-5", 1000);

        LlmPricing.Compute(1000, 600, 600, 0, price).Should().Be((600 / 1000m * 0.409m) + (600 / 1000m * 0.0164m));
        LlmPricing.Compute(1000, -5, -5, -5, price).Should().Be(1000 / 1000m * 0.327m);
    }
}

using AiStockTrading.Shared.Infrastructure.Composable.Llm;
using AwesomeAssertions;
using Xunit;

namespace AiStockTrading.Shared.Infrastructure.Tests.Llm;

// NFR（費用）, FR-04, IADR-0055 決定2: LLM 単価適用（純関数）。
// fail-safe: 単価未設定（0）なら 0 円＝計上しても統制判定に影響しない。
// IADR-0122 決定2: モデル別単価を #282（report-service）と共有するため TradeDecisionService.Domain から移設した。
public class LlmPricingTests
{
    [Fact]
    public void トークン数と単価から費用を算出する()
    {
        // 入力 1000 × 0.5 円/1k = 0.5、出力 2000 × 1.5 円/1k = 3.0 → 3.5 円
        LlmPricing.Compute(inputTokens: 1000, outputTokens: 2000, inputPer1kTokens: 0.5m, outputPer1kTokens: 1.5m)
            .Should().Be(3.5m);
    }

    [Fact]
    public void 端数トークンも按分して算出する()
    {
        // 入力 1500 × 2 円/1k = 3.0、出力 0 → 3.0 円
        LlmPricing.Compute(1500, 0, 2m, 10m).Should().Be(3m);
    }

    [Fact]
    public void 単価未設定_0_なら費用は_0_円_安全既定()
    {
        LlmPricing.Compute(10_000, 20_000, 0m, 0m).Should().Be(0m);
    }

    [Fact]
    public void 負のトークンは_0_として扱う()
    {
        LlmPricing.Compute(-100, -50, 1m, 1m).Should().Be(0m);
    }

    [Fact]
    public void トークン_0_なら費用は_0_円()
    {
        LlmPricing.Compute(0, 0, 100m, 100m).Should().Be(0m);
    }

    // IADR-0122 決定1: 単価表が解決した LlmPrice をそのまま適用できる（計上側が単価を分解しない）。
    [Fact]
    public void 解決済みの単価をそのまま適用する()
    {
        LlmPricing.Compute(1000, 2000, new LlmPrice(0.327m, 1.637m)).Should().Be(3.601m);
    }
}

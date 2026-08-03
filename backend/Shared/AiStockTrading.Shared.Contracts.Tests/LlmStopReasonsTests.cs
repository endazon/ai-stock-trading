using AiStockTrading.Shared.Contracts.Llm;
using AwesomeAssertions;
using Xunit;

namespace AiStockTrading.Shared.Contracts.Tests;

// #247, FR-04, IADR-0104 決定1: LLM ゲートウェイ応答の終了理由（stopReason）語彙の判定。
// 未知値は既定へ落とさず透過する（enum にしない理由）。判定は大小無視の完全一致のみ。
public class LlmStopReasonsTests
{
    [Theory]
    [InlineData("refusal")]
    [InlineData("REFUSAL")]
    [InlineData("Refusal")]
    public void 拒否は大小を無視して判定する(string stopReason) =>
        LlmStopReasons.IsRefusal(stopReason).Should().BeTrue();

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("end_turn")]
    [InlineData("max_tokens")]
    [InlineData("future_reason")] // 未知値は透過（拒否として扱わない）
    [InlineData("refusalx")]      // 部分一致は拒否ではない
    public void 拒否以外_未知値_欠落は拒否ではない(string? stopReason) =>
        LlmStopReasons.IsRefusal(stopReason).Should().BeFalse();

    [Theory]
    [InlineData("max_tokens")]
    [InlineData("MAX_TOKENS")]
    public void 上限到達は大小を無視して判定する(string stopReason) =>
        LlmStopReasons.IsMaxTokens(stopReason).Should().BeTrue();

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("end_turn")]
    [InlineData("refusal")]
    [InlineData("future_reason")]
    public void 上限到達以外_未知値_欠落は上限到達ではない(string? stopReason) =>
        LlmStopReasons.IsMaxTokens(stopReason).Should().BeFalse();

    // 上流（microservices-platform の CompletionStopReasons）語彙の写像であることを固定する。
    [Fact]
    public void 上流語彙の文字列と一致する()
    {
        LlmStopReasons.EndTurn.Should().Be("end_turn");
        LlmStopReasons.MaxTokens.Should().Be("max_tokens");
        LlmStopReasons.Refusal.Should().Be("refusal");
        LlmStopReasons.StopSequence.Should().Be("stop_sequence");
        LlmStopReasons.ToolUse.Should().Be("tool_use");
    }
}

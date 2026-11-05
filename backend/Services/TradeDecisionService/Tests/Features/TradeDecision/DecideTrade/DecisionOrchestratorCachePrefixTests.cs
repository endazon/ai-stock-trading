using AiStockTrading.Shared.Contracts.Llm;
using TradeDecisionService.Features.TradeDecision;
using TradeDecisionService.Features.TradeDecision.DecideTrade;
using AwesomeAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace TradeDecisionService.Tests;

// FR-04, NFR, #1523, IADR-0607 決定2/3: 一次・二次の要求に、それぞれの接頭辞を分けて渡す。
// model・purpose は従前のまま（判断の内容は変えない）。接頭辞が合わなければ分けずに全文を送る。
public class DecisionOrchestratorCachePrefixTests
{
    private sealed class RecordingLlm(params string[] outputs) : ILlmCompletionClient
    {
        private int _index;
        public List<(LlmPrompt Prompt, string? Model, string? Purpose)> Calls { get; } = [];

        public Task<string> CompleteAsync(
            string prompt, string? model = null, string? purpose = null, CancellationToken ct = default) =>
            CompleteAsync(new LlmPrompt(null, prompt), model, purpose, ct);

        public Task<string> CompleteAsync(
            LlmPrompt prompt, string? model = null, string? purpose = null, CancellationToken ct = default)
        {
            Calls.Add((prompt, model, purpose));
            var output = outputs[Math.Min(_index, outputs.Length - 1)];
            _index++;
            return Task.FromResult(output);
        }
    }

    private const string Buy =
        """{"action":"Buy","rationale":"理由","referencePrice":1000,"stopLossDistancePerShare":30}""";

    [Fact]
    public async Task 一次と二次にそれぞれの接頭辞を分けて渡しmodelとpurposeは変えない()
    {
        var llm = new RecordingLlm(Buy);
        var options = new DecisionOrchestrationOptions
        {
            VoteCount = 2,
            EnableScreening = true,
            PrimaryModel = "claude-haiku-5-5",
            SecondaryModel = "claude-sonnet-5-5",
        };
        var orchestrator = new DecisionOrchestrator(llm, options, NullLogger<DecisionOrchestrator>.Instance);

        await orchestrator.DecideAsync(
            () => "SCREEN-PREFIX|screen-rest", "DECISION-PREFIX|decision-rest", signedHeldQuantity: null,
            screeningCacheablePrefix: "SCREEN-PREFIX|", decisionCacheablePrefix: "DECISION-PREFIX|");

        llm.Calls.Should().HaveCount(3);
        llm.Calls[0].Prompt.Should().Be(new LlmPrompt("SCREEN-PREFIX|", "screen-rest"));
        llm.Calls[0].Model.Should().Be("claude-haiku-5-5");
        llm.Calls[0].Purpose.Should().Be(LlmPurposes.TradeDecisionScreening);
        foreach (var call in llm.Calls.Skip(1))
        {
            call.Prompt.Should().Be(new LlmPrompt("DECISION-PREFIX|", "decision-rest"));
            call.Model.Should().Be("claude-sonnet-5-5");
            call.Purpose.Should().Be(LlmPurposes.TradeDecision);
        }
    }

    [Fact]
    public async Task 接頭辞を渡さないか合わなければ分けずに全文を送る()
    {
        var llm = new RecordingLlm(Buy);
        var options = new DecisionOrchestrationOptions { VoteCount = 1, EnableScreening = true };
        var orchestrator = new DecisionOrchestrator(llm, options, NullLogger<DecisionOrchestrator>.Instance);

        await orchestrator.DecideAsync(
            () => "screen", "decision", signedHeldQuantity: null, decisionCacheablePrefix: "NOT-A-PREFIX");

        llm.Calls.Select(c => c.Prompt).Should().Equal(new LlmPrompt(null, "screen"), new LlmPrompt(null, "decision"));
    }

    // 既存の偽物（文字列の多重定義だけを実装する）は、既定の実装を通じて全文を受け取る（従前の試験の期待は変わらない）。
    private sealed class StringOnlyLlm : ILlmCompletionClient
    {
        public List<string> Prompts { get; } = [];

        public Task<string> CompleteAsync(
            string prompt, string? model = null, string? purpose = null, CancellationToken ct = default)
        {
            Prompts.Add(prompt);
            return Task.FromResult(Buy);
        }
    }

    [Fact]
    public async Task 文字列だけを受ける実装には接頭辞と残りを足した全文が届く()
    {
        var llm = new StringOnlyLlm();
        var orchestrator = new DecisionOrchestrator(
            llm, new DecisionOrchestrationOptions { VoteCount = 1, EnableScreening = true }, NullLogger<DecisionOrchestrator>.Instance);

        await orchestrator.DecideAsync(
            () => "SP|screen", "DP|decision", signedHeldQuantity: null,
            screeningCacheablePrefix: "SP|", decisionCacheablePrefix: "DP|");

        llm.Prompts.Should().Equal("SP|screen", "DP|decision");
    }
}

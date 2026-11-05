using System.Net;
using System.Text;
using System.Text.Json;
using AiStockTrading.Shared.Contracts.Llm;
using AiStockTrading.Shared.Infrastructure.Composable.Llm;
using TradeDecisionService.Features.TradeDecision;
using TradeDecisionService.Features.TradeDecision.RecordStage0Decisions;
using TradeDecisionService.Infrastructure.ExternalServices;
using AwesomeAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace TradeDecisionService.Tests;

// FR-04, NFR（費用）, #1523, IADR-0607: 取引判断の egress は接頭辞を分けて送り、応答のキャッシュの書き・読みを計上へ渡す。
// 計上（Stage 0 の記録の費用を含む）は書き・読み・残りをそれぞれの単価で数える。
public class PromptCacheEgressAndCostTests
{
    private sealed class CapturingHandler(string body) : HttpMessageHandler
    {
        public string? Body { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        }
    }

    private sealed class RecordingReporter : ILlmUsageReporter
    {
        public LlmUsage? Last { get; private set; }

        public Task ReportAsync(LlmUsage usage, CancellationToken cancellationToken = default)
        {
            Last = usage;
            return Task.CompletedTask;
        }
    }

    private const string Response =
        """{"text":"{\"action\":\"Hold\",\"rationale\":\"見送り\"}","model":"claude-sonnet-5-5","inputTokens":3000,"outputTokens":50,"sent":true,"stopReason":"end_turn","cacheCreationInputTokens":0,"cacheReadInputTokens":2048}""";

    private static HttpLlmCompletionClient Client(HttpMessageHandler handler, ILlmUsageReporter reporter) =>
        new(new HttpClient(handler) { BaseAddress = new Uri("http://llm-gateway") },
            NullLogger<HttpLlmCompletionClient>.Instance, "internal", purposeOverride: null, reporter);

    [Fact]
    public async Task 接頭辞を分けて送りmax_tokensとmodelとpurposeは従前のまま()
    {
        var handler = new CapturingHandler(Response);

        await Client(handler, new RecordingReporter()).CompleteAsync(
            new LlmPrompt("接頭辞|", "残り"), "claude-sonnet-5-5", LlmPurposes.TradeDecision);

        using var sent = JsonDocument.Parse(handler.Body!);
        sent.RootElement.GetProperty("cacheablePrefix").GetString().Should().Be("接頭辞|");
        sent.RootElement.GetProperty("prompt").GetString().Should().Be("残り");
        sent.RootElement.GetProperty("maxTokens").GetInt32().Should().Be(4096);
        sent.RootElement.GetProperty("model").GetString().Should().Be("claude-sonnet-5-5");
        sent.RootElement.GetProperty("purpose").GetString().Should().Be(LlmPurposes.TradeDecision);
    }

    [Fact]
    public async Task 文字列の呼び出しは従前どおり接頭辞なしで全文を送る()
    {
        var handler = new CapturingHandler(Response);

        await Client(handler, new RecordingReporter()).CompleteAsync("全文", "claude-sonnet-5-5", LlmPurposes.TradeDecision);

        using var sent = JsonDocument.Parse(handler.Body!);
        sent.RootElement.GetProperty("cacheablePrefix").ValueKind.Should().Be(JsonValueKind.Null);
        sent.RootElement.GetProperty("prompt").GetString().Should().Be("全文");
    }

    [Fact]
    public async Task 応答のキャッシュの書きと読みを計上へ渡す()
    {
        var reporter = new RecordingReporter();

        await Client(new CapturingHandler(Response), reporter).CompleteAsync(
            new LlmPrompt("接頭辞|", "残り"), "claude-sonnet-5-5", LlmPurposes.TradeDecision);

        reporter.Last.Should().Be(new LlmUsage(LlmPurposes.TradeDecision, 3000, 50, "claude-sonnet-5-5",
            CacheCreationInputTokens: 0, CacheReadInputTokens: 2048));
    }

    // values-local.yaml と同じ単価（円/1k トークン）。
    private static LlmPriceTable Prices() => LlmPriceTable.FromRows(
    [
        new LlmPriceRow("claude-sonnet-5-5", "0.327", "1.637", CacheWrite: "0.409", CacheRead: "0.0164"),
        new LlmPriceRow("claude-haiku-5-5", "0.0164", "0.0819", "100000", "0.0819", "0.409",
            CacheWrite: "0.0205", CacheRead: "0.00164", LongContextCacheWrite: "0.102", LongContextCacheRead: "0.00819"),
    ]);

    [Fact]
    public void 計上の金額は書き_読み_残りをそれぞれの単価で数える()
    {
        var usage = new LlmUsage(LlmPurposes.TradeDecision, 3000, 50, "claude-sonnet-5-5",
            CacheCreationInputTokens: 0, CacheReadInputTokens: 2048);

        PublishingLlmUsageReporter.AmountOf(usage, Prices())
            .Should().Be((952 / 1000m * 0.327m) + (2048 / 1000m * 0.0164m) + (50 / 1000m * 1.637m));
    }

    [Fact]
    public void 計上のHaikuの段は全体の入力トークン数で選ぶ()
    {
        var usage = new LlmUsage(LlmPurposes.TradeDecisionScreening, 120_000, 100, "claude-haiku-5-5",
            CacheCreationInputTokens: 0, CacheReadInputTokens: 110_000);

        PublishingLlmUsageReporter.AmountOf(usage, Prices())
            .Should().Be((10_000 / 1000m * 0.0819m) + (110_000 / 1000m * 0.00819m) + (100 / 1000m * 0.409m));
    }

    [Fact]
    public void 入力トークン数が取れない計上は高い側の段で引き金額の式の入力は_0_のまま()
    {
        var usage = new LlmUsage(LlmPurposes.TradeDecisionScreening, null, 100, "claude-haiku-5-5");

        PublishingLlmUsageReporter.AmountOf(usage, Prices()).Should().Be(100 / 1000m * 0.409m);
    }

    [Fact]
    public void Stage0の記録の費用も同じ式で数える()
    {
        var usages = new[]
        {
            new LlmUsage(LlmPurposes.TradeDecision, 3000, 50, "claude-sonnet-5-5", CacheCreationInputTokens: 2048),
            new LlmUsage(LlmPurposes.TradeDecision, 3000, 50, "claude-sonnet-5-5", CacheReadInputTokens: 2048),
        };

        Stage0RecordingUsageCollector.CostOf(usages, Prices()).Should().Be(
            (952 / 1000m * 0.327m) + (2048 / 1000m * 0.409m) + (50 / 1000m * 1.637m)
            + (952 / 1000m * 0.327m) + (2048 / 1000m * 0.0164m) + (50 / 1000m * 1.637m));
    }
}

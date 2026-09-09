using System.Net;
using AiStockTrading.Shared.Contracts.Events;
using AiStockTrading.Shared.Contracts.Llm;
using TradeDecisionService.Infrastructure.ExternalServices;
using TradeDecisionService.Features.TradeDecision;
using TradeDecisionService.Domain;
using AwesomeAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace TradeDecisionService.Tests;

// NFR-05, FR-04, FR-11, #724, IADR-0323: **認可の失敗を「割当モデルが利用できない」として記録しない。**
//
// 🔴 これは安全側かどうかの話ではない（どちらも Hold へ倒れる）。**記録の正しさ**の話である。
// 401/403 を `ModelUnavailable` へ倒すと、監査台帳・Discord 通知・月報の内訳に
// 「割当モデルが利用できません」という**誤った原因**が残り、次に読む人が LLM 提供側を疑う。
// 実際の原因は s2s の資格情報・付与ロールであり、モデルの可用性とは無関係である。
//
// 陰性対照の置き方: 401/403 で **`TradeDecisionSkipped` が 1 件も出ないこと**と、
// Hold の理由に **「割当モデル」の語が現れないこと**を対で固定する。
// 400/404 側（本物のモデル不可）は従来どおり出ることも同時に固定する
//（これが無いと「何も記録しない」実装でも緑になる）。
public class HttpLlmCompletionClientAuthorizationTests
{
    private static HttpLlmCompletionClient Client(
        HttpMessageHandler handler, RecordingGovernanceReporter governance) =>
        new(new HttpClient(handler) { BaseAddress = new Uri("http://llm-gateway") },
            NullLogger<HttpLlmCompletionClient>.Instance, "internal", LlmPurposes.TradeDecision,
            new NoOpLlmUsageReporter(), logPrompts: false, governanceReporter: governance);

    // ---- 🔴 否定形: 認可の失敗はモデル不可として記録されない --------------------------------

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized)]  // 401: 資格情報が無い・失効
    [InlineData(HttpStatusCode.Forbidden)]     // 403: 認証は通ったがロールが足りない（platform-service 未付与）
    public async Task 認可の失敗は見送りとして記録されず_理由にも割当モデルが現れない(HttpStatusCode status)
    {
        var governance = new RecordingGovernanceReporter();

        var output = await Client(new StubHandler(status, ""), governance).CompleteAsync("p");

        // ① 取引はしない（安全側は不変）。
        TradeDecisionParser.Parse(output).Action.Should().Be(TradeAction.Hold);
        // ② 🔴 誤帰属しない。理由に「割当モデル」「モデルが利用でき」の語を出さない。
        output.Should().NotContain("割当モデル");
        output.Should().Contain("認可");
        // ③ 🔴 TradeDecisionSkipped を出さない。通知の題名が「割当モデルが利用できません」で固定されているため、
        //    事由の文字列だけ足しても誤帰属が別の層で再生産される（NotificationFormatter）。
        governance.Skips.Should().BeEmpty();
        governance.Fallbacks.Should().BeEmpty();
    }

    // ---- 対照群: 本物のモデル不可（400 系のうち 401/403/429 以外）は従来どおり記録される ----
    // これが無いと「認可も何も、一切記録しない」実装でも上の否定形が緑になる。

    [Theory]
    [InlineData(HttpStatusCode.BadRequest)]  // 400
    [InlineData(HttpStatusCode.NotFound)]    // 404
    public async Task 本物のモデル不可は従来どおり見送りとして記録される(HttpStatusCode status)
    {
        var governance = new RecordingGovernanceReporter();

        var output = await Client(new StubHandler(status, ""), governance).CompleteAsync("p");

        TradeDecisionParser.Parse(output).Action.Should().Be(TradeAction.Hold);
        output.Should().Contain("割当モデル");
        governance.Skips.Should().ContainSingle()
            .Which.Reason.Should().Be(TradeDecisionSkipReasons.ModelUnavailable);
    }

    // 429（レート制限）は従来どおり「再試行」であって見送りの記録を出さない（ADR-0017 決定3・退行防止）。
    [Fact]
    public async Task レート制限は従来どおり見送りとして記録されない()
    {
        var governance = new RecordingGovernanceReporter();

        var output = await Client(new StubHandler((HttpStatusCode)429, ""), governance).CompleteAsync("p");

        TradeDecisionParser.Parse(output).Action.Should().Be(TradeAction.Hold);
        governance.Skips.Should().BeEmpty();
    }

    // ---- fake -------------------------------------------------------------------------------

    private sealed class RecordingGovernanceReporter : ILlmGovernanceReporter
    {
        public List<LlmAssignmentEvaluation> Fallbacks { get; } = [];

        public List<(string Purpose, string Reason, string? Expected, string? Effective)> Skips { get; } = [];

        public Task FallbackFiredAsync(
            LlmAssignmentEvaluation evaluation, string purpose, CancellationToken cancellationToken = default)
        {
            Fallbacks.Add(evaluation);
            return Task.CompletedTask;
        }

        public Task DecisionSkippedAsync(
            string purpose, string reason, string? expectedModel, string? effectiveModel,
            CancellationToken cancellationToken = default)
        {
            Skips.Add((purpose, reason, expectedModel, effectiveModel));
            return Task.CompletedTask;
        }
    }

    private sealed class StubHandler(HttpStatusCode status, string body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(body) });
    }
}

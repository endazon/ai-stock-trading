using System.Net;
using AiStockTrading.Shared.Contracts.Events;
using AiStockTrading.Shared.Contracts.Llm;
using AiStockTrading.TradeDecision.Application.Adapters;
using AiStockTrading.TradeDecision.Application.Ports;
using AiStockTrading.TradeDecision.Domain;
using AiStockTrading.TradeDecision.Infrastructure.Composable.Adapters;
using AwesomeAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AiStockTrading.TradeDecision.Infrastructure.Tests;

// FR-04, UC-01, ADR-0014 §決定3, ADR-0017 決定2/決定3, #335, IADR-0216:
// **取引判断のフォールバック禁止**の退行防止（統制系 3 点セット: 境界値・プロパティベース・否定形）。
//
// 🔴 計画の明文（ADR-0017 決定2）:
//   「取引判断は `claude-sonnet-5` に固定し、**いかなる理由でもフォールバックしない**。
//    指定モデルが利用できない場合、取引判断は実行されず、その結果として発注も行われない。
//    **この振る舞いは障害ではなく、設計上の正常な結果である。**」
//
// 本テスト群が守るのは「別モデルの応答で発注へ進まないこと」であり、`ILlmCompletionClient` が
// 返す JSON が Hold であることが**発注ゼロの構造的な根拠**である（判断パーサは Hold を発注へ写像しない）。
public class HttpLlmCompletionClientFallbackBanTests
{
    private const string Pin = "claude-sonnet-5";

    private static HttpLlmCompletionClient Client(
        HttpMessageHandler handler,
        RecordingGovernanceReporter? governance = null,
        string purpose = LlmPurposes.TradeDecision) =>
        new(new HttpClient(handler) { BaseAddress = new Uri("http://llm-gateway") },
            NullLogger<HttpLlmCompletionClient>.Instance, "internal", purpose,
            new NoOpLlmUsageReporter(), logPrompts: false,
            governanceReporter: governance ?? new RecordingGovernanceReporter());

    // 判断パーサは Buy/Sell に参照価格と損切り幅を要求する（無いと Hold へ丸まる）。
    // 本試験は「割当が合えば Buy が通る／合わなければ Hold になる」を対比させるため、成立する形で書く。
    private const string BuyJson =
        """{\"action\":\"Buy\",\"referencePrice\":1000,\"stopLossDistancePerShare\":30}""";

    private static string Body(string? model) =>
        model is null
            ? $$"""{"text":"{{BuyJson}}","inputTokens":10,"outputTokens":5,"sent":true}"""
            : $$"""{"text":"{{BuyJson}}","model":"{{model}}","inputTokens":10,"outputTokens":5,"sent":true}""";

    // ---- ピンどおりなら通す（対照群。これが無いと「常に Hold」でも緑になる） ------------------

    [Fact]
    public async Task ピン留めしたモデルが応答したら本文を判断へ渡す()
    {
        var governance = new RecordingGovernanceReporter();

        var output = await Client(new StubHandler(HttpStatusCode.OK, Body(Pin)), governance).CompleteAsync("p");

        TradeDecisionParser.Parse(output).Action.Should().Be(TradeAction.Buy);
        governance.Skips.Should().BeEmpty();
        governance.Fallbacks.Should().BeEmpty();
    }

    // ---- 🔴 否定形: ピン以外が応答したら発注へ進まない ----------------------------------------

    // 「発注ゼロ」の機械的な表明: 判断は Hold であり、**フォールバック候補への再呼び出しも 0 回**である
    // （AST 側で別モデルを試す経路が生えていないことの直接の証拠）。
    [Theory]
    [InlineData("claude-opus-5")]        // 基盤の DefaultModel へ無音で落ちた形（platform IADR-0102 の罠）
    [InlineData("claude-haiku-4-5")]     // 他用途の第 2 候補
    [InlineData("claude-opus-4-8")]      // 旧ピン（ADR-0014 が改定した値）
    [InlineData(null)]                   // モデル名を名乗らない応答
    public async Task 実効モデルがピンと違えば発注へ進まず_呼び出しも増やさない(string? effectiveModel)
    {
        var handler = new StubHandler(HttpStatusCode.OK, Body(effectiveModel));
        var governance = new RecordingGovernanceReporter();

        var output = await Client(handler, governance).CompleteAsync("p");

        // ① 発注ゼロ: 判断は Hold（本文の "Buy" は破棄されている）。
        TradeDecisionParser.Parse(output).Action.Should().Be(TradeAction.Hold);
        // ② フォールバック呼び出しゼロ: ゲートウェイを 1 回しか叩いていない（別モデルで再試行していない）。
        handler.Calls.Should().Be(1);
        // ③ 沈黙のスキップにしない（ADR-0017 決定2）。
        governance.Skips.Should().ContainSingle()
            .Which.Reason.Should().Be(TradeDecisionSkipReasons.ModelMismatch);
    }

    // 🔴 否定形（#335 の受け入れ基準）: `claude-fable-5` の応答は成果物にしない。
    [Fact]
    public async Task 禁止モデルが応答したら本文を破棄して見送る_fable_5_を判断へ流さない()
    {
        var handler = new StubHandler(HttpStatusCode.OK, Body(LlmAssignments.ForbiddenModel));
        var governance = new RecordingGovernanceReporter();

        var output = await Client(handler, governance).CompleteAsync("p");

        TradeDecisionParser.Parse(output).Action.Should().Be(TradeAction.Hold);
        handler.Calls.Should().Be(1);
        governance.Skips.Should().ContainSingle()
            .Which.Reason.Should().Be(TradeDecisionSkipReasons.ForbiddenModel);
    }

    // 🔴 否定形: **要求そのものが禁止モデルを名指ししない。**
    // AST は用途（purpose）だけを送り、モデル ID の解決は基盤に委ねる（01_architecture-overview）。
    [Fact]
    public async Task 要求本文に禁止モデルを載せない()
    {
        var handler = new CapturingHandler(Body(Pin));

        await Client(handler).CompleteAsync("p");

        handler.LastBody.Should().NotContain(LlmAssignments.ForbiddenModel);
    }

    // ---- 429（再試行）と 400 系（モデル不可）の分岐 ------------------------------------------

    // 🔴 429 は再試行であってフォールバックではない（ADR-0017 決定3）。
    // 発注しない点は同じだが、**見送りとして記録しない** —— 混雑のたびに「モデルが使えない」という
    // 誤った運用シグナルが積み上がると、恒常的な格下げを疑う根拠になってしまう。
    [Fact]
    public async Task レート制限_429_は見送りとして記録しない_再試行であってモデル不可ではない()
    {
        var governance = new RecordingGovernanceReporter();

        var output = await Client(new StubHandler(HttpStatusCode.TooManyRequests, ""), governance).CompleteAsync("p");

        TradeDecisionParser.Parse(output).Action.Should().Be(TradeAction.Hold);
        governance.Skips.Should().BeEmpty();
    }

    [Theory]
    [InlineData(HttpStatusCode.BadRequest)]
    [InlineData(HttpStatusCode.NotFound)]
    [InlineData(HttpStatusCode.UnprocessableEntity)]
    public async Task モデル不可_400系_は見送りとして記録する(HttpStatusCode status)
    {
        var governance = new RecordingGovernanceReporter();

        var output = await Client(new StubHandler(status, ""), governance).CompleteAsync("p");

        TradeDecisionParser.Parse(output).Action.Should().Be(TradeAction.Hold);
        governance.Skips.Should().ContainSingle()
            .Which.Reason.Should().Be(TradeDecisionSkipReasons.ModelUnavailable);
    }

    // 5xx は呼び出し先の不調であり「別モデルにすれば直る」失敗ではない（モデル不可として記録しない）。
    [Fact]
    public async Task ゲートウェイの不調_5xx_はモデル不可として記録しない()
    {
        var governance = new RecordingGovernanceReporter();

        await Client(new StubHandler(HttpStatusCode.ServiceUnavailable, ""), governance).CompleteAsync("p");

        governance.Skips.Should().BeEmpty();
    }

    // ---- スクリーニング層も同じ統制に服する -------------------------------------------------

    [Fact]
    public async Task スクリーニング層もピン以外なら見送る()
    {
        var governance = new RecordingGovernanceReporter();

        var output = await Client(
            new StubHandler(HttpStatusCode.OK, Body("claude-sonnet-5")), governance,
            LlmPurposes.TradeDecisionScreening).CompleteAsync("p");

        TradeDecisionParser.Parse(output).Action.Should().Be(TradeAction.Hold);
        governance.Skips.Should().ContainSingle();
    }

    [Fact]
    public async Task スクリーニング層はピン_haiku_なら通す()
    {
        var output = await Client(
            new StubHandler(HttpStatusCode.OK, Body(LlmAssignments.Haiku45)), governance: null,
            LlmPurposes.TradeDecisionScreening).CompleteAsync("p");

        TradeDecisionParser.Parse(output).Action.Should().Be(TradeAction.Buy);
    }

    // ---- 記録は best-effort（統制自体を止めない） -------------------------------------------

    // 記録に失敗しても**見送りは成立する**。記録できないことを理由に発注へ進んではならない。
    [Fact]
    public async Task 見送りの記録に失敗しても発注へ進まない()
    {
        var client = new HttpLlmCompletionClient(
            new HttpClient(new StubHandler(HttpStatusCode.OK, Body("claude-opus-5")))
            {
                BaseAddress = new Uri("http://llm-gateway"),
            },
            NullLogger<HttpLlmCompletionClient>.Instance, "internal", LlmPurposes.TradeDecision,
            new NoOpLlmUsageReporter(), logPrompts: false,
            governanceReporter: new ThrowingGovernanceReporter());

        TradeDecisionParser.Parse(await client.CompleteAsync("p")).Action.Should().Be(TradeAction.Hold);
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

    private sealed class ThrowingGovernanceReporter : ILlmGovernanceReporter
    {
        public Task FallbackFiredAsync(
            LlmAssignmentEvaluation evaluation, string purpose, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("記録の発行に失敗");

        public Task DecisionSkippedAsync(
            string purpose, string reason, string? expectedModel, string? effectiveModel,
            CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("記録の発行に失敗");
    }

    // 呼び出し回数を数える StubHandler（「フォールバック呼び出しゼロ」の証拠に使う）。
    private sealed class StubHandler(HttpStatusCode status, string body) : HttpMessageHandler
    {
        public int Calls { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(body) });
        }
    }

    private sealed class CapturingHandler(string body) : HttpMessageHandler
    {
        public string? LastBody { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastBody = request.Content is null
                ? null
                : await request.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body) };
        }
    }
}

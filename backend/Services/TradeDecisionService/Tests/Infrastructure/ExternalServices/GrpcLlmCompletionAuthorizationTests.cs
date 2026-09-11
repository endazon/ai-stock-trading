using AiStockTrading.Shared.Infrastructure.Grpc.LlmGateway.V1;
using AiStockTrading.Shared.Infrastructure.Composable.Llm;
using AiStockTrading.Shared.Contracts.Llm;
using TradeDecisionService.Infrastructure.ExternalServices;
using TradeDecisionService.Features.TradeDecision;
using TradeDecisionService.Domain;
using AwesomeAssertions;
using Grpc.Core;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using GrpcStatusCode = Grpc.Core.StatusCode;

namespace TradeDecisionService.Tests;

// NFR-05, FR-04, FR-11, MSP:ADR-0029, IADR-0284, IADR-0323 決定 3, IADR-0328, IADR-0332, #746:
// **輸送を gRPC に替えても、認可の失敗を「割当モデルが利用できない」として記録しない。**
//
// `HttpLlmCompletionClientAuthorizationTests`（REST）と**同じ主張**を gRPC で立てる。
// 🔴 分けて書く理由: 判定器は 1 つでも、**輸送ごとに status → 分類の写像が別**である。
// 写像を落とすと（例: UNAUTHENTICATED を Other へ倒す）判定器は無傷のまま誤帰属が復活する。
// 実際、gRPC には「4xx」が無く `INVALID_ARGUMENT` を 400 になぞらえたくなる誘引がある
// —— それをやるとモデル不可の誤帰属が別の入口から入る（IADR-0332 決定 3）。
public class GrpcLlmCompletionAuthorizationTests
{
    private static HttpLlmCompletionClient Client(
        LlmCompletion.LlmCompletionClient grpc, RecordingGovernanceReporter governance) =>
        new(new GrpcLlmCompletionTransport(grpc),
            NullLogger<HttpLlmCompletionClient>.Instance, "internal", LlmPurposes.TradeDecision,
            new NoOpLlmUsageReporter(), logPrompts: false, governanceReporter: governance);

    // ---- 🔴 否定形: 認可の失敗はモデル不可として記録されない -----------------------------------

    [Theory]
    [InlineData(GrpcStatusCode.Unauthenticated)]  // メタデータ無し（トークンが取れなかった場合も含む）
    [InlineData(GrpcStatusCode.PermissionDenied)] // platform-service ロール未付与
    public async Task 認可の失敗は見送りとして記録されず_理由にも割当モデルが現れない(GrpcStatusCode status)
    {
        var governance = new RecordingGovernanceReporter();

        var output = await Client(Failing(status), governance).CompleteAsync("p");

        // ① 取引はしない（安全側は輸送に依らず不変）。
        TradeDecisionParser.Parse(output).Action.Should().Be(TradeAction.Hold);
        // ② 🔴 誤帰属しない。
        output.Should().NotContain("割当モデル");
        output.Should().Contain("認可");
        // ③ 🔴 TradeDecisionSkipped を出さない（通知の題名が誤帰属を再生産するため）。
        governance.Skips.Should().BeEmpty();
        governance.Fallbacks.Should().BeEmpty();
    }

    // ---- 対照群 1: 認可以外の失敗は「伝送の失敗」へ倒れ、見送りとしても記録されない -------------
    // 🔴 REST は 400/404 を ModelUnavailable として**記録する**が、gRPC ではそれに当たる status が無い
    // （モデル不可は `sent=false` の応答で来る）。ここで Skips が出るなら、status からモデル不可を
    // 作る写像が復活したということである。

    [Theory]
    [InlineData(GrpcStatusCode.InvalidArgument)]
    [InlineData(GrpcStatusCode.Unimplemented)]   // gRPC 面が未配備（＝輸送の誤設定）
    [InlineData(GrpcStatusCode.Unavailable)]
    [InlineData(GrpcStatusCode.Internal)]
    [InlineData(GrpcStatusCode.ResourceExhausted)]
    public async Task 認可以外の失敗はモデル不可として記録されない(GrpcStatusCode status)
    {
        var governance = new RecordingGovernanceReporter();

        var output = await Client(Failing(status), governance).CompleteAsync("p");

        TradeDecisionParser.Parse(output).Action.Should().Be(TradeAction.Hold);
        output.Should().NotContain("割当モデル");
        output.Should().NotContain("認可");
        governance.Skips.Should().BeEmpty();
    }

    // ---- 対照群 2: 本物のモデル不可（縮退・割当逸脱）は gRPC でも従来どおり記録される -----------
    // これが無いと「gRPC では一切記録しない」実装でも上の 2 つが緑になる。

    [Fact]
    public async Task 割当外のモデルが答えたら_gRPC_でも見送りとして記録される()
    {
        var governance = new RecordingGovernanceReporter();
        var grpc = Responding(new CompleteResponse
        {
            Text = """{"action":"Buy","rationale":"x"}""",
            Sent = true,
            // 用途 trade-decision の割当（ADR-0011 のピン）と違うモデルが答えた。
            Model = "some-other-model",
            StopReason = "end_turn",
        });

        var output = await Client(grpc, governance).CompleteAsync("p");

        TradeDecisionParser.Parse(output).Action.Should().Be(TradeAction.Hold);
        output.Should().Contain("割当モデル");
        governance.Skips.Should().ContainSingle();
    }

    // 縮退（sent=false）は「送っていない」＝伝送の失敗と同じ倒れ先（REST の Sent=false と同値）。
    [Fact]
    public async Task 縮退は_gRPC_でも取引しない安全側へ倒れる()
    {
        var governance = new RecordingGovernanceReporter();
        var grpc = Responding(new CompleteResponse { Text = "越境不可", Sent = false });

        var output = await Client(grpc, governance).CompleteAsync("p");

        TradeDecisionParser.Parse(output).Action.Should().Be(TradeAction.Hold);
        governance.Skips.Should().BeEmpty();
    }

    // 打ち切り（deadline 超過）は呼び出し元のタイムアウト枝へ倒れる（停止要求ではないので伝播しない）。
    [Fact]
    public async Task deadline_超過は取引しない安全側へ倒れ_停止要求としては伝播しない()
    {
        var governance = new RecordingGovernanceReporter();

        var output = await Client(Failing(GrpcStatusCode.DeadlineExceeded), governance).CompleteAsync("p");

        TradeDecisionParser.Parse(output).Action.Should().Be(TradeAction.Hold);
        governance.Skips.Should().BeEmpty();
    }

    // 🔴 呼び出し側の停止要求は**握り潰さない**（縮退させると停止が効かなくなる）。
    [Fact]
    public async Task 呼び出し側の停止要求は伝播する()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        var client = Client(Failing(GrpcStatusCode.Cancelled), new RecordingGovernanceReporter());

        var act = async () => await client.CompleteAsync("p", cancellationToken: cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    // ---- test double -----------------------------------------------------------------------------

    private static FakeCompletionClient Responding(CompleteResponse response) => new(_ => response);

    private static FakeCompletionClient Failing(GrpcStatusCode status) =>
        new(_ => throw new RpcException(new Status(status, "test")));

    private sealed class FakeCompletionClient(Func<CompleteRequest, CompleteResponse> respond)
        : LlmCompletion.LlmCompletionClient
    {
        public override AsyncUnaryCall<CompleteResponse> CompleteAsync(
            CompleteRequest request, CallOptions options)
        {
            Task<CompleteResponse> response;
            try
            {
                response = Task.FromResult(respond(request));
            }
            catch (Exception ex)
            {
                response = Task.FromException<CompleteResponse>(ex);
            }

            return new AsyncUnaryCall<CompleteResponse>(
                response, Task.FromResult(new Metadata()), () => Status.DefaultSuccess,
                () => new Metadata(), () => { });
        }
    }

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
}

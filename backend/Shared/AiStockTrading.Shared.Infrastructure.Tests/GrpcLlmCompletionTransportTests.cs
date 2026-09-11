using AiStockTrading.Shared.Contracts.Llm;
using AiStockTrading.Shared.Infrastructure.Composable.Llm;
using AiStockTrading.Shared.Infrastructure.Grpc.LlmGateway.V1;
using AwesomeAssertions;
using Grpc.Core;
using Xunit;
using GrpcStatusCode = Grpc.Core.StatusCode;

namespace AiStockTrading.Shared.Infrastructure.Tests;

// NFR, FR-04, FR-06, MSP:ADR-0029, IADR-0284, IADR-0323 決定 3, IADR-0328, IADR-0332, #746:
// east-west gRPC 輸送（`platform.llmgateway.v1.LlmCompletion/Complete`）の写像。
//
// 固定するのは 4 点である。
//   ① 認可の失敗（UNAUTHENTICATED / PERMISSION_DENIED）が `Unauthorized` へ倒れること（陽性）
//   ② それ以外の status が `ModelUnavailable` に**ならない**こと（陰性対照。IADR-0332 決定 3）
//   ③ deadline が `CallOptions.Deadline` に載ること（陽性）／要求単位の指定が既定より優先されること（陰性対照）
//   ④ 縮退（sent=false）が**応答として**返り、例外にならないこと（基盤の「縮退はエラーではない」）
public class GrpcLlmCompletionTransportTests
{
    private static readonly LlmCompletionCall Call = new("prompt", 4096, null, "internal", "trade-decision");

    // ---- ① 認可の失敗（陽性） ---------------------------------------------------------------

    [Theory]
    [InlineData(GrpcStatusCode.Unauthenticated)]  // トークン無し（実装ガイド §4 の拒否表）
    [InlineData(GrpcStatusCode.PermissionDenied)] // platform-service ロール無し
    public async Task 認可の失敗は_Unauthorized_へ倒れる(GrpcStatusCode status)
    {
        var transport = new GrpcLlmCompletionTransport(Failing(status));

        var exchange = await transport.CompleteAsync(Call);

        exchange.Outcome.Should().Be(LlmTransportOutcome.Failed);
        exchange.Failure.Should().Be(LlmFailureKind.Unauthorized);
        // ログに出る状態は status の名前（HTTP の "401" に相当する位置）。
        exchange.Detail.Should().Be(status.ToString());
    }

    // ---- ② 陰性対照: 認可以外は ModelUnavailable にならない ---------------------------------
    // 🔴 これが無いと「全部 Unauthorized」でも ① が緑になり、かつ「4xx 相当を ModelUnavailable へ」の
    // 退行（＝IADR-0323 が閉じた誤帰属の作り直し）を捕まえられない。

    [Theory]
    [InlineData(GrpcStatusCode.ResourceExhausted, LlmFailureKind.Retryable)]   // 429 相当
    [InlineData(GrpcStatusCode.Unavailable, LlmFailureKind.Other)]            // 呼び出し先の不調
    [InlineData(GrpcStatusCode.Internal, LlmFailureKind.Other)]
    [InlineData(GrpcStatusCode.InvalidArgument, LlmFailureKind.Other)]        // 🔴 モデル不可ではない
    [InlineData(GrpcStatusCode.Unimplemented, LlmFailureKind.Other)]          // 🔴 gRPC 面が未配備＝輸送の誤設定
    [InlineData(GrpcStatusCode.NotFound, LlmFailureKind.Other)]
    [InlineData(GrpcStatusCode.FailedPrecondition, LlmFailureKind.Other)]
    public async Task 認可以外の_status_は_モデル不可にならない(GrpcStatusCode status, LlmFailureKind expected)
    {
        var exchange = await new GrpcLlmCompletionTransport(Failing(status)).CompleteAsync(Call);

        exchange.Failure.Should().Be(expected);
        exchange.Failure.Should().NotBe(LlmFailureKind.ModelUnavailable);
    }

    // 「モデルが使えない」が status から作られる経路が 1 つも無いことを、値域の全数で固定する。
    // （分類の単一情報源そのものを直接叩く。輸送を経ない分、退行が別の枝から入っても捕まる。）
    [Fact]
    public async Task どの_gRPC_status_も_ModelUnavailable_を作らない()
    {
        var kinds = Enum.GetValues<GrpcStatusCode>()
            .Select(s => LlmFailureClassification.ClassifyGrpcStatus((int)s))
            .Distinct()
            .ToArray();

        kinds.Should().NotContain(LlmFailureKind.ModelUnavailable);
        // 逆に「全部 Other」へ潰れていないこと（① の写像が生きている）。
        kinds.Should().Contain(LlmFailureKind.Unauthorized);
        kinds.Should().Contain(LlmFailureKind.Retryable);
        await Task.CompletedTask;
    }

    // 🔴 分類は**数値**で受ける（`LlmFailureClassification` は Domain から到達する契約プロジェクトにあり、
    // `Grpc.Core` を参照できない。IADR-0332 決定 3）。数値が実際の enum とずれていないことをここで縛る
    // —— ずれると「認可の失敗が Other へ落ちる」形で静かに壊れ、誤帰属が復活する。
    [Fact]
    public async Task 分類が持つ数値は実際の_gRPC_status_と一致する()
    {
        LlmFailureClassification.UnauthenticatedGrpcStatusCode
            .Should().Be((int)GrpcStatusCode.Unauthenticated);
        LlmFailureClassification.PermissionDeniedGrpcStatusCode
            .Should().Be((int)GrpcStatusCode.PermissionDenied);
        LlmFailureClassification.ResourceExhaustedGrpcStatusCode
            .Should().Be((int)GrpcStatusCode.ResourceExhausted);
        await Task.CompletedTask;
    }

    // ---- ③ deadline（IADR-0123 → CallOptions.Deadline） --------------------------------------

    [Fact]
    public async Task 要求単位の上限が_deadline_に載る()
    {
        var now = new DateTimeOffset(2026, 9, 11, 3, 0, 0, TimeSpan.Zero);
        var client = Responding(new CompleteResponse { Text = "ok", Sent = true });
        var transport = new GrpcLlmCompletionTransport(client, TimeSpan.FromSeconds(30), new FixedTime(now));

        await transport.CompleteAsync(Call, TimeSpan.FromSeconds(120));

        // 要求単位（週報・月報の 120 秒）が輸送の既定（30 秒）より優先される。
        client.LastOptions.Deadline.Should().Be(now.UtcDateTime.AddSeconds(120));
    }

    // 陰性対照 1: 要求単位の指定が無ければ輸送の既定（`LlmGateway:TimeoutSeconds` 相当）が載る。
    [Fact]
    public async Task 要求単位の指定が無ければ輸送の既定が_deadline_に載る()
    {
        var now = new DateTimeOffset(2026, 9, 11, 3, 0, 0, TimeSpan.Zero);
        var client = Responding(new CompleteResponse { Text = "ok", Sent = true });
        var transport = new GrpcLlmCompletionTransport(client, TimeSpan.FromSeconds(30), new FixedTime(now));

        await transport.CompleteAsync(Call);

        client.LastOptions.Deadline.Should().Be(now.UtcDateTime.AddSeconds(30));
    }

    // 陰性対照 2: 上限が 1 つも無ければ deadline を**付けない**（勝手な上限を作らない）。
    [Fact]
    public async Task 上限が無ければ_deadline_を付けない()
    {
        var client = Responding(new CompleteResponse { Text = "ok", Sent = true });

        await new GrpcLlmCompletionTransport(client).CompleteAsync(Call);

        client.LastOptions.Deadline.Should().BeNull();
    }

    // deadline 超過は「輸送の失敗」ではなく**打ち切り**として上がる（呼び出し元の既存タイムアウト枝へ）。
    [Theory]
    [InlineData(GrpcStatusCode.DeadlineExceeded)]
    [InlineData(GrpcStatusCode.Cancelled)]
    public async Task 打ち切りは_OperationCanceledException_として上がる(GrpcStatusCode status)
    {
        var transport = new GrpcLlmCompletionTransport(Failing(status));

        var act = async () => await transport.CompleteAsync(Call);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    // ---- ④ 縮退・応答の写し -------------------------------------------------------------------

    [Fact]
    public async Task 応答は輸送に依らない形へ写される()
    {
        var client = Responding(new CompleteResponse
        {
            Text = "買い",
            Sent = true,
            Model = "claude-opus-5",
            StopReason = "end_turn",
            InputTokens = 12,
            OutputTokens = 34,
        });

        var exchange = await new GrpcLlmCompletionTransport(client).CompleteAsync(Call);

        exchange.Outcome.Should().Be(LlmTransportOutcome.Completed);
        exchange.Payload.Should().Be(new LlmCompletionPayload("買い", true, "claude-opus-5", "end_turn", 12, 34));
        // 要求も 1 対 1 で載る（proto3 に null は無いので未指定は空文字）。
        client.LastRequest!.Prompt.Should().Be("prompt");
        client.LastRequest.MaxTokens.Should().Be(4096);
        client.LastRequest.Model.Should().BeEmpty();
        client.LastRequest.Confidentiality.Should().Be("internal");
        client.LastRequest.Purpose.Should().Be("trade-decision");
    }

    // 🔴 縮退（sent=false）はエラーではない —— 応答として返り、判定は呼び出し元（判定器）が行う。
    [Fact]
    public async Task 縮退は例外ではなく応答として返る()
    {
        var client = Responding(new CompleteResponse { Text = "越境不可", Sent = false });

        var exchange = await new GrpcLlmCompletionTransport(client).CompleteAsync(Call);

        exchange.Outcome.Should().Be(LlmTransportOutcome.Completed);
        exchange.Payload!.Sent.Should().BeFalse();
    }

    // 未報告（proto3 の既定＝空文字）は REST の null と同じ形へ戻す。
    // これを怠ると `LlmStopReasons` / `LlmAssignmentEvaluator` が空文字を「値がある」と読む。
    [Fact]
    public async Task 空文字の_model_と_stopReason_は_null_へ戻す()
    {
        var exchange = await new GrpcLlmCompletionTransport(
            Responding(new CompleteResponse { Text = "本文", Sent = true })).CompleteAsync(Call);

        exchange.Payload!.Model.Should().BeNull();
        exchange.Payload.StopReason.Should().BeNull();
    }

    // ---- test double ---------------------------------------------------------------------------

    private static FakeCompletionClient Responding(CompleteResponse response) =>
        new(_ => response);

    private static FakeCompletionClient Failing(GrpcStatusCode status) =>
        new(_ => throw new RpcException(new Status(status, "test")));

    private sealed class FakeCompletionClient(Func<CompleteRequest, CompleteResponse> respond)
        : LlmCompletion.LlmCompletionClient
    {
        public CompleteRequest? LastRequest { get; private set; }

        public CallOptions LastOptions { get; private set; }

        public override AsyncUnaryCall<CompleteResponse> CompleteAsync(
            CompleteRequest request, CallOptions options)
        {
            LastRequest = request;
            LastOptions = options;

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

    private sealed class FixedTime(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}

using AiStockTrading.Shared.Contracts.Llm;
using AiStockTrading.Shared.Infrastructure.Grpc.LlmGateway.V1;
using Grpc.Core;

namespace AiStockTrading.Shared.Infrastructure.Composable.Llm;

// NFR, FR-04, FR-06, MSP:ADR-0029, MSP:ADR-0075, IADR-0284, IADR-0323, IADR-0328, IADR-0332, #746:
// `platform.llmgateway.v1.LlmCompletion/Complete`（east-west gRPC・h2c）の輸送。
// **`LlmGateway:Grpc` を設定したときだけ**選ばれる。既定は REST（IADR-0332 決定 2）。
//
// 🔴 **縮退（`sent=false`）はエラーではない。** 越境拒否・プロバイダ未登録・上流不調は `RpcException` ではなく
// **応答**で返る（基盤の実装ガイド `docs/api/east-west-grpc.md`「3 つ目の面」の 🔴。REST の 200 ＋ `Sent=false`
// と同値）。したがって本クラスは `sent` を読まず、そのまま呼び出し元（判定器）へ渡す。
//
// 🔴 **`ModelUnavailable` を status から作らない。** 理由は `LlmFailureClassification.ClassifyGrpcStatus` の
// コメント（IADR-0332 決定 3）。
//
// 🔴 **打ち切りは握り潰さない。** deadline 超過・キャンセルは `OperationCanceledException` として上げ、
// 「タイムアウトか停止要求か」の判別は呼び出し元の既存 catch に委ねる（`ILlmCompletionTransport` の 🔴）。
public sealed class GrpcLlmCompletionTransport(
    LlmCompletion.LlmCompletionClient client,
    TimeSpan? defaultDeadline = null,
    TimeProvider? timeProvider = null)
    : ILlmCompletionTransport
{
    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;

    public async Task<LlmCompletionExchange> CompleteAsync(
        LlmCompletionCall call, TimeSpan? deadline = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(call);

        var options = new CallOptions(cancellationToken: cancellationToken);

        // IADR-0123 決定 1 → gRPC の deadline。要求単位の指定（報告書の種別別上限）が無ければ
        // 輸送の既定（`LlmGateway:TimeoutSeconds`。REST では `HttpClient.Timeout` が担っていた上限）を使う。
        // 🔴 既定も要求単位も無い場合は deadline を付けない —— 勝手な上限を作ると、構成で延ばしたつもりの
        // 呼び出しが黙って切られる。
        if ((deadline ?? defaultDeadline) is { } effective && effective > TimeSpan.Zero)
            options = options.WithDeadline(_time.GetUtcNow().UtcDateTime + effective);

        try
        {
            // 🔴 proto3 に null は無い。REST の null は「未指定」を表す空文字・0 へ写す（proto のコメント参照）。
            // 既定値の解釈（max_tokens=0 → 4096／model="" → 用途で選ぶ／confidentiality="" → restricted）は
            // **ゲートウェイ側**が持つ。呼び出し側で先回りして埋めない（既定値の単一情報源を 2 つにしない）。
            var response = await client.CompleteAsync(new CompleteRequest
            {
                Prompt = call.Prompt,
                MaxTokens = call.MaxTokens,
                Model = call.Model ?? string.Empty,
                Confidentiality = call.Confidentiality ?? string.Empty,
                Purpose = call.Purpose ?? string.Empty,
            }, options).ConfigureAwait(false);

            return LlmCompletionExchange.Completed(new LlmCompletionPayload(
                response.Text,
                response.Sent,
                // 空文字は「未報告」を表す。REST は null で来るので、呼び出し元の判定
                // （`LlmAssignmentEvaluator` / `LlmStopReasons`）へ同じ形で渡すために null へ戻す。
                NullIfEmpty(response.Model),
                NullIfEmpty(response.StopReason),
                response.InputTokens,
                response.OutputTokens));
        }
        catch (RpcException ex) when (IsCancellation(ex))
        {
            // deadline 超過・キャンセルは呼び出し元の catch が「タイムアウト / 停止要求」を判別する。
            throw new OperationCanceledException(ex.Status.Detail, ex, cancellationToken);
        }
        catch (RpcException ex)
        {
            return LlmCompletionExchange.Failed(
                LlmFailureClassification.ClassifyGrpcStatus((int)ex.StatusCode), ex.StatusCode.ToString());
        }
    }

    private static bool IsCancellation(RpcException ex) =>
        ex.StatusCode is StatusCode.DeadlineExceeded or StatusCode.Cancelled;

    private static string? NullIfEmpty(string? value) => string.IsNullOrEmpty(value) ? null : value;
}

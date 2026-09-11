namespace AiStockTrading.Shared.Contracts.Llm;

// NFR, FR-04, FR-06, MSP:ADR-0029, IADR-0284, IADR-0328, IADR-0332, #746:
// LLM ゲートウェイのテキスト生成（一括）を「送る」部分だけ抜き出した継ぎ目（seam）。
//
// 🔴 **輸送を足すために判定器を 2 つにしない。** 呼び出し元 2 サービス
// （`HttpLlmCompletionClient` / `HttpReportNarrativeDrafter`）の中身は
// ①要求を組む → ②送る → ③応答を解釈する、の 3 段で、**②だけが輸送に依る**。
// ③（`Sent=false` の縮退・`stopReason`・割当照合・費用計測・Hold / プレースホルダの振り分け）は
// REST でも gRPC でも同じでなければならない。gRPC 用の呼び出し元クラスを別に作ると、
// 片方だけ直る事故が構造的に入る（基盤の実装ガイド `docs/api/east-west-grpc.md` も
// 「判定器を 2 つにしない」と書く）。
//
// 🔴 **キャンセルとタイムアウトは輸送で握り潰さない。** 呼び出し元は
// `catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)` で
// 「タイムアウト（縮退してよい）」と「停止要求（伝播すべき）」を分けており、この判別には
// **呼び出し元だけが持つ外側のトークン**が要る（報告書は要求単位の CTS を linked で被せるため、
// 輸送へ渡るトークンは既に「タイムアウトでも立つ」）。輸送は
// **打ち切りを `OperationCanceledException` として上げるところまで**を担い、意味づけは呼び出し元へ返す。
// 同じ理由で伝送の例外（通信断・DNS 失敗）も握り潰さない —— 呼び出し元の既存の catch がそのまま効く。
//
// **実装は 2 つだけ**: `RestLlmCompletionTransport`（現行・既定）と `GrpcLlmCompletionTransport`
// （`LlmGateway:Grpc` を設定したときだけ）。REST は撤去まで並走する（IADR-0284 決定 5 の段 6）。
public interface ILlmCompletionTransport
{
    /// <summary>生成を 1 回要求する。</summary>
    /// <param name="call">要求。</param>
    /// <param name="deadline">
    /// この呼び出しの上限（要求単位）。
    /// 🔴 **輸送ごとに使い方が違う。** REST は呼び出し元が既に <c>CancellationTokenSource</c> で打ち切っており
    /// （＋ <c>HttpClient.Timeout</c> が多層防御の上限）、この値を見ない。gRPC はこれを
    /// <c>CallOptions.Deadline</c> へ写す —— deadline は**サーバ側へも伝播する**ため、
    /// クライアントで待つのをやめるだけの CTS より強い（IADR-0123 決定 1 を gRPC へ写す点。IADR-0332 決定 4）。
    /// <c>null</c> なら輸送の既定（gRPC は構成の上限）に従う。
    /// </param>
    /// <param name="cancellationToken">打ち切り。**呼び出し元へ <c>OperationCanceledException</c> として伝播する。**</param>
    Task<LlmCompletionExchange> CompleteAsync(
        LlmCompletionCall call, TimeSpan? deadline = null, CancellationToken cancellationToken = default);
}

/// <summary>
/// 輸送に依らない生成要求。REST の <c>CompletionApiRequest</c>／gRPC の <c>CompleteRequest</c> と 1 対 1。
/// </summary>
/// <param name="Prompt">送信する本文。</param>
/// <param name="MaxTokens">思考トークンと本文の合算上限（IADR-0101）。</param>
/// <param name="Model">希望モデル。<c>null</c>＝未指定（ゲートウェイが用途で選ぶ）。</param>
/// <param name="Confidentiality">入力の最高機密区分。未指定は安全側（restricted）。</param>
/// <param name="Purpose">用途。費用の計上区分・割当モデルの照合はこの値で引かれる（IADR-0212）。</param>
public sealed record LlmCompletionCall(
    string Prompt, int MaxTokens, string? Model, string? Confidentiality, string? Purpose);

/// <summary>
/// 輸送に依らない応答本体。REST の <c>CompletionApiResponse</c>／gRPC の <c>CompleteResponse</c> の
/// **呼び出し元が読む部分**（部分写像。欠落は既定値へ落ちるだけで安全側は崩れない）。
/// </summary>
/// <param name="Sent">
/// <c>false</c> は縮退（越境拒否・プロバイダ未登録・上流不調）。
/// 🔴 **縮退はエラーではない** —— REST は 200 ＋ <c>Sent=false</c>、gRPC も**応答**で返る
/// （基盤の実装ガイド「縮退はエラーではない」）。
/// </param>
public sealed record LlmCompletionPayload(
    string? Text, bool Sent, string? Model, string? StopReason, int? InputTokens, int? OutputTokens);

/// <summary>輸送の結果の種別。**打ち切り・伝送の例外はここに無い**（例外として呼び出し元へ上がる）。</summary>
public enum LlmTransportOutcome
{
    /// <summary>応答が届いた（<c>Sent=false</c> の縮退を含む）。<see cref="LlmCompletionExchange.Payload"/> が非 null。</summary>
    Completed,

    /// <summary>呼び出し先が失敗を返した（REST の非 2xx／gRPC の <c>RpcException</c>）。分類は <see cref="LlmCompletionExchange.Failure"/>。</summary>
    Failed,

    /// <summary>応答が解釈できない（不正 JSON・JSON null・想定外の形式）。伝送の失敗とは区別して記録する（IADR-0104 決定 3）。</summary>
    Malformed,
}

/// <summary>輸送 1 回の結果。</summary>
/// <param name="Outcome">結果の種別。</param>
/// <param name="Payload"><see cref="LlmTransportOutcome.Completed"/> のときだけ非 null。</param>
/// <param name="Failure">
/// <see cref="LlmTransportOutcome.Failed"/> のときの分類（IADR-0323 決定 3）。
/// 🔴 <see cref="LlmFailureKind.Unauthorized"/> と <see cref="LlmFailureKind.ModelUnavailable"/> を混ぜない。
/// </param>
/// <param name="Detail">ログに出す状態（REST は HTTP status、gRPC は status コード名）。</param>
/// <param name="Error">応答を解釈できなかった例外（<see cref="LlmTransportOutcome.Malformed"/>）。</param>
public sealed record LlmCompletionExchange(
    LlmTransportOutcome Outcome,
    LlmCompletionPayload? Payload = null,
    LlmFailureKind Failure = LlmFailureKind.Other,
    string? Detail = null,
    Exception? Error = null)
{
    public static LlmCompletionExchange Completed(LlmCompletionPayload payload) =>
        new(LlmTransportOutcome.Completed, payload);

    public static LlmCompletionExchange Failed(LlmFailureKind failure, string detail) =>
        new(LlmTransportOutcome.Failed, Failure: failure, Detail: detail);

    public static LlmCompletionExchange Malformed(Exception? error = null) =>
        new(LlmTransportOutcome.Malformed, Error: error);
}

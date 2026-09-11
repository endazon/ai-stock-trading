using System.Globalization;
using System.Net.Http.Json;
using System.Text.Json;

namespace AiStockTrading.Shared.Contracts.Llm;

// NFR, FR-04, FR-06, IADR-0061, IADR-0071, IADR-0104, IADR-0323, IADR-0332, #746:
// `POST /complete`（REST）の輸送。**現行の既定**であり、gRPC 化後も撤去まで並走する（IADR-0284 決定 5 の段 6）。
//
// 🔴 **本クラスは「送って、届いたものを素直に渡す」だけである。** `Sent=false` の縮退・`stopReason`・
// 割当照合・費用計測・Hold / プレースホルダの振り分けは呼び出し元（判定器）が持つ —— 輸送ごとに
// 判定が分かれないようにするための分担である（`ILlmCompletionTransport` の 🔴 を参照）。
//
// 分類（非 2xx）は `LlmFailureClassification.Classify` をそのまま使う（語彙の単一情報源）。
// s2s トークンは名前付き HttpClient に挿した `ServiceTokenHandler` が付ける（IADR-0323 決定 1）ため、
// 本クラスは資格情報を知らない。
public sealed class RestLlmCompletionTransport(HttpClient httpClient) : ILlmCompletionTransport
{
    /// <summary>基盤 LlmGateway の一括生成エンドポイント（相対パス。BaseAddress は配線側が与える）。</summary>
    public const string CompletePath = "/complete";

    public async Task<LlmCompletionExchange> CompleteAsync(
        LlmCompletionCall call, TimeSpan? deadline = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(call);

        // deadline は見ない。REST では呼び出し元が要求単位の CTS で打ち切っており（報告書の種別別上限・
        // IADR-0123 決定 1）、`HttpClient.Timeout` が多層防御の上限として残る。ここで二重に CTS を張ると
        // 「どちらで切られたか」がログから読めなくなる（IADR-0123 決定 5 が残した秒数の意味が壊れる）。
        var request = new CompletionRequest(
            call.Prompt, call.MaxTokens, call.Model, call.Confidentiality, call.Purpose);
        using var response = await httpClient
            .PostAsJsonAsync(CompletePath, request, cancellationToken)
            .ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            var status = (int)response.StatusCode;
            return LlmCompletionExchange.Failed(
                LlmFailureClassification.Classify(status), status.ToString(CultureInfo.InvariantCulture));
        }

        CompletionResponse? dto;
        try
        {
            dto = await response.Content
                .ReadFromJsonAsync<CompletionResponse>(cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException)
        {
            return LlmCompletionExchange.Malformed(ex);
        }

        // JSON null（本文が "null"）も解釈不能として扱う。伝送は成立しているため「例外」ではない。
        return dto is null
            ? LlmCompletionExchange.Malformed()
            : LlmCompletionExchange.Completed(new LlmCompletionPayload(
                dto.Text, dto.Sent, dto.Model, dto.StopReason, dto.InputTokens, dto.OutputTokens));
    }

    // POST /complete の要求（基盤 LlmGateway CompletionApiRequest 相当・camelCase JSON）。
    private sealed record CompletionRequest(
        string Prompt, int MaxTokens, string? Model, string? Confidentiality, string? Purpose);

    // POST /complete の応答（CompletionApiResponse の必要部分）。**部分写像**であり、欠落しても
    // 既定値へ落ちるだけで安全側は崩れない（IADR-0104 / IADR-0219 の非破壊の扱いを踏襲）。
    private sealed record CompletionResponse(
        string? Text, bool Sent, string? Model, string? StopReason = null,
        int? InputTokens = null, int? OutputTokens = null);
}

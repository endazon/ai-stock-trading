using System.Net.Http.Json;
using AiStockTrading.TradeDecision.Application.Ports;
using Microsoft.Extensions.Logging;

namespace AiStockTrading.TradeDecision.Worker.Composable.Adapters;

// #79, FR-04, ADR-0003, IADR-0017/0039: 実 LLM 補完を platform LLM ゲートウェイ（POST /complete）へ委譲する。
// ADR-0010（platform LLM ゲートウェイの越境ルーティング。本リポの FR-11=監査ログとは別物のため ID を使わず ADR で示す）:
// confidentiality/purpose を載せて送信先を判定させる（送信可否・モデル選択はゲートウェイ側）。
// フェイルセーフ（IADR-0017 の安全既定と一致）: 送信拒否（Sent=false）・非 2xx・例外・タイムアウト・空/不正応答は
// Hold（取引しない）に倒す。判断パーサはこの JSON を Hold として解釈する。
internal sealed class HttpLlmCompletionClient(
    HttpClient httpClient,
    ILogger<HttpLlmCompletionClient> logger,
    string confidentiality,
    string purpose)
    : ILlmCompletionClient
{
    private const string HoldFallback = """{"action":"Hold","rationale":"LLM ゲートウェイ送信不可のため見送り"}""";

    public async Task<string> CompleteAsync(string prompt, string? model = null, CancellationToken cancellationToken = default)
    {
        try
        {
            var request = new CompletionRequest(prompt, MaxTokens: 1024, model, confidentiality, purpose);
            using var response = await httpClient
                .PostAsJsonAsync("/complete", request, cancellationToken)
                .ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                logger.LogWarning("LLM ゲートウェイ /complete が非 2xx（{Status}）。取引しない安全側（Hold）に倒します。",
                    (int)response.StatusCode);
                return HoldFallback;
            }

            var dto = await response.Content
                .ReadFromJsonAsync<CompletionResponse>(cancellationToken)
                .ConfigureAwait(false);

            // Sent=false は機密区分による送信拒否（縮退）。空応答・欠落も取引しない安全側に倒す。
            if (dto is null || !dto.Sent || string.IsNullOrWhiteSpace(dto.Text))
            {
                logger.LogWarning("LLM ゲートウェイが送信不可/空応答（Sent={Sent}）。取引しない安全側（Hold）に倒します。",
                    dto?.Sent);
                return HoldFallback;
            }

            return dto.Text;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // タイムアウト（呼び出し側キャンセルではない）＝ゲートウェイ応答遅延。取引しない安全側に倒す。
            logger.LogWarning("LLM ゲートウェイ /complete がタイムアウト。取引しない安全側（Hold）に倒します。");
            return HoldFallback;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "LLM ゲートウェイ /complete で例外。取引しない安全側（Hold）に倒します。");
            return HoldFallback;
        }
    }

    // POST /complete の要求（platform LlmGateway CompletionApiRequest 相当・camelCase JSON）。
    private sealed record CompletionRequest(string Prompt, int MaxTokens, string? Model, string? Confidentiality, string? Purpose);

    // POST /complete の応答（CompletionApiResponse の必要部分）。Sent=false は送信拒否（縮退）。
    private sealed record CompletionResponse(string? Text, bool Sent);
}

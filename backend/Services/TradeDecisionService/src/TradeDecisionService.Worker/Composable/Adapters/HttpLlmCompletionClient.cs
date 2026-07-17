using System.Net.Http.Json;
using AiStockTrading.TradeDecision.Application.Ports;
using Microsoft.Extensions.Logging;

namespace AiStockTrading.TradeDecision.Worker.Composable.Adapters;

// #79, FR-04, ADR-0003, IADR-0017/0039: 実 LLM 補完を platform LLM ゲートウェイ（POST /complete）へ委譲する。
// ADR-0010（platform LLM ゲートウェイの越境ルーティング。本リポの FR-11=監査ログとは別物のため ID を使わず ADR で示す）:
// confidentiality/purpose を載せて送信先を判定させる（送信可否・モデル選択はゲートウェイ側）。
// フェイルセーフ（IADR-0017 の安全既定と一致）: 送信拒否（Sent=false）・非 2xx・例外・タイムアウト・空/不正応答は
// Hold（取引しない）に倒す。判断パーサはこの JSON を Hold として解釈する。
// #79, IADR-0055 決定3: 成功応答のトークンを ILlmUsageReporter へ渡す（計測点は egress）。既定 NoOp＝publish しない。
// #11, FR-11, IADR-0061 決定1: logPrompts=true でプロンプト本文と LLM 生出力を全量記録する（判断根拠の事後再構成）。
// プロンプトは保有ポジション・資金残枠等の機微を含むため既定オフ＝記録しない（最小権限）。
internal sealed class HttpLlmCompletionClient(
    HttpClient httpClient,
    ILogger<HttpLlmCompletionClient> logger,
    string confidentiality,
    string purpose,
    ILlmUsageReporter usageReporter,
    bool logPrompts = false)
    : ILlmCompletionClient
{
    private const string HoldFallback = """{"action":"Hold","rationale":"LLM ゲートウェイ送信不可のため見送り"}""";

    public async Task<string> CompleteAsync(string prompt, string? model = null, CancellationToken cancellationToken = default)
    {
        try
        {
            // FR-11, IADR-0061 決定1: 送信前にプロンプト全量を記録する（応答前に失敗しても入力は残る）。
            if (logPrompts)
                logger.LogInformation("LLM 要求: model={Model} prompt={Prompt}", model, prompt);

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

            // FR-11, IADR-0061 決定1: LLM の生出力を全量記録する（構造化解析前＝パーサが Hold へ丸める前の原文）。
            if (logPrompts)
                logger.LogInformation("LLM 応答: model={Model} tokens={Input}/{Output} text={Text}",
                    dto.Model, dto.InputTokens ?? 0, dto.OutputTokens ?? 0, dto.Text);

            // #79, IADR-0055: 成功応答のトークンを費用計測へ渡す。計測は best-effort＝失敗しても LLM 応答は壊さない
            // （費用計測の不調で取引判断を Hold に倒すのは過剰。計上漏れは at-least-once 再配信で緩和される）。
            try
            {
                await usageReporter
                    .ReportAsync(new LlmUsage(dto.InputTokens ?? 0, dto.OutputTokens ?? 0), cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "LLM 費用計測の報告に失敗しました（応答は継続）。");
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
    // InputTokens/OutputTokens は費用計測の入力（#79・IADR-0055）。欠落時は 0 として扱う。
    // Model はゲートウェイが実際に選択したモデル（要求の Model は希望値であり、越境ルーティングで変わり得る）。
    // FR-11 の全量ログで「どのモデルが答えたか」を残すために受ける（IADR-0061 決定1）。
    // 実基盤の契約（microservices-platform リポジトリ
    // src/platform/backend/Shared/Platform.Shared.Contracts/Dtos/CompletionDto.cs の CompletionApiResponse。
    // 2026-07-17 時点で Text/Model/InputTokens/OutputTokens/Sent/Endpoint/RoutingReason）に Model は実在する。
    // 本リポジトリからは参照できない外部契約のため、追随漏れの検出はここの写像ではなく実結線時の疎通に委ねる。
    // 本 record は必要部分のみを受ける部分写像であり、欠落しても既定値に落ちるだけで安全側は崩れない。
    private sealed record CompletionResponse(string? Text, bool Sent, int? InputTokens, int? OutputTokens, string? Model);
}

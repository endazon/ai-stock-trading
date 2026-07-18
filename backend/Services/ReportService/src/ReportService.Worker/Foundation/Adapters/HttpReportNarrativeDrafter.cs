using System.Net.Http.Json;
using AiStockTrading.Report.Application.Ports;
using AiStockTrading.Report.Application.Services;
using Microsoft.Extensions.Logging;

namespace AiStockTrading.Report.Worker.Foundation.Adapters;

// FR-06/16, IADR-0071 決定1, ADR-0003: 報告書の散文ドラフトを platform LLM ゲートウェイ（POST /complete）へ委譲する実装。
// IADR-0061（#11 の実 LLM 接続）と同形の安全既定・fail-safe に倣う:
// - プロンプトは純関数 ReportNarrativePromptBuilder で構築（散文のみ・数値は再計算/改変しない＝数値はコード集計が権威・FR-16）。
// - 送信拒否（Sent=false）・非 2xx・タイムアウト・空/不正応答・例外は「プレースホルダ散文」へ倒す。取引判断の Hold と異なり
//   報告書は発注を伴わないため、安全側＝捏造しない定型散文（数値には一切関与しない）。
// - ADR-0010: /complete は匿名エンドポイントゆえ s2s トークンは付けない。リトライはゲートウェイ側一元化に委ね重ねない。
// - IADR-0061 決定1: logPrompts=true でプロンプト本文と LLM 生出力を全量記録する。既定オフ＝機微を既定でログ基盤へ流さない。
internal sealed class HttpReportNarrativeDrafter(
    HttpClient httpClient,
    ILogger<HttpReportNarrativeDrafter> logger,
    string confidentiality,
    string purpose,
    bool logPrompts = false)
    : IReportNarrativeDrafter
{
    public async Task<string> DraftNarrativeAsync(ReportNarrativeContext context, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        var prompt = ReportNarrativePromptBuilder.Build(context);

        try
        {
            if (logPrompts)
                logger.LogInformation("報告書散文 LLM 要求: kind={Kind} periodKey={PeriodKey} prompt={Prompt}",
                    context.Kind, context.PeriodKey, prompt);

            var request = new CompletionRequest(prompt, MaxTokens: 1024, Model: null, confidentiality, purpose);
            using var response = await httpClient
                .PostAsJsonAsync("/complete", request, cancellationToken)
                .ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                logger.LogWarning("報告書散文 LLM /complete が非 2xx（{Status}）。プレースホルダ散文に倒します。", (int)response.StatusCode);
                return ReportNarrativeDefaults.PlaceholderText;
            }

            var dto = await response.Content
                .ReadFromJsonAsync<CompletionResponse>(cancellationToken)
                .ConfigureAwait(false);

            // Sent=false は機密区分による送信拒否（縮退）。空応答・欠落もプレースホルダ散文に倒す。
            if (dto is null || !dto.Sent || string.IsNullOrWhiteSpace(dto.Text))
            {
                logger.LogWarning("報告書散文 LLM が送信不可/空応答（Sent={Sent}）。プレースホルダ散文に倒します。", dto?.Sent);
                return ReportNarrativeDefaults.PlaceholderText;
            }

            if (logPrompts)
                logger.LogInformation("報告書散文 LLM 応答: model={Model} text={Text}", dto.Model, dto.Text);

            return dto.Text;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning("報告書散文 LLM /complete がタイムアウト。プレースホルダ散文に倒します。");
            return ReportNarrativeDefaults.PlaceholderText;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "報告書散文 LLM /complete で例外。プレースホルダ散文に倒します。");
            return ReportNarrativeDefaults.PlaceholderText;
        }
    }

    // POST /complete の要求（platform LlmGateway CompletionApiRequest 相当・camelCase JSON）。
    private sealed record CompletionRequest(string Prompt, int MaxTokens, string? Model, string? Confidentiality, string? Purpose);

    // POST /complete の応答（CompletionApiResponse の必要部分）。Sent=false は送信拒否（縮退）。
    // 本 record は必要部分のみを受ける部分写像であり、欠落しても既定値に落ちるだけで安全側は崩れない。
    private sealed record CompletionResponse(string? Text, bool Sent, string? Model);
}

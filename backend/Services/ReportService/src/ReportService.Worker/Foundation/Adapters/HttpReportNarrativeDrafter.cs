using System.Net.Http.Json;
using System.Text.Json;
using AiStockTrading.Shared.Contracts.Llm;
using AiStockTrading.Report.Application.Ports;
using AiStockTrading.Report.Application.Services;
using AiStockTrading.Report.Domain;
using Microsoft.Extensions.Logging;

namespace AiStockTrading.Report.Worker.Foundation.Adapters;

// FR-06/16, IADR-0071 決定1, ADR-0003: 報告書の散文ドラフトを platform LLM ゲートウェイ（POST /complete）へ委譲する実装。
// IADR-0061（#11 の実 LLM 接続）と同形の安全既定・fail-safe に倣う:
// - プロンプトは純関数 ReportNarrativePromptBuilder で構築（散文のみ・数値は再計算/改変しない＝数値はコード集計が権威・FR-16）。
// - 送信拒否（Sent=false）・非 2xx・タイムアウト・空/不正応答・例外は「プレースホルダ散文」へ倒す。取引判断の Hold と異なり
//   報告書は発注を伴わないため、安全側＝捏造しない定型散文（数値には一切関与しない）。
// - ADR-0010: /complete は匿名エンドポイントゆえ s2s トークンは付けない。リトライはゲートウェイ側一元化に委ね重ねない。
// - IADR-0061 決定1: logPrompts=true でプロンプト本文と LLM 生出力を全量記録する。既定オフ＝機微を既定でログ基盤へ流さない。
// - IADR-0120 決定1/2: purpose は要求ごとに種別（context.Kind）から決める。purposeOverride は構成
//   LlmGateway:Purpose の明示設定で、指定時は全種別へ適用する（既存デプロイの非破壊）。
// - IADR-0122 決定1, #308: タイムアウトも要求ごとに種別から決める（timeoutFor）。種別ごとに別モデルが
//   割り当たる（IADR-0120）以上、サービス共通の 1 本では週報・月報が構造的に間に合わない。
//   timeoutFor 未注入なら従来どおり HttpClient.Timeout のみが効く（非破壊）。
internal sealed class HttpReportNarrativeDrafter(
    HttpClient httpClient,
    ILogger<HttpReportNarrativeDrafter> logger,
    string confidentiality,
    string? purposeOverride,
    bool logPrompts = false,
    Func<ReportKind, TimeSpan>? timeoutFor = null)
    : IReportNarrativeDrafter
{
    public async Task<string> DraftNarrativeAsync(ReportNarrativeContext context, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        var prompt = ReportNarrativePromptBuilder.Build(context);

        // IADR-0120 決定1, #291: 報告書は方針階層（月報→週報→日報→取引）をなす方針書であり、上位ほど難度が高い。
        // 種別ごとの purpose を送ることで基盤の Llm:Routing:PurposeModels が種別ごとのモデルを解決する。
        // 従来は単一の固定値を送っており、基盤に該当エントリが無いため 3 種別すべてが DefaultModel へ着地していた。
        var purpose = string.IsNullOrWhiteSpace(purposeOverride)
            ? ReportNarrativePurpose.For(context.Kind)
            : purposeOverride;

        // IADR-0122 決定1, #308: 種別ごとの上限を要求単位で適用する。呼び出し側の停止要求と linked にすることで、
        // 「タイムアウト（縮退してよい）」と「停止要求（伝播すべき）」の区別は下の catch の条件式がそのまま担う。
        var timeout = timeoutFor?.Invoke(context.Kind);
        using var timeoutCts = timeout is null ? null : CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts?.CancelAfter(timeout!.Value);
        var requestToken = timeoutCts?.Token ?? cancellationToken;

        try
        {
            if (logPrompts)
                logger.LogInformation("報告書散文 LLM 要求: kind={Kind} periodKey={PeriodKey} prompt={Prompt}",
                    context.Kind, context.PeriodKey, prompt);

            // IADR-0101, MSP/ADR-0025: MaxTokens は思考トークンと本文の合算上限（Opus 5 等は thinking が既定有効）。
            // 1024 のままだと思考が上限を食い、途中で切れた文章がそのまま成果物になる（安全網なし）。
            // IADR-0120 決定1: Model は引き続き明示しない（null）＝モデルの決定権は基盤の LlmRouter に残す。
            // AST がモデル ID を持つと NonZdrModels による除外や版数改定へ追随できず、許可一覧との整合も崩れる。
            var request = new CompletionRequest(prompt, MaxTokens: 4096, Model: null, confidentiality, purpose);
            using var response = await httpClient
                .PostAsJsonAsync("/complete", request, requestToken)
                .ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                logger.LogWarning("報告書散文 LLM /complete が非 2xx（{Status}）。プレースホルダ散文に倒します。", (int)response.StatusCode);
                return ReportNarrativeDefaults.PlaceholderText;
            }

            // Sent=false は機密区分による送信拒否（縮退）。空応答・欠落もプレースホルダ散文に倒す。
            // #247, IADR-0104 決定3: 縮退の理由（応答不正 / 送信拒否 / 拒否 / 空応答 / 上限到達）を区別して記録する。
            CompletionResponse? dto;
            try
            {
                dto = await response.Content
                    .ReadFromJsonAsync<CompletionResponse>(requestToken)
                    .ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is JsonException or NotSupportedException)
            {
                logger.LogWarning(ex, "報告書散文 LLM /complete の応答を解釈できません（不正 JSON・想定外の形式）。プレースホルダ散文に倒します。");
                return ReportNarrativeDefaults.PlaceholderText;
            }

            if (dto is null)
            {
                logger.LogWarning("報告書散文 LLM /complete の応答が空です（JSON null）。プレースホルダ散文に倒します。");
                return ReportNarrativeDefaults.PlaceholderText;
            }

            if (!dto.Sent)
            {
                logger.LogWarning("報告書散文 LLM が送信不可（Sent=false・機密区分による縮退）。プレースホルダ散文に倒します。");
                return ReportNarrativeDefaults.PlaceholderText;
            }

            // IADR-0061 決定1: 生出力の全量記録は、以降で破棄し得る本文も含めて拒否・空応答の判定より前に行う。
            if (logPrompts)
                logger.LogInformation("報告書散文 LLM 応答: model={Model} stopReason={StopReason} text={Text}",
                    dto.Model, dto.StopReason, dto.Text);

            // #247, IADR-0104 決定2: 拒否（安全性分類器による停止）は**本文を読む前に**評価し、本文が非空でも破棄する。
            // 拒否された断片が報告書の成果物になることを、上流の破棄実装に依存せず防ぐ（多層防御）。
            if (LlmStopReasons.IsRefusal(dto.StopReason))
            {
                logger.LogWarning(
                    "報告書散文 LLM が要求を拒否しました（stopReason={StopReason} textLength={TextLength}）。本文を破棄し、プレースホルダ散文に倒します。",
                    dto.StopReason, dto.Text?.Length ?? 0);
                return ReportNarrativeDefaults.PlaceholderText;
            }

            if (string.IsNullOrWhiteSpace(dto.Text))
            {
                logger.LogWarning("報告書散文 LLM の応答本文が空です（stopReason={StopReason}）。プレースホルダ散文に倒します。",
                    dto.StopReason);
                return ReportNarrativeDefaults.PlaceholderText;
            }

            // IADR-0104 決定5, IADR-0101: 上限到達は拒否ではなく劣化。本文は破棄せず（途中で切れることの観測を残し）、
            // 劣化として記録するにとどめる。
            if (LlmStopReasons.IsMaxTokens(dto.StopReason))
                logger.LogWarning(
                    "報告書散文 LLM の応答が出力上限に到達しました（stopReason={StopReason}）。散文が途中で切れている可能性があります。",
                    dto.StopReason);

            return dto.Text;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // IADR-0122 決定5: どの上限で切られたのかを残す（種別ごとに上限が変わるため、秒数が無いと切り分けできない）。
            // timeout が null（種別別の上限が無い構成）のときは HttpClient 自体の上限で切られている。
            logger.LogWarning(
                "報告書散文 LLM /complete がタイムアウト（kind={Kind} timeoutSeconds={TimeoutSeconds}）。プレースホルダ散文に倒します。",
                context.Kind, (timeout ?? httpClient.Timeout).TotalSeconds);
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
    // #247, IADR-0104: StopReason は送信が成立した場合のモデル側の終了理由（Sent とは独立した軸）。
    // 未設定（null）＝上流未更新・未対応プロバイダでは従来どおりの分岐へ素通りする（非破壊）。
    private sealed record CompletionResponse(string? Text, bool Sent, string? Model, string? StopReason = null);
}

using AiStockTrading.Shared.Contracts.Llm;
using AiStockTrading.Shared.Contracts.Logging;
using Microsoft.Extensions.Logging;
using ReportService.Domain;
using ReportService.Features.Reports;

namespace ReportService.Infrastructure.ExternalServices;

// FR-07, FR-14, UC-03, ADR-0003, #1016, IADR-0431 決定 2・3: 方針の改訂案を platform LLM ゲートウェイで作る。
//
// 輸送・purpose・費用の計上・割当逸脱の通知は散文ドラフト（HttpReportNarrativeDrafter）と同じ作法に揃える:
// - purpose は種別から（report-daily/weekly/monthly。IADR-0120）。構成 LlmGateway:Purpose の明示設定は全種別へ上書き。
//   新しい purpose を作らない——基盤の割当表に無い値は無音で DefaultModel へ落ちる（IADR-0120 の罠）。
// - 送信が成立した応答のトークンは本文の扱いとは独立に計上する（課金は発生している。IADR-0219）。
// - 禁止モデル（ZDR 非対応）の出力は成果物にしない（ADR-0015 / ADR-0017 決定1）。
//
// 🔴 散文ドラフトと違い、**失敗はプレースホルダへ倒さず「案なし」で返す**（IReportPolicyReviser の注記）。
public sealed class LlmReportPolicyReviser(
    ILlmCompletionTransport transport,
    ILogger<LlmReportPolicyReviser> logger,
    string confidentiality,
    string? purposeOverride,
    TimeSpan timeout,
    ILlmUsageReporter usageReporter,
    ILlmGovernanceReporter governanceReporter,
    bool logPrompts = false)
    : IReportPolicyReviser
{
    // 方針 2000 文字＋入れ替え案＋説明の JSON に、思考トークンの余裕を足した合算上限（IADR-0101）。
    private const int MaxTokens = 4096;

    public async Task<PolicyRevisionOutcome> ReviseAsync(
        PolicyRevisionContext context, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);

        var prompt = PolicyRevisionPromptBuilder.Build(context);
        var purpose = string.IsNullOrWhiteSpace(purposeOverride) ? ReportNarrativePurpose.For(context.Kind) : purposeOverride;

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(timeout);

        LlmModelUsage? modelUsage = null;
        try
        {
            if (logPrompts)
                logger.LogInformation("方針の改訂 LLM 要求: periodKey={PeriodKey} prompt={Prompt}",
                    LogSanitizer.Sanitize(context.PeriodKey), LogSanitizer.Sanitize(prompt));

            var exchange = await transport
                .CompleteAsync(new LlmCompletionCall(prompt, MaxTokens, Model: null, confidentiality, purpose), timeout, timeoutCts.Token)
                .ConfigureAwait(false);

            if (exchange.Outcome == LlmTransportOutcome.Failed)
            {
                if (exchange.Failure == LlmFailureKind.Unauthorized)
                    logger.LogWarning("方針の改訂 LLM が認可を拒否しました（{Status}）。LlmGateway:Auth を確認してください。", exchange.Detail);
                else
                    logger.LogWarning("方針の改訂 LLM の呼び出しが失敗しました（{Status}）。", exchange.Detail);

                return PolicyRevisionOutcome.Failed(PolicyRevisionFailure.CallFailed, "AI の呼び出しに失敗しました");
            }

            if (exchange.Outcome == LlmTransportOutcome.Malformed)
            {
                logger.LogWarning(exchange.Error, "方針の改訂 LLM の応答を解釈できませんでした。");
                return PolicyRevisionOutcome.Failed(PolicyRevisionFailure.InvalidOutput, "AI の応答を解釈できませんでした");
            }

            var dto = exchange.Payload!;
            if (!dto.Sent)
            {
                logger.LogWarning("方針の改訂 LLM が送信不可（Sent=false・機密区分による縮退）でした。");
                return PolicyRevisionOutcome.Failed(PolicyRevisionFailure.Refused, "AI へ送信できませんでした（縮退中）");
            }

            if (logPrompts)
                logger.LogInformation("方針の改訂 LLM 応答: model={Model} stopReason={StopReason} text={Text}",
                    dto.Model, dto.StopReason, LogSanitizer.Sanitize(dto.Text));

            // 費用の計上（best-effort。失敗しても改訂は続ける）。
            // FR-14, ADR-0042 決定 3, #1024, IADR-0432 決定 2: 計上区分は `policy-revision` へ付け替える（用途キーは
            // 報告書と同じ＝モデル割当は変えない）。月次 LLM 上限の対象外で、月報 §7 に回数と費用を別の行で載せる。
            try
            {
                await usageReporter
                    .ReportAsync(
                        new LlmUsage(LlmPurposes.PolicyRevision, dto.InputTokens ?? 0, dto.OutputTokens ?? 0, dto.Model),
                        timeoutCts.Token)
                    .ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "方針の改訂の LLM 費用計測の報告に失敗しました（改訂は継続）。");
            }

            var evaluation = LlmAssignmentEvaluator.Evaluate(purpose, dto.Model);
            modelUsage = new LlmModelUsage(purpose, evaluation.ExpectedModel, evaluation.EffectiveModel, evaluation.Outcome.ToString());

            if (evaluation.Outcome != LlmAssignmentOutcome.Primary)
            {
                try
                {
                    await governanceReporter.FallbackFiredAsync(evaluation, purpose, timeoutCts.Token).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    logger.LogWarning(ex, "方針の改訂の割当逸脱の記録に失敗しました（改訂は継続）。");
                }
            }

            if (evaluation.Outcome == LlmAssignmentOutcome.Forbidden)
            {
                logger.LogWarning("方針の改訂が本システムで使用しないモデルで生成されました（model={Model}）。案を捨てます。",
                    evaluation.EffectiveModel);
                return PolicyRevisionOutcome.Failed(
                    PolicyRevisionFailure.Refused, "本システムで使用しないモデルが応答したため、案を捨てました", modelUsage);
            }

            if (LlmStopReasons.IsRefusal(dto.StopReason))
            {
                logger.LogWarning("方針の改訂 LLM が要求を拒否しました（stopReason={StopReason}）。", dto.StopReason);
                return PolicyRevisionOutcome.Failed(PolicyRevisionFailure.Refused, "AI が要求を拒否しました", modelUsage);
            }

            // 上限到達は途中で切れた JSON になり得る——下の検証が捨てる（部分採用しない）。
            var parsed = PolicyRevisionProposalParser.Parse(dto.Text);
            if (!parsed.IsValid)
            {
                logger.LogWarning(
                    "方針の改訂 LLM の出力が案の形式に合いませんでした（理由={Reason}・stopReason={StopReason}・textLength={Length}）。",
                    parsed.Reason, dto.StopReason, dto.Text?.Length ?? 0);
                return PolicyRevisionOutcome.Failed(PolicyRevisionFailure.InvalidOutput, parsed.Reason!, modelUsage);
            }

            return PolicyRevisionOutcome.Proposed(parsed.Proposal!, modelUsage);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning("方針の改訂 LLM がタイムアウトしました（{Seconds} 秒）。", timeout.TotalSeconds);
            return PolicyRevisionOutcome.Failed(
                PolicyRevisionFailure.TimedOut, $"AI の応答が {timeout.TotalSeconds:0} 秒以内に返りませんでした", modelUsage);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "方針の改訂 LLM の呼び出しで例外が発生しました。");
            return PolicyRevisionOutcome.Failed(PolicyRevisionFailure.CallFailed, "AI の呼び出しに失敗しました", modelUsage);
        }
    }
}

// FR-07, #1016, IADR-0431 決定 2: LLM が構成されていないときの実装。**案なし**を返す（定型の方針文を作らない）。
public sealed class UnavailableReportPolicyReviser : IReportPolicyReviser
{
    public const string Message = "AI（LLM ゲートウェイ）が構成されていないため、方針の改訂案を作れません";

    public Task<PolicyRevisionOutcome> ReviseAsync(PolicyRevisionContext context, CancellationToken cancellationToken = default) =>
        Task.FromResult(PolicyRevisionOutcome.Failed(PolicyRevisionFailure.Unavailable, Message));
}

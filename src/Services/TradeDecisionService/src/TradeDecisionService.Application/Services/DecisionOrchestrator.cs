using AiStockTrading.TradeDecision.Application.Ports;
using AiStockTrading.TradeDecision.Domain;
using Microsoft.Extensions.Logging;

namespace AiStockTrading.TradeDecision.Application.Services;

// FR-04, FR-11, ADR-0003, IADR-0037: 多数決・二段（一次スクリーニング→二次本判断）オーケストレーション。
// LLM 非決定性への対策として、二次は同一入力を VoteCount 回実行し DecisionAggregator で多数決を採る（L128）。
// 費用統制として、有効時は一次で軽量モデルの絞り込みを行い、Hold なら二次を呼ばず打ち切る（L129）。
// モデル選択（一次=軽量／二次=高性能）はポート引数でゲートウェイへ渡すのみ（実解決は後続・L34）。
public sealed class DecisionOrchestrator(
    ILlmCompletionClient llm,
    DecisionOrchestrationOptions options,
    ILogger logger)
{
    // screeningPromptFactory は一次スクリーニング時のみ評価する（既定＝スクリーニング無効の経路で無駄なプロンプト構築を避ける）。
    public async Task<OrchestratedDecision> DecideAsync(
        Func<string> screeningPromptFactory, string decisionPrompt, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(screeningPromptFactory);
        ArgumentNullException.ThrowIfNull(decisionPrompt);

        // 一次スクリーニング（軽量モデル・1 回）。Hold なら二次をスキップして打ち切る（費用統制）。
        if (options.EnableScreening)
        {
            var screenOutput = await llm.CompleteAsync(screeningPromptFactory(), options.PrimaryModel, cancellationToken)
                .ConfigureAwait(false);
            var screen = TradeDecisionParser.Parse(screenOutput);
            if (screen.Action == TradeAction.Hold)
            {
                logger.LogInformation("一次スクリーニングで見送り（二次判断をスキップ・費用統制）");
                return new OrchestratedDecision(LlmDecision.Hold, TotalVotes: 0, AgreementVotes: 0, ScreenedOut: true);
            }
        }

        // 二次本判断（高性能モデル）。同一入力を VoteCount 回実行し、各出力を解析して多数決で集約する。
        var votes = new List<LlmDecision>(options.VoteCount);
        for (var i = 0; i < options.VoteCount; i++)
        {
            var output = await llm.CompleteAsync(decisionPrompt, options.SecondaryModel, cancellationToken)
                .ConfigureAwait(false);
            votes.Add(TradeDecisionParser.Parse(output));
        }

        var aggregated = DecisionAggregator.Aggregate(votes);
        logger.LogInformation(
            "二次多数決: total={Total} agreement={Agreement} action={Action}",
            aggregated.TotalVotes, aggregated.AgreementVotes, aggregated.Decision.Action);

        return new OrchestratedDecision(
            aggregated.Decision, aggregated.TotalVotes, aggregated.AgreementVotes, ScreenedOut: false);
    }
}

// IADR-0037: オーケストレーション結果。Decision は下流サイジングへ、票数・スクリーニング可否は FR-11 監査ログへ。
// ScreenedOut=true は一次スクリーニングで打ち切ったこと（TotalVotes=0）を表す。
public sealed record OrchestratedDecision(
    LlmDecision Decision, int TotalVotes, int AgreementVotes, bool ScreenedOut);

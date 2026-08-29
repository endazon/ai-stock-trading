using AiStockTrading.Shared.Contracts.Llm;
using TradeDecisionService.Features.TradeDecision;
using TradeDecisionService.Domain;
using Microsoft.Extensions.Logging;

namespace TradeDecisionService.Features.TradeDecision;

// FR-04, FR-11, ADR-0003, IADR-0039: 多数決・二段（一次スクリーニング→二次本判断）オーケストレーション。
// LLM 非決定性への対策として、二次は同一入力を VoteCount 回実行し DecisionAggregator で多数決を採る（L128）。
// 費用統制として、有効時は一次で軽量モデルの絞り込みを行い、Hold なら二次を呼ばず打ち切る（L129）。
// モデル選択（一次=軽量／二次=高性能）はポート引数でゲートウェイへ渡すのみ（実解決は後続・L34）。
//
// 🔴 FR-04, ADR-0014, ADR-0017 決定2, #335, IADR-0212: **用途（purpose）も層ごとに分ける。**
// 割当（一次=claude-haiku-4-5／二次=claude-sonnet-5・LlmAssignments）も費用の計上区分も purpose で引かれるため、
// 両層が同じ purpose を名乗ると**一次の応答が二次の割当と照合されて必ず「割当外」になり、全サイクルが見送りへ倒れる**。
// モデルの希望値（options.PrimaryModel / SecondaryModel）だけを変えても、判定に使われるのは purpose の側である。
// 用途キーは計画（ADR-0017 決定1・01_architecture-overview §判断の二段化）が確定させた統制値であり、
// 構成で可変にしない —— 運用でずらせる形にすると、ずらした先で割当統制が無音で外れる。
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
            // IADR-0212: 用途は一次スクリーニング（軽量モデルの割当・費用も取引判断サイクルの一部）。
            var screenOutput = await llm
                .CompleteAsync(
                    screeningPromptFactory(), options.PrimaryModel, LlmPurposes.TradeDecisionScreening, cancellationToken)
                .ConfigureAwait(false);
            var screen = TradeDecisionParser.ParseDetailed(screenOutput);
            if (screen.Decision.Action == TradeAction.Hold)
            {
                // #247, IADR-0104 決定6: 一次で打ち切る場合も見送りの根拠（LLM 由来。拒否・空応答等）を保つ。
                // Hold は TradeDecisionMade を発行しないため、FR-11 ログが唯一の監査記録である。
                // #337（#290 吸収）, IADR-0248: **解析不能と見送りを区別して記録する。** どちらも打ち切り
                // （安全側・取引しない）だが、解析不能は出力の形の退行を示す信号であり、見送りに混ぜると
                // 監査から見えなくなる。
                if (screen.IsUnparseable)
                {
                    logger.LogWarning(
                        "一次スクリーニングの構造化出力が解析不能（見送りとは区別して記録・#290）: kind={Kind} detail={Detail}",
                        screen.Failure!.Kind, screen.Failure.Detail);
                }
                else
                {
                    logger.LogInformation(
                        "一次スクリーニングで見送り（二次判断をスキップ・費用統制）: rationale={Rationale}",
                        screen.Decision.Rationale);
                }

                return new OrchestratedDecision(
                    screen.Decision, TotalVotes: 0, AgreementVotes: 0, ScreenedOut: true,
                    UnparseableVotes: 0, ScreeningUnparseable: screen.IsUnparseable);
            }
        }

        // 二次本判断（高性能モデル）。同一入力を VoteCount 回実行し、各出力を解析して多数決で集約する。
        var votes = new List<LlmDecision>(options.VoteCount);
        var unparseableVotes = 0;
        for (var i = 0; i < options.VoteCount; i++)
        {
            // IADR-0212: 用途は本判断（claude-sonnet-5 ピン留め・フォールバック禁止・ADR-0017 決定2）。
            var output = await llm
                .CompleteAsync(decisionPrompt, options.SecondaryModel, LlmPurposes.TradeDecision, cancellationToken)
                .ConfigureAwait(false);
            var parsed = TradeDecisionParser.ParseDetailed(output);
            if (parsed.IsUnparseable)
            {
                // #290, IADR-0248: 解析不能票は Hold として多数決へ入れる（安全側・従来挙動）が、
                // 件数は見送りと区別して数え、FR-11 の記録へ出す。
                unparseableVotes++;
                logger.LogWarning(
                    "二次本判断の構造化出力が解析不能（Hold 票として扱う・#290）: vote={Vote}/{Total} kind={Kind} detail={Detail}",
                    i + 1, options.VoteCount, parsed.Failure!.Kind, parsed.Failure.Detail);
            }

            votes.Add(parsed.Decision);
        }

        var aggregated = DecisionAggregator.Aggregate(votes);
        logger.LogInformation(
            "二次多数決: total={Total} agreement={Agreement} action={Action} unparseable={Unparseable}",
            aggregated.TotalVotes, aggregated.AgreementVotes, aggregated.Decision.Action, unparseableVotes);

        return new OrchestratedDecision(
            aggregated.Decision, aggregated.TotalVotes, aggregated.AgreementVotes, ScreenedOut: false,
            UnparseableVotes: unparseableVotes, ScreeningUnparseable: false);
    }
}

// IADR-0039: オーケストレーション結果。Decision は下流サイジングへ、票数・スクリーニング可否は FR-11 監査ログへ。
// ScreenedOut=true は一次スクリーニングで打ち切ったこと（TotalVotes=0）を表す。
// #337（#290 吸収）, IADR-0248: UnparseableVotes は二次本判断のうち構造化出力を解析できなかった票数、
// ScreeningUnparseable は一次の打ち切りが「解析不能」由来だったこと（見送りとの区別・FR-11 記録用）。
public sealed record OrchestratedDecision(
    LlmDecision Decision, int TotalVotes, int AgreementVotes, bool ScreenedOut,
    int UnparseableVotes = 0, bool ScreeningUnparseable = false);

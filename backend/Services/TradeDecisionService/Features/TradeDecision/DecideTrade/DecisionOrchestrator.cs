using AiStockTrading.Shared.Contracts.Llm;
using AiStockTrading.Shared.Contracts.Logging;
using TradeDecisionService.Features.TradeDecision;
using TradeDecisionService.Domain;
using Microsoft.Extensions.Logging;

namespace TradeDecisionService.Features.TradeDecision.DecideTrade;

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
    // 🔴 FR-04, FR-10, #1187, IADR-0248: signedHeldQuantity は判断プロンプトへ渡したのと同じ照会の符号付き保有数（null＝不明）。
    // 二次本判断の解釈が「保有を決済する売買」では損切り幅を任意にするために使う（TradeDecisionParser.ParseDetailed）。
    // **省略可能にしない** —— 省ける形にすると渡し忘れが「決済の判断を損切り幅の欠落で捨てる」（#1187 の症状）へ黙って戻り、
    // 試験は全緑のままになる（IADR-0163 決定2 の規律に倣う）。保有を知らない呼び出し側は null（不明＝緩めない）を明示する。
    public async Task<OrchestratedDecision> DecideAsync(
        Func<string> screeningPromptFactory, string decisionPrompt, int? signedHeldQuantity,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(screeningPromptFactory);
        ArgumentNullException.ThrowIfNull(decisionPrompt);

        // 一次スクリーニング（軽量モデル・1 回）。Hold なら二次をスキップして打ち切る（費用統制）。
        var screeningGarbleSuspected = false;
        if (options.EnableScreening)
        {
            // IADR-0212: 用途は一次スクリーニング（軽量モデルの割当・費用も取引判断サイクルの一部）。
            var screenOutput = await llm
                .CompleteAsync(
                    screeningPromptFactory(), options.PrimaryModel, LlmPurposes.TradeDecisionScreening, cancellationToken)
                .ConfigureAwait(false);
            // #806, IADR-0248: 一次は**方向（関心の有無）だけ**を読む（ParseScreening）。本判断用の不変量
            // （Buy/Sell は価格・損切り幅が正）を一次に掛けると、数値を省いた Buy 候補が InvalidValues＝解析不能で
            // 打ち切られ、関心ありの銘柄が本判断に届かない（2026-09-16 開場中の実測）。価格・損切り幅は二次が改めて出す。
            var screen = TradeDecisionParser.ParseScreening(screenOutput);

            // 🔴 FR-04, FR-11, #1290, IADR-0524 決定 2/3: 一次の根拠文の文字化けの疑いを**受け取った地点で 1 回だけ**検出し、
            // 印（ScreeningRationaleGarbleSuspected）として運ぶ（転記先ごとに検出し直さない）。🔴 action は変えない（Hold に倒さない）。
            // 解析不能の根拠は安全既定の定型文でありモデルの文ではないため検出しない。
            screeningGarbleSuspected = !screen.IsUnparseable && RationaleGarbleDetector.IsSuspected(screen.Rationale);
            if (screeningGarbleSuspected)
            {
                logger.LogWarning(
                    "一次スクリーニングの判断理由に文字化けの疑い（action は変えない・転記に目印を付ける・#1290）: action={Action} rationale={Rationale}",
                    screen.Action, LogSanitizer.Sanitize(screen.Rationale));
            }

            if (!screen.IsInterested)
            {
                // #247, IADR-0104 決定6: 一次で打ち切る場合も見送りの根拠（LLM 由来。拒否・空応答等）を保つ。
                // Hold は TradeDecisionMade を発行しないため、FR-11 ログが唯一の監査記録である。
                // #337（#290 吸収）, IADR-0248: **解析不能と見送りを区別して記録する。** どちらも打ち切り
                // （安全側・取引しない）だが、解析不能は出力の形の退行を示す信号であり、見送りに混ぜると
                // 監査から見えなくなる。
                // #1290, IADR-0524 決定 3: 見送りの根拠は一次の根拠文そのものが下流（FR-11 の判断の記録・Stage 0 の記録）へ渡る。
                // 疑いがあれば、ここで 1 回だけ目印を前置する（原文は書き換えない）。
                var held = screen.AsHold with
                {
                    Rationale = RationaleGarbleDetector.Mark(screen.AsHold.Rationale, screeningGarbleSuspected),
                };
                if (screen.IsUnparseable)
                {
                    // #1187: detail はモデル出力（不明な action の文字列）や例外文を含み得るため 1 行へ正規化する。
                    logger.LogWarning(
                        "一次スクリーニングの構造化出力が解析不能（見送りとは区別して記録・#290）: kind={Kind} detail={Detail}",
                        screen.Failure!.Kind, LogSanitizer.Sanitize(screen.Failure.Detail));
                }
                else
                {
                    logger.LogInformation(
                        "一次スクリーニングで見送り（二次判断をスキップ・費用統制）: rationale={Rationale}",
                        held.Rationale);
                }

                return new OrchestratedDecision(
                    held, TotalVotes: 0, AgreementVotes: 0, ScreenedOut: true,
                    UnparseableVotes: 0, ScreeningUnparseable: screen.IsUnparseable,
                    ScreeningRationaleGarbleSuspected: screeningGarbleSuspected);
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
            // #1187: 保有を決済する売買（ロング保有中の Sell・ショート保有中の Buy）では損切り幅を任意にする。
            var parsed = TradeDecisionParser.ParseDetailed(output, signedHeldQuantity);
            if (parsed.IsUnparseable)
            {
                // #290, IADR-0248: 解析不能票は Hold として多数決へ入れる（安全側・従来挙動）が、
                // 件数は見送りと区別して数え、FR-11 の記録へ出す。
                // 🔴 #1187: **解析できた action を載せる**（InvalidValues のとき Buy/Sell。形の問題では null）。従来は載らず、
                // 「捨てられたのは利確の Sell だった」が推定でしか言えなかった。action は列挙値（モデルの文字列ではない）。
                // detail はモデル出力（不明な action の文字列）や例外文を含み得るため 1 行へ正規化する。
                unparseableVotes++;
                logger.LogWarning(
                    "二次本判断の構造化出力が解析不能（Hold 票として扱う・#290）: vote={Vote}/{Total} kind={Kind} action={Action} " +
                    "held={Held} detail={Detail}",
                    i + 1, options.VoteCount, parsed.Failure!.Kind, parsed.Failure.Action?.ToString() ?? "不明",
                    signedHeldQuantity?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "不明",
                    LogSanitizer.Sanitize(parsed.Failure.Detail));
            }

            votes.Add(parsed.Decision);
        }

        var aggregated = DecisionAggregator.Aggregate(votes);
        logger.LogInformation(
            "二次多数決: total={Total} agreement={Agreement} action={Action} unparseable={Unparseable}",
            aggregated.TotalVotes, aggregated.AgreementVotes, aggregated.Decision.Action, unparseableVotes);

        return new OrchestratedDecision(
            aggregated.Decision, aggregated.TotalVotes, aggregated.AgreementVotes, ScreenedOut: false,
            UnparseableVotes: unparseableVotes, ScreeningUnparseable: false,
            ScreeningRationaleGarbleSuspected: screeningGarbleSuspected);
    }
}

// IADR-0039: オーケストレーション結果。Decision は下流サイジングへ、票数・スクリーニング可否は FR-11 監査ログへ。
// ScreenedOut=true は一次スクリーニングで打ち切ったこと（TotalVotes=0）を表す。
// #337（#290 吸収）, IADR-0248: UnparseableVotes は二次本判断のうち構造化出力を解析できなかった票数、
// ScreeningUnparseable は一次の打ち切りが「解析不能」由来だったこと（見送りとの区別・FR-11 記録用）。
// 🔴 FR-04, FR-11, #1290, IADR-0524 決定 2/3: ScreeningRationaleGarbleSuspected は一次の根拠文に文字化けの疑いがあったこと
// （一次を走らせなかった・解析不能なら false）。見送り（ScreenedOut）のときは Decision.Rationale に目印を前置済み。
// 本判断へ進んだときは Decision.Rationale は本判断の根拠であり、印は一次の根拠文についての記録に留まる。
public sealed record OrchestratedDecision(
    LlmDecision Decision, int TotalVotes, int AgreementVotes, bool ScreenedOut,
    int UnparseableVotes = 0, bool ScreeningUnparseable = false, bool ScreeningRationaleGarbleSuspected = false);

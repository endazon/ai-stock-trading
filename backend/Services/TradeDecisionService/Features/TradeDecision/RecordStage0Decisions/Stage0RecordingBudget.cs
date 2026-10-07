using AiStockTrading.Shared.Infrastructure.Composable.Llm;

namespace TradeDecisionService.Features.TradeDecision.RecordStage0Decisions;

// FR-15, NFR（費用）, ADR-0033 決定5, #632, IADR-0318: Stage 0 記録の**費用見積り**（純関数）。
//
// ADR-0033 決定5 は金額の上限値を固定せず、**実行前に見積りを提示し利用者が承認しない限り実行しない**
// ことを必須とした。見積りに含めるものも同決定が定めている。
//   - 銘柄数 × 対象営業日数 × 1 日あたり判断回数 × 多数決回数
//   - **実測に基づく 1 判断あたりのトークン量**（入力・出力）と、単価表による円換算
//   - 合計見込み額
//
// 🔴 FR-15, ADR-0054 決定3, #1196, IADR-0498: 記録は本番と同じ二段（一次スクリーニング → 本判断）で走る。
// 呼び出し回数は「判断時点数 ×（一次 1 ＋ 多数決回数）」で数える —— **全件が一次を通ると見る**（過大側。一次で見送れば
// 本判断を呼ばないため、実績は見積り以下になる）。一次は一次のトークン量と一次の層の単価で円換算する。
//
// 🔴 **トークン量の既定値を発明しない。** 計画（05_trading-assumptions §6.1）に 1 判断あたりの
// トークン量の前提は無く、ADR-0033 は「決定5 の見積りが最初の実測値になる」と書いている。
// 引数で受け、未設定なら 0 円になる（＝承認値と一致しない＝実行されない）ようにする。

/// <summary>見積りの入力。</summary>
/// <param name="DecisionsPerDay">
/// 1 日あたりの判断回数。**現行の記録器は 1**（定時サイクル 1 回に相当）。式は計画の条文どおり
/// 引数に取るが、複数回/日は as-of 入力の実供給と同時に扱う（残件）。
/// </param>
/// <param name="ScreeningInputTokensPerDecision">一次スクリーニング 1 回あたりの入力トークン量（ADR-0054 決定3・#1196）。</param>
/// <param name="ScreeningOutputTokensPerDecision">一次スクリーニング 1 回あたりの出力トークン量。</param>
public readonly record struct Stage0RecordingEstimateInput(
    int SymbolCount,
    int TradingDayCount,
    int DecisionsPerDay,
    int VoteCount,
    int InputTokensPerDecision,
    int OutputTokensPerDecision,
    int ScreeningInputTokensPerDecision = 0,
    int ScreeningOutputTokensPerDecision = 0);

/// <summary>見積りの結果（利用者へ提示する内訳）。</summary>
public readonly record struct Stage0RecordingEstimate(
    long CallCount,
    long InputTokens,
    long OutputTokens,
    decimal TotalJpy);

public static class Stage0RecordingBudget
{
    /// <summary>
    /// 見積りを算出する。負の入力は 0 として扱い、**過小見積りを作らない**方向に丸める
    /// （呼び出し回数が 0 なら金額 0 ＝承認値と一致せず実行されない）。
    /// </summary>
    /// <param name="price">本判断（`trade-decision`）の層の単価。</param>
    /// <param name="screeningPrice">一次スクリーニング（`trade-decision-screening`）の層の単価（ADR-0054 決定3・#1196）。</param>
    public static Stage0RecordingEstimate Estimate(
        Stage0RecordingEstimateInput input, LlmPrice price, LlmPrice screeningPrice)
    {
        var symbols = Math.Max(0, input.SymbolCount);
        var days = Math.Max(0, input.TradingDayCount);
        var perDay = Math.Max(0, input.DecisionsPerDay);
        var votes = Math.Max(0, input.VoteCount);
        var inputTokens = Math.Max(0, input.InputTokensPerDecision);
        var outputTokens = Math.Max(0, input.OutputTokensPerDecision);
        var screeningInputTokens = Math.Max(0, input.ScreeningInputTokensPerDecision);
        var screeningOutputTokens = Math.Max(0, input.ScreeningOutputTokensPerDecision);

        var decisions = (long)symbols * days * perDay;
        var decisionCalls = decisions * votes;
        // 🔴 #1196: 一次は判断時点ごとに 1 回（全件が一次を通ると見る＝過大側）。
        var screeningCalls = decisions;
        var totalInput = (decisionCalls * inputTokens) + (screeningCalls * screeningInputTokens);
        var totalOutput = (decisionCalls * outputTokens) + (screeningCalls * screeningOutputTokens);

        // 単価は円/1k トークン。LlmPricing は int を取るため、桁あふれを避けて自前で同じ式を適用する
        // （費用 = 入力÷1000×入力単価 + 出力÷1000×出力単価。IADR-0055 決定2 と同一式）。層ごとに単価を引く。
        var total = Cost(decisionCalls * inputTokens, decisionCalls * outputTokens, price)
            + Cost(screeningCalls * screeningInputTokens, screeningCalls * screeningOutputTokens, screeningPrice);

        return new Stage0RecordingEstimate(decisionCalls + screeningCalls, totalInput, totalOutput, total);
    }

    private static decimal Cost(long input, long output, LlmPrice price) =>
        (input / 1000m * price.InputPer1kTokens) + (output / 1000m * price.OutputPer1kTokens);

    /// <summary>
    /// 期間 [from, to] の**平日**の数。
    /// <para>
    /// 🔴 **休場日を差し引かない。** 休場日の一覧は市場カレンダー（構成注入・既定は空）にしか無く、
    /// 見積りの時点で確実に引けない。差し引かないことで見積りは**過大側**に寄る ——
    /// 予算の見積りとして安全な向きであり、実績は必ず見積り以下になる
    /// （記録器は as-of 入力が得られない日をスキップするため）。
    /// </para>
    /// </summary>
    public static int WeekdayCount(DateOnly from, DateOnly to)
    {
        if (from > to)
            return 0;

        var count = 0;
        for (var day = from; day <= to; day = day.AddDays(1))
        {
            if (day.DayOfWeek is not (DayOfWeek.Saturday or DayOfWeek.Sunday))
                count++;
        }

        return count;
    }
}

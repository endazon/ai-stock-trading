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
// 🔴 **トークン量の既定値を発明しない。** 計画（05_trading-assumptions §6.1）に 1 判断あたりの
// トークン量の前提は無く、ADR-0033 は「決定5 の見積りが最初の実測値になる」と書いている。
// 引数で受け、未設定なら 0 円になる（＝承認値と一致しない＝実行されない）ようにする。

/// <summary>見積りの入力。</summary>
/// <param name="DecisionsPerDay">
/// 1 日あたりの判断回数。**現行の記録器は 1**（定時サイクル 1 回に相当）。式は計画の条文どおり
/// 引数に取るが、複数回/日は as-of 入力の実供給と同時に扱う（残件）。
/// </param>
public readonly record struct Stage0RecordingEstimateInput(
    int SymbolCount,
    int TradingDayCount,
    int DecisionsPerDay,
    int VoteCount,
    int InputTokensPerDecision,
    int OutputTokensPerDecision);

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
    public static Stage0RecordingEstimate Estimate(Stage0RecordingEstimateInput input, LlmPrice price)
    {
        var symbols = Math.Max(0, input.SymbolCount);
        var days = Math.Max(0, input.TradingDayCount);
        var perDay = Math.Max(0, input.DecisionsPerDay);
        var votes = Math.Max(0, input.VoteCount);
        var inputTokens = Math.Max(0, input.InputTokensPerDecision);
        var outputTokens = Math.Max(0, input.OutputTokensPerDecision);

        var calls = (long)symbols * days * perDay * votes;
        var totalInput = calls * inputTokens;
        var totalOutput = calls * outputTokens;

        // 単価は円/1k トークン。LlmPricing は int を取るため、桁あふれを避けて自前で同じ式を適用する
        // （費用 = 入力÷1000×入力単価 + 出力÷1000×出力単価。IADR-0055 決定2 と同一式）。
        var total = (totalInput / 1000m * price.InputPer1kTokens) + (totalOutput / 1000m * price.OutputPer1kTokens);

        return new Stage0RecordingEstimate(calls, totalInput, totalOutput, total);
    }

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

using AiStockTrading.Shared.Contracts.Events;
using AiStockTrading.Shared.Contracts.Llm;
using AiStockTrading.Shared.Kernel.Trading;

namespace AiStockTrading.Report.Domain;

// FR-06, FR-16, #338, 04_report-templates 月報 §7, ADR-0017 決定2・決定4, INDEX 決定44, IADR-0250/0251:
// 当期間の LLM 利用実績（月報 §7 と日報サマリのスキップ回数の素材）。
//
// 🔴 **本型は台帳から引いた「生の記録」だけを持ち、判断も丸めもしない。**
// 集計は LlmUsageAggregator（純関数）が行う。数値をコードで作る経路を 1 本に閉じ、LLM を介在させない（FR-16）。
//
// **`null`（本型そのものが無い）＝照会できていない／空の列＝当期間に事象が無かった。**
// 既存の FxSourceStatus・BuyInInferences と同じ規律であり、**両者を潰さない**。
public sealed record LlmUsageRecord(
    IReadOnlyList<LlmCostIncurred> Costs,
    IReadOnlyList<LlmFallbackFired> Fallbacks,
    IReadOnlyList<TradeDecisionSkipped> Skips,
    // INDEX 決定44: スクリーニング入力のコンテキスト超過による縮退の件数。
    // 🔴 **`null` は「供給が無い」であり「0 回」ではない。** 発生源（#337 の領域）が未実装のため、
    // 現状は常に null である。**0 と書くと「静かに判断材料が減っていた」状態を見逃す**——
    // 計画が件数を残せと定めた理由そのものを壊すため、倒し先を空へ寄せない。
    ScreeningDegradationCounts? ScreeningDegradation = null);

/// <summary>
/// INDEX 決定44, 04_report-templates 月報 §7: スクリーニング入力の縮退件数。
/// <para>
/// 🔴 <b>分割（材料は減らない）と切り詰め（材料が減る）は必ず分けて数える</b>（計画の明文）。
/// 1 つの数へ足し込むと、判断材料が減った事実が分割の回数に埋もれる。
/// </para>
/// </summary>
/// <param name="SplitCount">銘柄数の分割を行った回数（第一手・材料は減らない）。</param>
/// <param name="TruncationCount">切り詰めが発生した件数（材料が減った）。</param>
/// <param name="TruncatedTargets">
/// 切り詰めた対象の内訳（"RAG" / "ニュース" 等 → 件数）。計画の表記 <c>n 件（RAG: n / ニュース: n）</c> に対応する。
/// </param>
public sealed record ScreeningDegradationCounts(
    int SplitCount,
    int TruncationCount,
    IReadOnlyDictionary<string, int> TruncatedTargets);

/// <summary>
/// FR-06, FR-16, #338, 04_report-templates 月報 §7: <see cref="LlmUsageRecord"/> の集計結果（純関数の出力）。
/// </summary>
/// <param name="TradeDecisionCostJpy">月次 LLM 費用上限の対象となる費用（円）。</param>
/// <param name="ReportCostJpyByPurpose">報告書生成の費用（円）を用途別に。上限の対象外。</param>
/// <param name="OtherCostJpy">上限の対象でも報告書でもない用途の費用（情報収集等）。</param>
/// <param name="FallbacksByPurposeAndOutcome">フォールバック発火の件数（用途 × 原因）。</param>
/// <param name="SkipCount">モデル利用不能による取引判断スキップ回数。</param>
/// <param name="SkipsByReason">スキップ回数の事由別内訳。</param>
public sealed record LlmUsageSummary(
    decimal TradeDecisionCostJpy,
    IReadOnlyList<(string Purpose, decimal AmountJpy)> ReportCostJpyByPurpose,
    decimal OtherCostJpy,
    IReadOnlyList<(string Purpose, string Outcome, int Count)> FallbacksByPurposeAndOutcome,
    int SkipCount,
    IReadOnlyList<(string Reason, int Count)> SkipsByReason);

// FR-06, FR-16, #338, 04_report-templates 月報 §7, 05_trading-assumptions §6.1, IADR-0251:
// LLM 利用実績の集計（純関数・決定的・副作用なし）。
//
// 🔴 **取引判断の費用と報告書生成の費用は必ず分ける**（計画 §7 の明文。
// 「合算すると、どちらが上限に効いているか分からなくなる」）。分別の判定は
// `LlmCostScope.IsGoverned` を**唯一の入力**とする——費用統制サービスが上限へ積むか否かを決めるのと
// 同じ関数であり、報告書と統制で分別がずれることを構造的に防ぐ。
public static class LlmUsageAggregator
{
    /// <summary>
    /// 月次 LLM 費用上限（円）。消費率の分母に用いる（**抑制はしない**。
    /// 計画 §7「目的は検知のみであり、上限による抑制は行わない」）。
    /// </summary>
    /// <remarks>
    /// 🔴 **値を書かず、前提条件（05_trading-assumptions §6.1）の既定から引く。**
    /// 同じ数を 2 箇所に置くと、片方だけ変わったときに消費率が黙って誤表示になる
    /// （分母がずれても計算は成立するため、テストも実行時も気づけない）。
    /// `const` にできないのは意図的で、コンパイル時定数にすると再び値の複写になる。
    /// </remarks>
    public static readonly decimal MonthlyLlmCostLimitJpy =
        TradingAssumptionsDefaults.Create().CostLimits.Llm;

    /// <summary>集計する（決定的・入力順に依存しない）。</summary>
    public static LlmUsageSummary Aggregate(LlmUsageRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);

        var governed = 0m;
        var other = 0m;
        var reportCosts = new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase);

        foreach (var cost in record.Costs)
        {
            // 🔴 上限の対象判定は購読側（費用統制）と同じ関数で行う。
            // 用途が null / 空のときは対象へ倒る（過小計上を作らない・IADR-0218）。
            if (LlmCostScope.IsGoverned(cost.Purpose))
            {
                governed += cost.Amount;
                continue;
            }

            if (LlmPurposes.IsReport(cost.Purpose))
            {
                var key = cost.Purpose!;
                reportCosts[key] = reportCosts.TryGetValue(key, out var current) ? current + cost.Amount : cost.Amount;
                continue;
            }

            // 情報収集など、上限の対象でも報告書でもない用途。**捨てない**——
            // 落とすと「どこにも現れない費用」ができ、#282 と同じ形になる。
            other += cost.Amount;
        }

        return new LlmUsageSummary(
            governed,
            [.. reportCosts.OrderBy(e => e.Key, StringComparer.Ordinal).Select(e => (e.Key, e.Value))],
            other,
            [.. record.Fallbacks
                .GroupBy(f => (f.Purpose, f.Outcome))
                .OrderBy(g => g.Key.Purpose, StringComparer.Ordinal)
                .ThenBy(g => g.Key.Outcome, StringComparer.Ordinal)
                .Select(g => (g.Key.Purpose, g.Key.Outcome, g.Count()))],
            record.Skips.Count,
            [.. record.Skips
                .GroupBy(s => s.Reason, StringComparer.Ordinal)
                .OrderBy(g => g.Key, StringComparer.Ordinal)
                .Select(g => (g.Key, g.Count()))]);
    }

    /// <summary>
    /// 月次上限に対する消費率（0〜。1.0 = 100%）。
    /// <para>上限が 0 以下なら消費率は定義できないため <c>null</c> を返す（0% と書かない）。</para>
    /// </summary>
    /// <remarks>
    /// 既定値を省略可能引数に書けない（<see cref="MonthlyLlmCostLimitJpy"/> は
    /// コンパイル時定数ではない）ため、<c>null</c> を「既定の上限を使う」の意味に用いる。
    /// </remarks>
    public static decimal? ConsumptionRatio(decimal amountJpy, decimal? limitJpy = null)
    {
        var limit = limitJpy ?? MonthlyLlmCostLimitJpy;
        return limit <= 0m ? null : amountJpy / limit;
    }
}

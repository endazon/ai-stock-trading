using AiStockTrading.Shared.Contracts.Errors;
using AiStockTrading.Shared.Kernel.Trading;

namespace ConfigurationService.Features.Assumptions;

// 🔴 SC-01, FR-17, 05_trading-assumptions §6, IADR-0605, #1514: 月次費用上限の内訳と総額の整合。
// 計画 §6 は総額を「LLM＋インフラ＋データの合計」と定義する（20,000 円 ＝ 15,000＋5,000＋0）。よって整合は**合計 ＝ 総額**。
// 各欄は 0 以上（データ費用 0 が既定）。**保存のときだけ**検証する（読み取り・消費側の判定は変えない）。
// 画面（SC-01）も同じ規則で保存を無効にするが、正（権威）は本検証である（画面だけだと API の直叩きで抜ける）。
public static class CostLimitsConsistency
{
    /// <summary>規則に反していれば利用者へ見せる理由、満たしていれば <c>null</c>。</summary>
    public static string? Violation(MonthlyCostLimits limits)
    {
        ArgumentNullException.ThrowIfNull(limits);

        var negative = new List<string>();
        if (limits.Total < 0m) negative.Add("総額");
        if (limits.Llm < 0m) negative.Add("LLM");
        if (limits.Infrastructure < 0m) negative.Add("インフラ");
        if (limits.Data < 0m) negative.Add("データ");
        if (negative.Count > 0)
            return $"月次費用上限は 0 以上で指定してください（負の値: {string.Join("、", negative)}）。";

        var sum = limits.Llm + limits.Infrastructure + limits.Data;
        if (sum != limits.Total)
            return $"月次費用上限の内訳の合計（LLM＋インフラ＋データ ＝ {Format(sum)} 円）が総額（{Format(limits.Total)} 円）と一致しません。";

        return null;
    }

    /// <summary>規則に反していれば利用者へ見せる印つきの <see cref="ArgumentException"/> を投げる（ホストの群のフィルタが 400 へ写す）。</summary>
    public static void ThrowIfViolated(MonthlyCostLimits limits)
    {
        if (Violation(limits) is { } reason)
            throw new ArgumentException(reason, nameof(limits)).ClientVisible();
    }

    private static string Format(decimal value) => value.ToString(System.Globalization.CultureInfo.InvariantCulture);
}

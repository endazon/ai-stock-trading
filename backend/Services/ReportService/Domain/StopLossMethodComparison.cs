using AiStockTrading.Shared.Contracts.Events;
using AiStockTrading.Shared.Contracts.Trading;

namespace ReportService.Domain;

// FR-06, FR-10, ADR-0040 決定1, #1002, IADR-0429 決定3: 日報の「選ばれていた手法（承認時点）」と「実際に適用された手法
// （発注執行の解決結果）」の突き合わせ、および月報 §6 の日数ベースの内訳（純関数）。
//
// 🔴 **母集合は承認である。** 解決結果は DecisionId で承認に結び付け、**承認の JST 暦日**に数える（解決した時刻の日付では
// 数えない）。米国の取引時間は JST の日付を跨ぐため、解決の時刻で数えると 1 行目と 2 行目の母集合がずれる。
//
// 🔴 **解決結果が見つからない承認は、一致とも食い違いとも言わない**（別に数える）。「不明」を「なし」へ潰さない。
//
// - 同じ DecisionId の解決結果が複数あれば**時刻が最も遅いもの**を採る。2 本目は通常生じない——結果を返した回の後の
//   再配送は完了済み・見送り済みの判定で解決の前に戻るか、予約の競合で例外に終わる（例外の回は発行しない）。
//   生じ得るのは同じ承認が並行して配送され双方が予約の前で見送った場合など（中身は同じ）であり、その場合の決め方である。
// - 送信後に届いたか不明（BrokerDispatchIndeterminateException）の承認は、初回も再試行（予約の競合）も例外で終わるため
//   解決結果が発行されない——「記録が見つからない」承認として数える（一致とも食い違いとも言わない）。
// - **食い違い** ＝ 解決結果が見つかった承認のうち、適用した手法が承認の手法と違うもの（拒否は「適用なし」なので必ず食い違い）。
public sealed record StopLossMethodComparison(
    IReadOnlyList<StopLossMethodOutcome> Outcomes,
    int UnreadableResolutionCount)
{
    /// <summary>突き合わせの対象にした承認の数（＝ 1 行目の件数）。</summary>
    public int ApprovalCount => Outcomes.Count;

    /// <summary>解決結果の記録が見つかった承認の数（＝ 2 行目の件数）。</summary>
    public int ResolvedCount => Outcomes.Count(o => o.Resolution is not null);

    /// <summary>解決結果の記録が見つからない承認の数（2 行目・食い違いのどちらにも数えない）。</summary>
    public int UnresolvedCount => Outcomes.Count(o => o.Resolution is null);

    /// <summary>実際に適用された手法ごとの件数。手法の序数順で、拒否（適用なし＝ null）は最後。</summary>
    public IReadOnlyList<StopLossAppliedCount> AppliedCounts =>
    [
        .. Outcomes
            .Where(o => o.Resolution is not null)
            .GroupBy(o => o.Resolution!.AppliedMethod)
            .OrderBy(g => g.Key is null ? int.MaxValue : (int)g.Key.Value)
            .Select(g => new StopLossAppliedCount(g.Key, g.Count())),
    ];

    /// <summary>食い違いの件数。</summary>
    public int DisagreementCount => Outcomes.Count(o => o.Disagrees);

    /// <summary>食い違いを「選択 → 適用・理由・（拒否なら）発注先」でまとめたもの。</summary>
    public IReadOnlyList<StopLossMethodDisagreement> Disagreements =>
    [
        .. Outcomes
            .Where(o => o.Disagrees)
            .GroupBy(o => (
                Selected: o.Approval.Method,
                Applied: o.Resolution!.AppliedMethod,
                o.Resolution.Reason,
                Provider: o.Resolution.AppliedMethod is null ? o.Resolution.Provider : (BrokerProvider?)null))
            .OrderBy(g => (int)g.Key.Selected)
            .ThenBy(g => g.Key.Applied is null ? int.MaxValue : (int)g.Key.Applied.Value)
            .ThenBy(g => (int)g.Key.Reason)
            .Select(g => new StopLossMethodDisagreement(
                g.Key.Selected, g.Key.Applied, g.Key.Reason, g.Key.Provider, g.Count())),
    ];

    // ---- 月報 §6（日数ベース）-----------------------------------------------------------------

    /// <summary>新規建ての承認が 1 件以上あった JST 暦日の数。</summary>
    public int ApprovalDays => Outcomes.Select(o => o.Day).Distinct().Count();

    /// <summary>実際に適用された手法ごとに、それが 1 件以上あった JST 暦日の数（手法の序数順・拒否は最後）。</summary>
    public IReadOnlyList<StopLossAppliedDays> AppliedDays =>
    [
        .. Outcomes
            .Where(o => o.Resolution is not null)
            .GroupBy(o => o.Resolution!.AppliedMethod)
            .OrderBy(g => g.Key is null ? int.MaxValue : (int)g.Key.Value)
            .Select(g => new StopLossAppliedDays(g.Key, g.Select(o => o.Day).Distinct().Count())),
    ];

    /// <summary>実際に適用された手法（拒否を含む）が 2 種類以上あった日の数（<see cref="AppliedDays"/> では各手法に重複して数える）。</summary>
    public int MixedDays => Outcomes
        .Where(o => o.Resolution is not null)
        .GroupBy(o => o.Day)
        .Count(g => g.Select(o => o.Resolution!.AppliedMethod).Distinct().Count() > 1);

    /// <summary>食い違いが 1 件以上あった日の数。</summary>
    public int DisagreementDays => Outcomes.Where(o => o.Disagrees).Select(o => o.Day).Distinct().Count();

    /// <summary>解決結果の記録が見つからない承認を含む日の数（その日の食い違いは判定しきれていない）。</summary>
    public int UnresolvedDays => Outcomes.Where(o => o.Resolution is null).Select(o => o.Day).Distinct().Count();

    public static StopLossMethodComparison From(StopLossMethodUsage usage, StopLossMethodResolutionFeed feed)
    {
        ArgumentNullException.ThrowIfNull(usage);
        ArgumentNullException.ThrowIfNull(feed);

        var latest = new Dictionary<Guid, StopLossMethodResolved>();
        foreach (var r in feed.Resolutions)
        {
            if (!latest.TryGetValue(r.DecisionId, out var existing) || r.OccurredAt >= existing.OccurredAt)
                latest[r.DecisionId] = r;
        }

        var outcomes = usage.Approvals
            .Select(a => new StopLossMethodOutcome(a, JstDayOf(a.ApprovedAt), latest.GetValueOrDefault(a.DecisionId)))
            .ToList();
        return new StopLossMethodComparison(outcomes, feed.UnreadableCount);
    }

    /// <summary>承認時刻の JST 暦日（日報の期間と同じ境界。<see cref="ReportSchedule.JstOffset"/>）。</summary>
    public static DateOnly JstDayOf(DateTimeOffset instant) =>
        DateOnly.FromDateTime(instant.ToOffset(ReportSchedule.JstOffset).DateTime);

    /// <summary>実際に適用された手法の表示名。拒否（null）は「発注せず（拒否）」。</summary>
    public static string AppliedLabel(StopLossExecutionMethod? applied) =>
        applied is { } method ? StopLossMethodUsage.Label(method) : "発注せず（拒否）";

    /// <summary>食い違いの理由の表示名（発注執行の解決規則の分岐に対応する）。</summary>
    public static string ReasonLabel(StopLossMethodResolutionReason reason, BrokerProvider? provider) => reason switch
    {
        StopLossMethodResolutionReason.BrokerNotMoomooSimulate =>
            "S0 以外の手法は moomoo SIMULATE でしか適用しないため発注しなかった"
            + (provider is { } p ? $"。実際の発注先: {ProviderLabel(p)}" : string.Empty),
        StopLossMethodResolutionReason.ShortSellEntry => "空売りの新規建ては S0 で扱う",
        StopLossMethodResolutionReason.UnknownMethod => "未知の手法の値のため S0 と同じ扱いにした",
        StopLossMethodResolutionReason.AsSelected => "選択どおり",
        _ => $"不明な理由({(int)reason})",
    };

    // 発注先の表示名（計画 05_screens「表示規約（共通）」の用語。内蔵 paper を「SIMULATE」と呼ばない）。
    private static string ProviderLabel(BrokerProvider provider) => provider switch
    {
        BrokerProvider.InternalPaper => "内蔵 paper",
        BrokerProvider.MoomooReal => "moomoo REAL",
        BrokerProvider.MoomooSimulate => "moomoo SIMULATE",
        _ => $"不明({(int)provider})",
    };
}

/// <summary>#1002: 承認 1 件と、その解決結果（見つからなければ null）。<see cref="Day"/> は承認の JST 暦日。</summary>
public sealed record StopLossMethodOutcome(StopLossMethodApproval Approval, DateOnly Day, StopLossMethodResolved? Resolution)
{
    /// <summary>解決結果が見つかり、適用した手法が承認の手法と違う（拒否を含む）。</summary>
    public bool Disagrees => Resolution is { } r && r.AppliedMethod != Approval.Method;
}

/// <summary>#1002: 実際に適用された手法（拒否は null）ごとの件数。</summary>
public sealed record StopLossAppliedCount(StopLossExecutionMethod? Applied, int Count);

/// <summary>#1002: 実際に適用された手法（拒否は null）ごとの日数。</summary>
public sealed record StopLossAppliedDays(StopLossExecutionMethod? Applied, int Days);

/// <summary>#1002: 同じ形の食い違い（選択 → 適用・理由・拒否なら発注先）の件数。</summary>
public sealed record StopLossMethodDisagreement(
    StopLossExecutionMethod Selected,
    StopLossExecutionMethod? Applied,
    StopLossMethodResolutionReason Reason,
    BrokerProvider? Provider,
    int Count);

/// <summary>
/// #1002, IADR-0429 決定4: 監査台帳から引いた解決結果（期間の前後 1 日を含む窓）と、本文を復元できなかった記録の数。
/// 🔴 <c>null</c>（照会できていない）と空（記録 0 件）は別の事実である——このクラスを null で表すのは前者だけ。
/// </summary>
public sealed record StopLossMethodResolutionFeed(
    IReadOnlyList<StopLossMethodResolved> Resolutions,
    int UnreadableCount = 0);

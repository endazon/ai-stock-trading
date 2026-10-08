using AiStockTrading.Shared.Contracts.Events;

namespace ReportService.Domain;

// FR-06, UC-03〜05, 計画 ADR-0053 決定 2・フォローアップ 3, #1224, IADR-0516 決定 1・2: 期間の集計として監査台帳（と強制買戻しの推定台帳）から
// 引いた記録を、報告書のセッションの窓で絞る（純関数・決定的）。
//
// 供給元は窓を覆う JST の暦日の外包（ReportSessionWindow.JstLedgerRange）で引き、ここで「報告可能になる瞬間」が窓に入る記録だけを残す。
// 市場を持つ記録はセッションの大引けと記録の時刻の遅いほう（約定と同じ日報に載り、生成の後の記録は次の報告書に載る）、
// 市場を持たない記録（為替の情報源の状態）は記録の時刻で数える。どの記録も同じ種別の報告書のちょうど 1 つに入る。
//
// 🔴 **LLM 利用実績はここで絞らない**（JST の暦日のまま。月次の費用上限が暦の月であるため。IADR-0516 決定 2）。
// 🔴 **損切りの手法の解決はここで絞らない**——承認と DecisionId で突き合わせる（解決の時刻で数えない。StopLossMethodComparison）。
public static class ReportLedgerWindowing
{
    /// <summary>借株料: 計上日（<c>TradingDay</c>・市場の現地の日）のセッションの大引けと記録の時刻の遅いほうで数える（ADR-0027 決定 3 の計上日への帰属）。</summary>
    public static BorrowFeeRecord Within(this BorrowFeeRecord record, ReportSessionWindow window)
    {
        ArgumentNullException.ThrowIfNull(record);
        ArgumentNullException.ThrowIfNull(window);

        return new BorrowFeeRecord(
            [.. record.Accruals.Where(a => window.Counts(a.Market, a.TradingDay, a.AccruedAt))],
            [.. record.Unavailable.Where(u => window.Counts(u.Market, u.TradingDay, u.ObservedAt))]);
    }

    /// <summary>
    /// 損切りの手法（承認時点）: 承認の市場・承認時刻で数え、手法別の件数を残った承認から引き直す。
    /// 明細を持たない値（件数だけで作った旧い値）と市場を持たない承認は絞らない（窓へ割り当てる手掛かりが無い）。
    /// 本文を復元できなかった記録の数は時刻を持たないため、そのまま残す。
    /// </summary>
    public static StopLossMethodUsage Within(this StopLossMethodUsage usage, ReportSessionWindow window)
    {
        ArgumentNullException.ThrowIfNull(usage);
        ArgumentNullException.ThrowIfNull(window);

        if (usage.Approvals.Count == 0)
            return usage;

        var kept = usage.Approvals
            .Where(a => a.Market is not { } market || window.Counts(market, a.ApprovedAt))
            .ToList();
        var counts = kept
            .GroupBy(a => a.Method)
            .OrderBy(g => (int)g.Key)
            .Select(g => new StopLossMethodCount(g.Key, g.Count()))
            .ToList();
        return new StopLossMethodUsage(counts, usage.UnreadableCount) { Approvals = kept };
    }

    /// <summary>強制買戻しの推定: 市場・推定時刻で数える。</summary>
    public static IReadOnlyList<BuyInInferred> Within(this IReadOnlyList<BuyInInferred> inferences, ReportSessionWindow window)
    {
        ArgumentNullException.ThrowIfNull(inferences);
        ArgumentNullException.ThrowIfNull(window);

        return [.. inferences.Where(b => window.Counts(b.Market, b.InferredAt))];
    }

    /// <summary>
    /// 維持率割れの自動縮小: 明細の市場ごとの報告可能になる瞬間の<b>最も早いもの</b>で数える（1 回の執行を 1 つの報告書へ。
    /// 市場が混ざっても 2 つの報告書へ割れない）。明細が無ければ執行の時刻で数える。
    /// </summary>
    public static IReadOnlyList<MaintenanceMarginReductionExecuted> Within(
        this IReadOnlyList<MaintenanceMarginReductionExecuted> reductions, ReportSessionWindow window)
    {
        ArgumentNullException.ThrowIfNull(reductions);
        ArgumentNullException.ThrowIfNull(window);

        return [.. reductions.Where(r => window.Contains(ReportableAt(r)))];
    }

    /// <summary>自動縮小 1 回が報告可能になる瞬間（明細の市場ごとの最も早いもの・明細が無ければ執行の時刻）。</summary>
    public static DateTimeOffset ReportableAt(MaintenanceMarginReductionExecuted reduction)
    {
        ArgumentNullException.ThrowIfNull(reduction);

        return reduction.Items.Count == 0
            ? reduction.ExecutedAt
            : reduction.Items
                .Select(i => i.Market)
                .Distinct()
                .Select(m => ReportSessionWindow.ReportableAt(m, reduction.ExecutedAt))
                .Min();
    }

    /// <summary>
    /// 為替の情報源の状態: 市場を持たない記録は発生時刻で、鮮度切れのレートでの決済（市場を持つ＝約定）は約定と同じ形で数える。
    /// クレジットは残った記録から引き直す（使っていない源のクレジットを出さない。IADR-0196 決定4）。
    /// </summary>
    public static FxSourceStatus Within(this FxSourceStatus status, ReportSessionWindow window)
    {
        ArgumentNullException.ThrowIfNull(status);
        ArgumentNullException.ThrowIfNull(window);

        return FxSourceStatus.Compose(
            [.. status.FellBacks.Where(e => window.Contains(e.OccurredAt))],
            [.. status.Restorations.Where(e => window.Contains(e.OccurredAt))],
            [.. status.StaleWarnings.Where(e => window.Contains(e.OccurredAt))],
            [.. status.StaleCloses.Where(e => window.Counts(e.Market, e.OccurredAt))],
            [.. status.Usages.Where(e => window.Contains(e.OccurredAt))]);
    }
}

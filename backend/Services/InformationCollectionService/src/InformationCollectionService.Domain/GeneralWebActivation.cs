namespace InformationCollectionService.Domain;

// FR-01, ADR-0020 決定4: 一般インターネット収集（許可リスト外の一般サイトからの取得）の発動申請。
//
// 🔴 **条件のない「最終手段」は運用時の裁量になり、規約違反・誤情報の取り込みの入口になる**（ADR-0020 §課題3）。
// したがって 4 条件を機械で判定し、**満たしていない条件を必ず列挙して返す**（「だいたい満たしている」を作らない）。
public sealed record GeneralWebActivationRequest(
    string Category,
    int OutageBusinessDays,
    bool ProviderAnnouncedDiscontinuation,
    bool HarmConfirmedInReports,
    bool TermsPermitAutomatedAccess,
    bool DataSeparationApplied,
    bool CorroboratedByIndependentSources);

// 発動可否の判定結果。ProvisionalUntil は**次回月報**（＝暫定措置の期限）。承認されないときは null。
public sealed record GeneralWebActivationDecision(
    bool Approved,
    IReadOnlyList<string> UnmetConditions,
    DateTimeOffset? ProvisionalUntil);

// FR-01, ADR-0020 決定4: 4 条件の判定と暫定期限の算出（純関数）。
public static class GeneralWebActivationPolicy
{
    /// <summary>条件 1 の境界。**欠測が 5 営業日以上継続**していること（4 営業日では成立しない）。</summary>
    public const int OutageBusinessDaysThreshold = 5;

    public static GeneralWebActivationDecision Evaluate(GeneralWebActivationRequest request, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(request);

        var unmet = new List<string>();

        // 条件1: 当該カテゴリの必須・推奨ソースが全滅し、代替の公式・準公式ソースが無いこと。
        // 具体的には「欠測が 5 営業日以上継続」または「提供終了・有料化が提供元から公表された」。
        if (request.OutageBusinessDays < OutageBusinessDaysThreshold && !request.ProviderAnnouncedDiscontinuation)
        {
            unmet.Add(
                $"条件1: 欠測が {OutageBusinessDaysThreshold} 営業日以上継続していない"
                + $"（{request.OutageBusinessDays} 営業日）、かつ提供終了・有料化の公表も無い。");
        }

        // 条件2: 欠測が損失または機会逸失の原因になったことが日報・週報の記録で確認されていること（推測で先行しない）。
        if (!request.HarmConfirmedInReports)
            unmet.Add("条件2: 損失・機会逸失の原因になったことが日報・週報の記録で確認されていない（推測で先行しない）。");

        // 条件3: 取得先の利用規約が自動取得を禁止していないこと（明文禁止先は恒久的に対象外）。
        if (!request.TermsPermitAutomatedAccess)
            unmet.Add("条件3: 取得先の利用規約が自動取得を禁止している（明文禁止先は本条件により恒久的に対象外）。");

        // 条件4: 「命令ではなくデータ」分離にかけ、複数独立ソースの裏取りを通すこと。
        if (!request.DataSeparationApplied)
            unmet.Add("条件4a: 取得テキストの「命令ではなくデータ」分離（ADR-0003）が適用されていない。");
        if (!request.CorroboratedByIndependentSources)
            unmet.Add("条件4b: 複数独立ソースの裏取りが無い（単一の一般 Web ソースのみを根拠とした発注は行わない）。");

        return unmet.Count == 0
            ? new GeneralWebActivationDecision(true, [], NextMonthlyReportBoundary(now))
            : new GeneralWebActivationDecision(false, unmet, null);
    }

    /// <summary>
    /// 暫定措置の期限＝<b>次回月報</b>。月報は月次であるため、翌月 1 日 00:00Z を境界とする。
    /// <para>
    /// 🔴 <b>恒久化しない。</b> 継続が必要なら延長ではなく、公式ソースへの切り替えか
    /// 有料の公式ソースの採用を ADR-0005 の判断プロセスに乗せる（ADR-0020 決定4）。
    /// </para>
    /// </summary>
    public static DateTimeOffset NextMonthlyReportBoundary(DateTimeOffset now)
    {
        var utc = now.ToUniversalTime();
        var firstOfThisMonth = new DateTimeOffset(utc.Year, utc.Month, 1, 0, 0, 0, TimeSpan.Zero);
        return firstOfThisMonth.AddMonths(1);
    }
}

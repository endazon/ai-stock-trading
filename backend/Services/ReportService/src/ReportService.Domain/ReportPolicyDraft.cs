namespace AiStockTrading.Report.Domain;

// FR-06, FR-07, ADR-0003, 04_workflows/03_reporting-cycle, IADR-0115 決定4, #280:
// 自動生成ドラフトの方針文（純関数・決定的）。
//
// PolicySummary は「確定すると取引に効く」フィールドであるため、自動生成では**新しい方針を機械に提案させない**。
// 直近の確定済み方針の継続案に留め、未確定である旨を明記する。機械が書いた新方針が承認待ちに並ぶと、利用者の
// レビューが「読んで承認するだけ」に退化しやすく、ADR-0003 の「確定には対話を要する」が形骸化するため。
// 振り返り・評価の散文は従来どおり IReportNarrativeDrafter（LLM）が担い、Markdown 本文に入る。
public static class ReportPolicyDraft
{
    /// <summary>方針階層の上位種別（日報→週報 / 週報→月報 / 月報→前月の月報）。</summary>
    public static ReportKind ParentKind(ReportKind kind) => kind switch
    {
        ReportKind.Daily => ReportKind.Weekly,
        ReportKind.Weekly => ReportKind.Monthly,
        _ => ReportKind.Monthly,
    };

    /// <summary>
    /// 自動生成ドラフトの方針文を組み立てる。<paramref name="previousPolicy"/> は同種別の直近確定済み方針、
    /// <paramref name="parentPeriodKey"/> は参照できた上位方針（BasedOn）。上位が参照できない場合は
    /// その旨を明記する（03_reporting-cycle「上位方針の欠落」）。
    /// </summary>
    public static string CarryOver(ReportKind kind, string? previousPeriodKey, string? previousPolicy, string? parentPeriodKey)
    {
        var self = KindLabel(kind);
        var lines = new List<string>(3) { "（自動生成ドラフト・未確定）" };

        if (!string.IsNullOrWhiteSpace(previousPolicy) && !string.IsNullOrWhiteSpace(previousPeriodKey))
        {
            lines.Add($"直近の確定済み{self}方針（{previousPeriodKey}）を継続する案です。確定前に内容を見直してください。");
            lines.Add(previousPolicy.Trim());
        }
        else
        {
            lines.Add($"参照できる確定済みの{self}方針がありません。方針を記入したうえで確定してください。");
        }

        if (string.IsNullOrWhiteSpace(parentPeriodKey))
            lines.Add($"上位方針（{ParentLabel(kind)}）は未確定のため参照していません。");

        return string.Join("\n\n", lines);
    }

    private static string KindLabel(ReportKind kind) => kind switch
    {
        ReportKind.Weekly => "週報",
        ReportKind.Monthly => "月報",
        _ => "日報",
    };

    // 上位の呼称。月報の上位は「前月の月報」であり、自種別の呼称と紛れないようにする。
    private static string ParentLabel(ReportKind kind) => kind switch
    {
        ReportKind.Daily => "週報",
        ReportKind.Weekly => "月報",
        _ => "前月の月報",
    };
}

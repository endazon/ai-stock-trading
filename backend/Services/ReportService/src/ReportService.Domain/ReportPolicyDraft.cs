using System.Text.RegularExpressions;

namespace AiStockTrading.Report.Domain;

// FR-06, FR-07, ADR-0003, 04_workflows/03_reporting-cycle, IADR-0115 決定4, IADR-0125, #280/#310:
// 自動生成ドラフトの方針文（純関数・決定的）。
//
// PolicySummary は「確定すると取引に効く」フィールドであるため、自動生成では**新しい方針を機械に提案させない**。
// 直近の確定済み方針の継続に留める。機械が書いた新方針が承認待ちに並ぶと、利用者のレビューが「読んで承認するだけ」に
// 退化しやすく、ADR-0003 の「確定には対話を要する」が形骸化するため。
// 振り返り・評価の散文は従来どおり IReportNarrativeDrafter（LLM）が担い、Markdown 本文に入る。
//
// IADR-0125, #310: 方針文が持つのは**方針の実体だけ**である。生成物の状態（ドラフト・未確定）とレビュー手順の指示は
// ここへ書かない（状態は ReportState / ReviewState と提示通知〈IADR-0116〉が持つ）。従来は前置きを本文へ載せ、
// 継続時に前世代の PolicySummary を丸ごと連結していたため、①世代ごとに定型文が積み上がり ②確定後も
// 「未確定」を名乗る本文が取引判断（G4 の確定方針）へ渡っていた。
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
    /// 引き継ぐのは方針の実体のみで、前置き・状態文言は引き継がない（IADR-0125 決定1）。
    /// </summary>
    public static string CarryOver(ReportKind kind, string? previousPeriodKey, string? previousPolicy, string? parentPeriodKey)
    {
        var substance = string.IsNullOrWhiteSpace(previousPeriodKey) ? string.Empty : Substance(previousPolicy);

        var lines = new List<string>(2)
        {
            substance.Length > 0 ? substance : NoPreviousNote(kind),
        };

        if (string.IsNullOrWhiteSpace(parentPeriodKey))
            lines.Add(NoParentNote(kind));

        return string.Join("\n\n", lines);
    }

    /// <summary>
    /// 方針文から**方針の実体**だけを取り出す（本クラスが生成した前置き・状態文言・注記の行を除去する）。
    /// 除去は全出現に対して行うため、既に世代累積したレコードを継承しても 1 回で畳まれる。
    /// 生成器の文言に該当しない行（利用者が書いた本文）は 1 文字も変えない。
    /// </summary>
    public static string Substance(string? policy)
    {
        if (string.IsNullOrWhiteSpace(policy))
            return string.Empty;

        var kept = policy
            .ReplaceLineEndings("\n")
            .Split('\n')
            .Where(line => !IsGeneratedLine(line.Trim()));

        // 除去で生じた空行の連続を 1 つの段落区切りへ畳む（利用者の段落構成は保つ）。
        return GeneratedNoise.BlankRun.Replace(string.Join("\n", kept), "\n\n").Trim();
    }

    // 継続元が無い（＝方針の実体が無い）ことの明示。レビュー手順の指示は本文へ書かない（IADR-0125 決定3）。
    private static string NoPreviousNote(ReportKind kind) => $"参照できる確定済みの{KindLabel(kind)}方針がありません。";

    // 上位方針を参照できないことの明示（03_reporting-cycle「上位方針の欠落」）。
    // 確定後も残るテキストであるため、ドラフトの状態（未確定）ではなく事実として書く。
    private static string NoParentNote(ReportKind kind) => $"上位方針（{ParentLabel(kind)}）は確定済みのものがないため参照していません。";

    private static bool IsGeneratedLine(string line) =>
        line.Length > 0 && GeneratedNoise.Patterns.Any(p => p.IsMatch(line));

    // 本クラスが生成した行の判別。**本 PR 以前の文言も含む**（live DB に累積済みのレコードを継承した際に
    // 畳めなければ、是正後の初回生成が過去の累積をそのまま引き継いでしまうため）。
    private static class GeneratedNoise
    {
        internal static readonly Regex BlankRun = new(@"\n{3,}", RegexOptions.Compiled);

        internal static readonly Regex[] Patterns =
        [
            // 旧: ドラフトの状態表示。
            new(@"^（自動生成ドラフト・未確定）$", RegexOptions.Compiled),
            // 旧: 継続案であることの注記＋レビュー指示。
            new(@"^直近の確定済み.{1,4}方針（.*?）を継続する案です。確定前に内容を見直してください。$", RegexOptions.Compiled),
            // 継続元なしの注記（旧はレビュー指示を伴う）。
            new(@"^参照できる確定済みの.{1,4}方針がありません。(方針を記入したうえで確定してください。)?$", RegexOptions.Compiled),
            // 上位方針の欠落の注記（旧＝「未確定のため」／現＝「確定済みのものがないため」）。
            new(@"^上位方針（.*?）は(未確定のため|確定済みのものがないため)参照していません。$", RegexOptions.Compiled),
        ];
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

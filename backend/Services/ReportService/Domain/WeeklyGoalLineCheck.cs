using AiStockTrading.Shared.Contracts.Events;

namespace ReportService.Domain;

// FR-06, FR-07, FR-16, 計画 ADR-0059 決定 2, #1218, IADR-0519 決定 2: 週報の方針に**書式どおりの「数値目標:」行**が無いこと
// （行なし・書式外・単位が基準通貨でない）を、確定の前に利用者へ見せる警告（純関数・決定的）。
//
// 🔴 **警告であり、確定は止めない**（計画 ADR-0059 決定 2。ADR-0051 決定 2 と同じ）。
// 🔴 **警告は方針の本文（PolicySummary）へ入れない**（IADR-0470 決定 4 と同じ。方針は確定される原文であり、表示と食い違わせない）。
// 出しどころは日報の利確の警告（PolicyTakeProfitCheck）と同じ 3 経路（提示の要約・/policy の改訂・作り直しの再提示）。対象は週報だけである。
public static class WeeklyGoalLineCheck
{
    private const string Guidance =
        "翌週の日報 §6 と週報 §1・§4 は、この週報の目標と週初来の実現損益を照合できず「照合不能」と書きます。"
        + "方針に「数値目標: -200 〜 +500 USD」の形の行を 1 行だけ書いてください（範囲は下限〜上限、単位は USD。円では書かないでください〔換算しません〕）。"
        + "「数値目標」の語はこの行の中でだけ使ってください（説明の文・見出しに使うと、行が 2 つあるとみなして読みません）。";

    /// <summary>警告が要るなら警告の全文（先頭は契約アセンブリの印）、要らなければ null。週報以外は常に null。</summary>
    public static string? WarningFor(ReportKind kind, string? policy)
    {
        if (kind != ReportKind.Weekly)
            return null;

        var reading = WeeklyGoalLine.Parse(policy);
        var reason = reading.Status switch
        {
            WeeklyGoalLineStatus.Conforming => null,
            WeeklyGoalLineStatus.Missing => "行がありません",
            WeeklyGoalLineStatus.UnitMismatch => $"単位が基準通貨 USD ではありません（{reading.Unit}）",
            _ => reading.CandidateCount > 1
                ? $"「数値目標」の語を含む行が {reading.CandidateCount} 行あります"
                : "行が書式に合いません",
        };

        return reason is null
            ? null
            : $"{ReportSummaryMarkers.WeeklyGoalLineMissingPrefix}（{reason} ／ 確定はできます）: {Guidance}";
    }
}

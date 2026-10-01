using AiStockTrading.Shared.Contracts.Events;
using AiStockTrading.Shared.Kernel.Trading;

namespace ReportService.Domain;

// FR-04, FR-07, ADR-0003, ADR-0048 決定 4, #1129, IADR-0470 決定 4: 日報の方針に**書式どおりの「利確:」行**が 1 行も無いこと
// （または書式に合わない「利確:」行があり、方針の利確の条件を読めないこと）を、確定の前に利用者へ見せる警告（純関数・決定的）。
// IADR-0470（2026-10-01 追記 / #1129 再監査）: 自由文の数値は読まない。文は直し方（「利確:」行の書式と例）まで書く。
//
// 🔴 **警告であり、確定は止めない**（オーナー裁定 2026-10-01。止めるのは計画の水準の統制であり、本件の範囲ではない）。
// 🔴 **警告は方針の本文（PolicySummary）へ入れない**——方針はそのまま取引判断へ渡り、表示と確定される原文を食い違わせない
// （ADR-0003・IADR-0431 決定 5）。入れるのは提示の要約・改訂の案内文・本文の改訂の記録だけである。
//
// 判定は共有カーネルの PolicyTakeProfitConditions（取引判断が条件の到達を確かめるのと同じ部品）で行う。
// 🔴 方針全体に 1 行でもあれば警告しない（保有中・新規建ての対象の銘柄ごとの有無は見ない）。警告の地点（改訂・自動生成）は
// 保有中の銘柄を持たず、銘柄ごとに見るには新たな配線が要るため（残余リスク。IADR-0470 の 2026-10-01 追記）。
// 「警告が出ない方針なら判断側も条件を読める」を、2 つの実装の食い違いで崩さないため。
// 対象は日報だけである（裁定は「日報の方針」。週報・月報の方針は銘柄別の売買条件の粒度を持たない〔04_report-templates 粒度の対応表〕）。
public static class PolicyTakeProfitCheck
{
    /// <summary>警告の全文（先頭は契約アセンブリの印）。</summary>
    public const string Warning =
        ReportSummaryMarkers.PolicyTakeProfitMissingPrefix
        + "（確定はできます）: システムは方針の利確の条件を読めず、取引判断に条件への到達を知らせません（保有継続〔Hold〕に倒れやすくなります）。"
        + "保有中の銘柄と新規建ての対象ごとに、方針へ「利確: AAPL +5%」（平均取得単価からの率。+ は必須）や「利確: MSFT $450 (50%)」（価格と一部利確の割合）の形の行を 1 行ずつ足してください"
        + "（すべての銘柄なら「利確: 全銘柄 +8%」）。「利確:」で始まる行にこの形以外の文字があると、その方針の利確の条件は 1 つも読まれません。";

    /// <summary>警告が要るなら警告の全文、要らなければ null。</summary>
    public static string? WarningFor(ReportKind kind, string? policy) =>
        kind == ReportKind.Daily && !PolicyTakeProfitConditions.HasAny(policy) ? Warning : null;
}

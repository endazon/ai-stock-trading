using AiStockTrading.Shared.Contracts.Events;
using AiStockTrading.Shared.Kernel.Trading;

namespace ReportService.Domain;

// FR-04, FR-07, ADR-0003, ADR-0048 決定 4, #1129, IADR-0470 決定 4: 日報の方針に**数値の利確条件**が 1 件も無いことを、
// 確定の前に利用者へ見せる警告（純関数・決定的）。
//
// 🔴 **警告であり、確定は止めない**（オーナー裁定 2026-10-01。止めるのは計画の水準の統制であり、本件の範囲ではない）。
// 🔴 **警告は方針の本文（PolicySummary）へ入れない**——方針はそのまま取引判断へ渡り、表示と確定される原文を食い違わせない
// （ADR-0003・IADR-0431 決定 5）。入れるのは提示の要約・改訂の案内文・本文の改訂の記録だけである。
//
// 判定は共有カーネルの PolicyTakeProfitConditions（取引判断が条件の到達を確かめるのと同じ部品）で行う。
// 「警告が出ない方針なら判断側も条件を読める」を、2 つの実装の食い違いで崩さないため。
// 対象は日報だけである（裁定は「日報の方針」。週報・月報の方針は銘柄別の売買条件の粒度を持たない〔04_report-templates 粒度の対応表〕）。
public static class PolicyTakeProfitCheck
{
    /// <summary>警告の全文（先頭は契約アセンブリの印）。</summary>
    public const string Warning =
        ReportSummaryMarkers.PolicyTakeProfitMissingPrefix
        + "（確定はできます）: 取引判断は「含み益が十分に出たら」のような条件に達したかを確かめられず、保有継続（Hold）に倒れます。"
        + "保有中の銘柄と新規建ての対象に、取得単価からの %（例 +5%）か価格で利確の条件を、一部利確ならその割合も書くことを検討してください。";

    /// <summary>警告が要るなら警告の全文、要らなければ null。</summary>
    public static string? WarningFor(ReportKind kind, string? policy) =>
        kind == ReportKind.Daily && !PolicyTakeProfitConditions.HasAny(policy) ? Warning : null;
}

using System.Text.RegularExpressions;
using AiStockTrading.Shared.Contracts.Events;

namespace ReportService.Domain;

// FR-04, FR-07, ADR-0048 決定 4, ADR-0051 決定 2, #1365, IADR-0544 決定 2: 日報の方針が**他の報告書（週報・月報・前日の日報）や
// 期間キーを参照で済ませている**ことを、確定の前に利用者へ見せる警告（純関数・決定的）。
//
// 取引判断は確定済みの日報の方針（PolicySummary）**だけ**を読む（FR-04「当日の日報の方針」の範囲内。週報の本文は判断へ渡らない）。
// 「週次方針 weekly-2026-W41 のしきい値に従う」のような方針では、判断はしきい値を確かめられず「不確実なら見送り」で見送り続ける
// （PoC の実測 2026-10-15）。しきい値は日報の方針の中に数値で書き写す（計画の粒度: 日報＝翌営業日の売買条件・銘柄別の具体値）。
//
// 🔴 **警告であり、確定は止めない**（ADR-0051 決定 2 と同じ。止めるのは計画の水準の統制である）。
// 🔴 **警告は方針の本文へ入れない**（PolicyTakeProfitCheck と同じ。方針はそのまま判断へ渡る）。文はコード定数で、方針の文字列を含めない。
// 🔴 生成器の注記行（「上位方針（週報）は確定済みのものがないため参照していません。」等）は対象外——ReportPolicyDraft.Substance で除いてから見る。
// 対象は日報だけである（週報・月報の方針は上位を参照してよい。判断へ渡るのは日報の方針だけ）。
public static class PolicySelfContainedCheck
{
    /// <summary>警告の全文（先頭は契約アセンブリの印）。</summary>
    public const string Warning =
        ReportSummaryMarkers.PolicyExternalReferencePrefix
        + "（確定はできます）: 取引判断の AI が読むのは日報の方針だけで、週報・月報・前日の日報の本文は渡りません。"
        + "方針が「週次方針のしきい値に従う」「weekly-2026-W41 の条件」のように他の報告書や期間キーを参照していると、判断は条件の成立を確かめられず見送りに倒れます。"
        + "買い条件・売り条件のしきい値は、参照先から数値を書き写して日報の方針の中に書いてください（例「AAPL: 前日比 -3% 以下で買い」）。"
        + "説明の文でも、報告書の名前（週報・月報・日報・上位方針など）と期間キーを使わないでください。";

    // 報告書の期間キー（ReportPeriod の自然キーの書式。大文字小文字・全角は問わない）。
    private static readonly Regex PeriodKey = new(
        @"(daily|weekly|monthly)\s*[-‐－ー_]\s*\d{4}\s*[-‐－ー_]\s*(W\d{1,2}|\d{1,2}(\s*[-‐－ー_]\s*\d{1,2})?)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    // 他の報告書の方針を指す語。判断はこれらの本文を受け取らない。
    // 「本日報告」「前日報道」「毎日報じる」「月報酬」のように語の一部として現れる並びは除く（後ろの 1 文字で見分ける）。
    private static readonly Regex ReportWord = new(
        @"(週|月|日)報(?![告道じ酬])|上位方針|(週次|月次|日次)の?方針|(前日|昨日|前営業日|先週|前週|今週|当週|先月|前月|今月|当月)の方針",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>警告が要るなら <see cref="Warning"/>、要らなければ null。日報以外は常に null。</summary>
    public static string? WarningFor(ReportKind kind, string? policy) =>
        kind == ReportKind.Daily && ReferencesOtherReport(policy) ? Warning : null;

    // 方針の実体（生成器の注記行を除く）が、他の報告書を指す語か期間キーを含むか。
    private static bool ReferencesOtherReport(string? policy)
    {
        var substance = ReportPolicyDraft.Substance(policy).Normalize(System.Text.NormalizationForm.FormKC);
        return substance.Length > 0 && (PeriodKey.IsMatch(substance) || ReportWord.IsMatch(substance));
    }
}

// FR-04, FR-07, #1365, IADR-0544 決定 3: 方針の警告（利確・数値目標・他の報告書の参照）を確定の前に見せる 1 つの文へ並べる。
// 日報では利確の警告と参照の警告が両方出得る（どちらか片方だけにすると、直した後にもう片方が初めて見える）。
public static class PolicyDraftWarnings
{
    /// <summary>null・空を除いて行で並べる。1 つも無ければ null。</summary>
    public static string? Join(params string?[] warnings)
    {
        var present = warnings.Where(w => !string.IsNullOrEmpty(w)).ToList();
        return present.Count == 0 ? null : string.Join("\n", present);
    }
}

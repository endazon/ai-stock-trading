using System.Globalization;
using System.Text.RegularExpressions;
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
// 「警告が出ない方針なら判断側も条件を読める」を、2 つの実装の食い違いで崩さないため。
//
// FR-04, FR-07, ADR-0051 決定 4・フォローアップ 1, #1223, IADR-0470（2026-10-08 追記）: **保有中の銘柄ごとにも見る。**
// 方針全体に読める行があっても、保有中の銘柄 X に掛かる行（X の行も「全銘柄」の行も）が無ければ、判断は X の利確条件を読めない
// （判断と同じ ForSymbol で判定する）。その銘柄を名指しして警告する。🔴 **新規建ての対象は銘柄ごとに見ない**——構造化された一覧が無く
// （監視銘柄は新規建ての対象より広い）、判断が利確の条件を読むのは保有になってからであり、保有になれば次の地点でこの判定に掛かる。
// 🔴 保有中の銘柄が得られない（照会の失敗・未構成・時点に復元できない）ときは、方針全体の行の有無の判定へ戻す（黙って省かない。受け入れ基準 3）。
// 対象は日報だけである（裁定は「日報の方針」。週報・月報の方針は銘柄別の売買条件の粒度を持たない〔04_report-templates 粒度の対応表〕）。
public static class PolicyTakeProfitCheck
{
    /// <summary>警告の全文（先頭は契約アセンブリの印）。</summary>
    public const string Warning =
        ReportSummaryMarkers.PolicyTakeProfitMissingPrefix
        + "（確定はできます）: システムは方針の利確の条件を読めず、取引判断に条件への到達・未到達を知らせません（保有継続〔Hold〕に倒れやすくなります）。"
        + "保有中の銘柄と新規建ての対象ごとに、方針へ「利確: AAPL +5%」（平均取得単価からの率。+ は必須）や「利確: MSFT $450 (50%)」（価格と一部利確の割合）の形の行を 1 行ずつ足してください"
        + "（すべての銘柄なら「利確: 全銘柄 +8%」）。銘柄の行に書けるのは英字のティッカーだけです（日本株など数字のコードの銘柄は「全銘柄」の行で扱ってください）。"
        // #1129 第 4 回監査 R1: 候補はコロンの有無を問わず「利確」を含む行のすべて（説明の文・見出し・表・語の一部も含む）。
        + "「利確」の語を含む行（コロンの有無を問わず、説明の文・見出し・表・「権利確定日」のような語の一部も含む）が 1 行でもこの形に合わないと、その方針の利確の条件は 1 つも読まれません。"
        + "説明の文では「利確」の語を使わないでください。銘柄ごとの例外もその銘柄の「利確:」行で書いてください（説明の文の例外は読まれません）。";

    /// <summary>銘柄ごとの警告で名指しする銘柄の上限（超えれば「ほか N 件」）。</summary>
    public const int MaxNamedSymbols = 10;

    // 名指しに使う銘柄の表記（英数字と . - だけ。台帳の銘柄はこの形であり、外れた値は名指しせず件数にだけ数える）。
    private static readonly Regex DisplayableSymbol = new(@"\A[A-Z0-9][A-Z0-9.\-]{0,15}\z", RegexOptions.CultureInvariant);

    /// <summary>
    /// 警告が要るなら警告の全文、要らなければ null。<paramref name="heldPositions"/> は保有中の建玉（null＝得られない）。
    /// 方針全体に読める行が無ければ <see cref="Warning"/>。あれば、保有中の銘柄のうち掛かる行が無いものを名指しする（<see cref="HeldSymbolsWarning"/>）。
    /// 建玉が得られなければ方針全体の判定だけを行う。
    /// </summary>
    public static string? WarningFor(ReportKind kind, string? policy, IReadOnlyList<ReportPosition>? heldPositions = null)
    {
        if (kind != ReportKind.Daily)
            return null;
        if (!PolicyTakeProfitConditions.HasAny(policy))
            return Warning;
        if (heldPositions is null)
            return null;

        var uncovered = HeldSymbolsWithoutLine(policy, heldPositions);
        return uncovered.Count == 0 ? null : HeldSymbolsWarning(uncovered);
    }

    /// <summary>保有中の銘柄のうち、方針に掛かる「利確:」行（その銘柄の行も「全銘柄」の行も）が無いもの（大文字・重複なし・序数順）。</summary>
    public static IReadOnlyList<string> HeldSymbolsWithoutLine(string? policy, IReadOnlyList<ReportPosition> heldPositions)
    {
        ArgumentNullException.ThrowIfNull(heldPositions);
        return heldPositions
            .Where(p => p is not null && p.Quantity != 0 && !string.IsNullOrWhiteSpace(p.Symbol))
            .Select(p => p.Symbol.Trim().ToUpperInvariant())
            .Distinct(StringComparer.Ordinal)
            .Where(s => PolicyTakeProfitConditions.ForSymbol(policy, s).Count == 0)
            .Order(StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>保有中の銘柄を名指しする警告の全文（先頭は契約アセンブリの印）。</summary>
    public static string HeldSymbolsWarning(IReadOnlyList<string> symbols)
    {
        ArgumentNullException.ThrowIfNull(symbols);
        var shown = symbols.Where(s => DisplayableSymbol.IsMatch(s)).Take(MaxNamedSymbols).ToList();
        var rest = symbols.Count - shown.Count;
        var names = shown.Count == 0
            ? $"{symbols.Count.ToString(CultureInfo.InvariantCulture)} 銘柄"
            : string.Join("・", shown) + (rest > 0 ? $" ほか {rest.ToString(CultureInfo.InvariantCulture)} 件" : string.Empty);
        return ReportSummaryMarkers.PolicyTakeProfitMissingPrefix
            + $"（保有中の銘柄 {names} ／ 確定はできます）: これらの銘柄に掛かる「利確:」行（その銘柄の行も「全銘柄」の行も）が無く、"
            + "システムは取引判断にこれらの銘柄の利確の条件への到達・未到達を知らせません（保有継続〔Hold〕に倒れやすくなります）。"
            + "銘柄ごとに「利確: AAPL +5%」の形の行を足すか、「利確: 全銘柄 +8%」の行を足してください"
            + "（日本株など数字のコードの銘柄は「全銘柄」の行で扱ってください）。";
    }
}

using System.Globalization;
using System.Text;
using ReportService.Domain;

namespace ReportService.Features.Reports;

// FR-06/16, IADR-0071 決定1: 散文ドラフトの LLM プロンプトを純関数で決定的に構築する。
// 数値は集計済みの参考値として提示するが、「散文のみ・数値は再計算/改変しない（数値はコード集計が権威・FR-16）」を明示する。
// プロンプト構築を Application に置くことで、実 LLM 実装（Worker の HttpReportNarrativeDrafter）と分離しテスト可能にする。
public static class ReportNarrativePromptBuilder
{
    public static string Build(ReportNarrativeContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var kindLabel = context.Kind switch
        {
            ReportKind.Daily => "日報",
            ReportKind.Weekly => "週報",
            ReportKind.Monthly => "月報",
            _ => context.Kind.ToString(),
        };

        var markets = context.Markets is { Count: > 0 } ? string.Join(", ", context.Markets) : "（指定なし）";
        var p = context.Pnl;

        var sb = new StringBuilder();
        sb.AppendLine($"あなたは株式取引の{kindLabel}の散文（市況所感・振り返り・翌期間の見通し）を日本語で作成するアシスタントです。");
        sb.AppendLine("以下の集計値はコードで確定済みです。これらは参考として提示するものであり、");
        sb.AppendLine("あなたは散文のみを作成してください。数値の再計算・改変・新たな数値の創作はしないでください（数値はコード集計が唯一の権威です）。");
        sb.AppendLine();
        sb.AppendLine($"- 種別: {kindLabel}");
        sb.AppendLine($"- 対象期間: {context.PeriodLabel}（期間キー: {context.PeriodKey}）");
        sb.AppendLine($"- 対象市場: {markets}");
        sb.AppendLine();
        // FR-06, FR-16, #892, IADR-0381: 🔴 **部分値を「確定済みの集計値」として LLM へ渡さない。**
        // 期間より前に建てた建玉の決済があると、実現損益・税・評価損益・決済件数・勝ち件数は部分値である
        // （報告書の在庫は当期間の約定だけから畳まれ、その建玉の取得原価を持たない）。本文・要約は
        // 「算出不能」と描くのに、散文だけが部分値を権威として受け取ると「当期は決済が無かった」
        // 「損益 0 だった」と書けてしまう。**値そのものを渡さず**、言及しないよう指示する。
        // 費用・約定件数は約定ごとに数え取得原価を要さないため、そのまま渡す。
        var unvalued = p.UnvaluedSettlementCount > 0
            ? string.Format(CultureInfo.InvariantCulture, "算出不能（{0}件）", p.UnvaluedSettlementCount)
            : null;

        sb.AppendLine("集計値（参考・再計算不可）:");
        sb.AppendLine($"- 実現損益(税引前): {unvalued ?? Num(p.RealizedPnlGross)}");
        sb.AppendLine($"- 費用合計: {Num(p.TotalCost)}");
        sb.AppendLine($"- 源泉徴収税額: {unvalued ?? Num(p.TaxWithheld)}");
        sb.AppendLine($"- 実現損益(税引後): {unvalued ?? Num(p.RealizedPnlNet)}");
        sb.AppendLine($"- 評価損益(参考): {unvalued ?? Num(p.UnrealizedPnl)}");
        sb.AppendLine($"- 約定件数: {p.TradeCount} / 決済件数: {unvalued ?? Count(p.RealizingTradeCount)} / 勝ち決済: {unvalued ?? Count(p.WinningTradeCount)}");
        if (unvalued is not null)
        {
            sb.AppendLine(string.Format(CultureInfo.InvariantCulture,
                "注意: 当期間には期間より前に建てた建玉の決済が {0} 件あり、その取得原価が当期間の約定に含まれていないため、"
                    + "実現損益・源泉徴収税額・評価損益・決済件数・勝ち決済は算出できていません（上記の「算出不能」）。",
                p.UnvaluedSettlementCount));
            sb.AppendLine("これらの値・増減・勝敗・決済の有無には散文で一切言及しないでください。"
                + "「決済が無かった」「損益は 0 だった」「勝ち越した／負け越した」等とも書かないでください。");
        }

        sb.AppendLine();
        sb.AppendLine($"翌期間の方針要旨（参考）: {context.PolicySummary}");
        sb.AppendLine();

        // FR-07, IADR-0120 決定3, #293, 04_workflows/03_reporting-cycle:
        // 上位方針（日報→週報 / 週報→月報 / 月報→前月の月報）の本文を提示し、差異評価を求める。
        // 計画の業務フローは「AI がドラフト生成＝上位方針の目標との差異評価＋翌期間の目標案」と定めており、
        // 期間キーだけでは差異評価が書けない。上位が未確定なら**その旨を明記**する（捏造させない）。
        var parentLabel = ParentLabel(context.Kind);
        if (context.ParentPolicy is { } parent)
        {
            sb.AppendLine($"上位方針（{parentLabel}・{parent.PeriodKey}・確定済み）:");
            sb.AppendLine(parent.Summary.Trim());
            sb.AppendLine();
            sb.AppendLine($"当期の振り返りでは、上記の{parentLabel}方針の目標に対する達成度と差異を評価してください。");
        }
        else
        {
            sb.AppendLine($"上位方針（{parentLabel}）は未確定のため参照していません。上位方針との差異評価は行わず、その旨を散文に明記してください。");
        }

        sb.AppendLine();
        sb.AppendLine("上記を踏まえ、市況所感・当期の振り返り・翌期間の見通しを簡潔な散文で述べてください。数値の羅列や再計算はしないこと。");

        return sb.ToString();
    }

    // 上位の呼称。月報の上位は「前月の月報」であり、自種別の呼称と紛れないようにする
    // （ReportPolicyDraft.ParentKind(Monthly) == Monthly＝最上位ゆえ自種別を遡るため）。
    private static string ParentLabel(ReportKind kind) => kind switch
    {
        ReportKind.Daily => "週報",
        ReportKind.Weekly => "月報",
        _ => "前月の月報",
    };

    private static string Num(decimal value) => value.ToString("0.####", CultureInfo.InvariantCulture);

    private static string Count(int value) => value.ToString(CultureInfo.InvariantCulture);
}

// FR-06/16, IADR-0071 決定1: 散文ドラフトの安全既定文。実 LLM 未接続時（PlaceholderReportNarrativeDrafter）と、
// 実 LLM が送信拒否/失敗/タイムアウトした fail-safe 時（HttpReportNarrativeDrafter）の両方で同一の定型文を用いる。
// 捏造せず「LLM 未接続の旨」だけを述べ、数値はコード集計値を参照させる（数値には一切関与しない）。
public static class ReportNarrativeDefaults
{
    public const string PlaceholderText =
        "（本節は LLM 未接続のため自動ドラフトされていません。数値は上記のコード集計値を参照してください。）";
}

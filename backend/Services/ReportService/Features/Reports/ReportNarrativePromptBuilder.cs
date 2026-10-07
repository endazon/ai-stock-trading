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
        // #1181, IADR-0493 決定 4: 期間開始時点の在庫を照会できなかったときも同じく値を渡さない（件数が無ければ件数を書かない）。
        var unvalued = p.UnvaluedSettlementCount > 0
            ? string.Format(CultureInfo.InvariantCulture, "算出不能（{0}件）", p.UnvaluedSettlementCount)
            : p.OpeningInventoryUnknown ? "算出不能" : null;

        // FR-06, FR-16, #1156, IADR-0480 決定 1: 🔴 **未供給を 0 として渡さない。** 約定は不達でも空列へ倒れる
        // （IADR-0115 決定5）ため、値は 0 件・損益 0 になる。それを確定値として渡すと「取引なし」「損益 0」と書ける。
        // 手動売買の取り込みが欠けると在庫の畳み込みが崩れ、評価損益は実在しない建玉の値になり得る（IADR-0360）。
        var unsupplied = context.UnsuppliedInputs.ToHashSet();
        var fillsUnsupplied = unsupplied.Contains(ReportInput.Fills) ? UnsuppliedValue : null;
        var unrealizedUnsupplied = fillsUnsupplied
            ?? (unsupplied.Contains(ReportInput.DriftAdoptions) ? UnsuppliedValue : null);

        sb.AppendLine("集計値（参考・再計算不可）:");
        // 計画 ADR-0035 決定 1, #1201, IADR-0501: 🔴 費用・税の控除前の値を「実現損益」と呼ばない（LLM が本文へ写し得る）。
        sb.AppendLine($"- 約定代金差額(費用・税の控除前): {fillsUnsupplied ?? unvalued ?? Num(p.RealizedPnlGross)}");
        // #1156, IADR-0480 決定 2: 費用合計は**概算**である（前提条件の料率から算出した売買手数料・取引諸費用）。
        // 実際の経費明細は本サービスへ取り込まれていない（#1086）。0 を「費用負担は無かった」と読ませない。
        // 🔴 行頭の「- 費用合計: <値>」の形は変えない（既存の試験・読み手が値をこの形で引く）。
        sb.AppendLine($"- 費用合計: {fillsUnsupplied ?? Num(p.TotalCost)}（概算）");
        sb.AppendLine($"- 源泉徴収税額: {fillsUnsupplied ?? unvalued ?? Num(p.TaxWithheld)}");
        sb.AppendLine($"- 実現損益(税引後): {fillsUnsupplied ?? unvalued ?? Num(p.RealizedPnlNet)}");
        sb.AppendLine($"- 評価損益(参考): {unrealizedUnsupplied ?? unvalued ?? Num(p.UnrealizedPnl)}");
        sb.AppendLine($"- 約定件数: {fillsUnsupplied ?? Count(p.TradeCount)} / 決済件数: {fillsUnsupplied ?? unvalued ?? Count(p.RealizingTradeCount)} / 勝ち決済: {fillsUnsupplied ?? unvalued ?? Count(p.WinningTradeCount)}");
        sb.AppendLine(CostEstimateNote);
        sb.AppendLine(UnrealizedScopeNote);
        if (fillsUnsupplied is not null)
        {
            sb.AppendLine("注意: 当期間の約定を取得できませんでした（未供給）。取引の有無・件数・損益・費用には散文で一切言及しないでください。"
                + "「取引は無かった」「損益は 0 だった」等とも書かないでください。");
        }
        if (unvalued is not null)
        {
            sb.AppendLine(p.UnvaluedSettlementCount > 0
                ? string.Format(CultureInfo.InvariantCulture,
                    "注意: 当期間には期間より前に建てた建玉の決済が {0} 件あり、その取得原価が当期間の約定に含まれていないため、"
                        + "実現損益・源泉徴収税額・評価損益・決済件数・勝ち決済は算出できていません（上記の「算出不能」）。",
                    p.UnvaluedSettlementCount)
                : "注意: 期間開始時点の在庫を照会できなかったため、持ち越した建玉の取得原価が分からず、"
                    + "実現損益・源泉徴収税額・評価損益・決済件数・勝ち決済は算出できていません（上記の「算出不能」）。");
            sb.AppendLine("これらの値・増減・勝敗・決済の有無には散文で一切言及しないでください。"
                + "「決済が無かった」「損益は 0 だった」「勝ち越した／負け越した」等とも書かないでください。");
        }

        // FR-06, FR-16, #1156, IADR-0480 決定 1: 建玉の状態を**3 通りで**渡す（未供給・0 件・N 件）。
        // 渡さなかった是正前は、LLM が評価損益 0 と約定 0 件から「参照すべき建玉がなく」と書いた（実際は 3 銘柄を保有）。
        sb.AppendLine();
        AppendPositions(sb, context);

        // FR-06, FR-16, #1156, IADR-0480 決定 1: 取得できなかった入力の一覧。**未供給が無ければ節ごと出さない**
        // （供給されている入力を「取得できなかった」と言わせない。05_screens の逆向きの禁止と同じ規律）。
        if (context.UnsuppliedInputs.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine($"取得できなかった入力（未供給）: {string.Join("、", ReportInputs.Labels(context.UnsuppliedInputs))}");
            sb.AppendLine(UnsuppliedRule);
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

    /// <summary>FR-06, #1156, IADR-0480 決定 1: 未供給の値を表す文言（0・「—」・空欄で表さない）。</summary>
    public const string UnsuppliedValue = "未供給（取得できなかった）";

    /// <summary>FR-06, #1156, IADR-0480 決定 1: 未供給の入力について散文が守る規則。</summary>
    public const string UnsuppliedRule =
        "上記の入力は「無い」「0」「発生しなかった」のではなく、値が分かりません。散文で触れるときは"
        + "「未供給（取得できなかった）」と書き、「無い」「0」「なかった」と言い切らないでください。";

    /// <summary>
    /// FR-06, FR-16, #1156, IADR-0480 決定 2: 費用合計の注記。費用合計は前提条件からの概算で、実際の経費明細は
    /// 取り込まれていない（#1086）。🔴 経費明細を取り込んだら、この注記を見直す（IADR-0480 の残余）。
    /// </summary>
    public const string CostEstimateNote =
        "注意: 費用合計は前提条件の料率から算出した概算（売買手数料と取引諸費用）です。"
        + "実際の経費明細は取り込まれておらず、為替スプレッド・借株料を含みません。"
        + "費用合計が 0 でも「費用負担は無かった」「費用は発生しなかった」とは書かないでください。";

    /// <summary>FR-06, FR-16, #1156, IADR-0480 決定 1: 評価損益の範囲の注記（建玉の有無を推測させない）。</summary>
    public const string UnrealizedScopeNote =
        "注意: 評価損益(参考)は当期間の約定から畳んだ建玉だけの値で、期間より前から保有している建玉を含みません。"
        + "この値や約定件数から、建玉の有無・保有状況を推測しないでください。";

    // FR-06, FR-16, #1156, IADR-0480 決定 1: 建玉の状態。日報だけが建玉を入力に持つ（ReportInputs.AppliesTo）。
    // 銘柄コードは自サービスの台帳（リスク管理の射影）由来であり、外部の自由文ではない。
    private static void AppendPositions(StringBuilder sb, ReportNarrativeContext context)
    {
        if (context.Kind != ReportKind.Daily)
        {
            sb.AppendLine("建玉: 本報告書の入力に含まれていません。建玉の有無・保有状況には散文で言及しないでください。");
            return;
        }

        if (context.Positions is not { } positions)
        {
            sb.AppendLine($"建玉（現在の台帳）: {UnsuppliedValue}");
            sb.AppendLine("建玉の有無・保有状況・評価損益には散文で触れないでください。「建玉なし」「ポジションを保有していない」とも書かないでください。");
            return;
        }

        if (positions.Count == 0)
        {
            sb.AppendLine("建玉（現在の台帳）: 0 件（建玉なし）");
            return;
        }

        var symbols = positions.Select(p => p.Symbol).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal);
        sb.AppendLine(string.Format(CultureInfo.InvariantCulture,
            "建玉（現在の台帳）: {0} 件（銘柄: {1}）", positions.Count, string.Join(", ", symbols)));
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

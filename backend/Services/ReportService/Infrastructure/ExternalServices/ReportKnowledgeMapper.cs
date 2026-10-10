using System.Globalization;
using ReportService.Domain;
using AiStockTrading.Shared.KnowledgeBase;
using Microsoft.Extensions.Logging;

namespace ReportService.Infrastructure.ExternalServices;

// FR-08, IADR-0069/0071 決定3, #565, IADR-0274［2026-09-03 追記］: 確定報告書を KB カタログ文書
// （KnowledgeDocument）へ写像する。本文（Markdown 実体・TradingReport.Body）は platform 側 POST /documents
// が Body として受け取れるようになった（IADR-0274 で IADR-0069 のスコープ境界は解消済み）ため、
// 非空なら Content へそのまま渡す（ContentType は text/markdown）。空（string.Empty）は「未供給」
// （手動 PUT /reports/{periodKey} 経路は本文を受け取らないため常に空になる。TradingReport.Body 参照）
// として Content: null に倒し、空文字列をそのまま「本文あり（0 文字）」として送らない
// （IADR-0274［2026-09-03 追記］）。機密区分は internal（取引の判断根拠は社外秘扱いが妥当）。
public static class ReportKnowledgeMapper
{
    // FR-08, #1028, IADR-0436 決定 2: 報告書の写しを KB の一覧から探す鍵（project と併せて外部 ID の代わりにする）。
    // 基盤に外部 ID での照会・upsert が無いため、入れ直し（ReportKnowledgeReingestService）が同じ名前で突き合わせる。
    public const string PeriodKeyAttribute = "periodKey";
    public const string KindAttribute = "kind";

    // FR-08, #1028, IADR-0436 決定 2［2026-09-26 PR #1038 の監査］: KB 文書の表題。2026-07-18（#169）から変わっていない。
    // project 属性（#665・2026-09-03）より前の写しは project を持たないため、入れ直しはこの表題との完全一致を
    // 「AST が書いた写し」の目印に使う（表題を変えると旧い写しを見失い、重複を作る）。
    public static string TitleOf(ReportKind kind, string periodKey) => $"確定報告書 {kind} {periodKey}";

    public static KnowledgeDocument ToDocument(TradingReport report, ILogger? logger = null)
    {
        ArgumentNullException.ThrowIfNull(report);

        var kind = report.Kind.ToString();
        var attributes = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [PeriodKeyAttribute] = report.PeriodKey,
            [KindAttribute] = kind,
            ["assumptionsVersion"] = report.AssumptionsVersion.ToString(CultureInfo.InvariantCulture),
            // FR-08, UC-01 手順 3, #1138, IADR-0474 決定1: 報告書は銘柄を持たない。判断側の 2 本目の検索（coverage=market の
            // 単値フィルタ）で引けるよう目印を付ける。付けないと、目印つきの収集情報が K 件たまった後は報告書だけが届かなくなる。
            [KnowledgeSearchAttributes.Coverage] = KnowledgeSearchAttributes.MarketCoverage,
        };
        if (report.ConfirmedAt is { } confirmedAt)
            attributes["confirmedAt"] = confirmedAt.ToString("O", CultureInfo.InvariantCulture);

        // #565, IADR-0274［2026-09-03 追記］: 空文字列は「未供給」であり「意図的な空の本文」ではない。
        // 未供給と空を区別するため null へ倒し、運用者が気づけるよう警告ログを残す（例外にはしない。
        // KB 保存は既存どおり best-effort であり確定を壊さない）。
        var hasBody = !string.IsNullOrEmpty(report.Body);
        if (!hasBody)
        {
            logger?.LogWarning(
                "確定報告書 {PeriodKey} は本文が空のため KB へ本文を送りません（手動確定など自動生成を経ていない可能性）。",
                report.PeriodKey);
        }

        return new KnowledgeDocument(
            Title: TitleOf(report.Kind, report.PeriodKey),
            Content: hasBody ? report.Body : null,
            Confidentiality: KnowledgeConfidentiality.Internal,
            Tags: ["report", kind.ToLowerInvariant()],
            SourceUri: null,
            ContentType: hasBody ? "text/markdown" : null,
            Attributes: attributes);
    }

    // ===== 承認待ちの報告書の写し（ドラフト）。FR-06, FR-08, UC-03, #1300, IADR-0526 決定 1 =====

    // 表題。確定版（TitleOf）と必ず分ける —— 入れ直しと MSP の写しの棚卸しは確定版の表題との完全一致を目印に使う。
    public static string DraftTitleOf(ReportKind kind, string periodKey) =>
        $"{KnowledgeReportDraftCopy.TitlePrefix}{kind} {periodKey}";

    /// <summary>
    /// ドラフトの写しの属性。<b>露出の 3 キーを必ず <c>excluded</c> で持つ</b>（欠けると基盤がその用途で索引する）。
    /// <c>coverage=market</c> は付けない（取引判断の 2 本目の検索の目印。ドラフトを判断へ渡さない）。
    /// <para>
    /// 🔴 ドラフトの写しの属性はこの関数だけが組み立てる。基盤の属性の更新（PATCH）は全置換のため、別の場所で組み立てると
    /// 露出のキーが落ちて検索に出る。本変更は PATCH を使わない（本文の差し替えだけ）。
    /// </para>
    /// </summary>
    public static Dictionary<string, string> DraftAttributesOf(ReportKind kind, string periodKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(periodKey);

        var attributes = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [PeriodKeyAttribute] = periodKey,
            [KindAttribute] = kind.ToString(),
            [KnowledgeReportDraftCopy.StateKey] = KnowledgeReportDraftCopy.DraftState,
        };
        foreach (var key in KnowledgeExposureAttributes.Keys)
            attributes[key] = KnowledgeExposureAttributes.Excluded;
        return attributes;
    }

    /// <summary>
    /// ドラフトの写しの本文。先頭に「承認待ち・版・確定で置き換わる」を書く（基盤の表題は変えられないので、版は本文が運ぶ）。
    /// 本文が空（月報の初回のブートストラップ・手動の PUT）なら方針を載せる。
    /// </summary>
    public static string DraftBodyOf(TradingReport report, int version)
    {
        ArgumentNullException.ThrowIfNull(report);

        var header = $"> 承認待ちの報告書（ドラフト・版 {version.ToString(CultureInfo.InvariantCulture)}）。"
            + "確定前の本文であり、検索・取引判断には使われない。確定すると確定版に置き換わる。";

        string content;
        if (!string.IsNullOrEmpty(report.Body))
            content = report.Body;
        else if (!string.IsNullOrWhiteSpace(report.PolicySummary))
            content = $"## 翌期間の方針\n\n{report.PolicySummary}";
        else
            content = "（本文はありません）";

        return $"{header}\n\n{content}";
    }

    public static KnowledgeDocument ToDraftDocument(TradingReport report, int version)
    {
        ArgumentNullException.ThrowIfNull(report);

        return new KnowledgeDocument(
            Title: DraftTitleOf(report.Kind, report.PeriodKey),
            Content: DraftBodyOf(report, version),
            Confidentiality: KnowledgeConfidentiality.Internal,
            // タグは確定版と同じ登録済みの語彙だけ（基盤のタグ辞書は未登録のタグを 400 で拒否する）。
            Tags: ["report", report.Kind.ToString().ToLowerInvariant()],
            SourceUri: null,
            ContentType: "text/markdown",
            Attributes: DraftAttributesOf(report.Kind, report.PeriodKey));
    }

    /// <summary>
    /// KB の文書がドラフトの写し（どの報告書のものかを問わない）か。<c>reportState=draft</c> を持ち、<b>かつ</b>表題が
    /// <c>報告書ドラフト </c> で始まるときだけ真。
    /// <para>
    /// 🔴 露出の 3 キーが全部 <c>excluded</c> であることは写しの目印にしない（#1300 の監査）。基盤では露出を全部除外にするのが
    /// 文書を隠す通常の操作であり、管理者が確定版の写しを隠しただけで「残った写し」と読んで消し、検索に出る写しを作り直してしまう。
    /// 2 つの条件を両方求めるのは、確定版の写し（表題 <c>確定報告書 …</c>・<c>reportState</c> なし）がどちらの条件でも一致しないようにするため。
    /// </para>
    /// </summary>
    public static bool IsDraftCopy(KnowledgeCatalogEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);

        return entry.Attributes.TryGetValue(KnowledgeReportDraftCopy.StateKey, out var state)
            && string.Equals(state, KnowledgeReportDraftCopy.DraftState, StringComparison.Ordinal)
            && KnowledgeReportDraftCopy.IsDraftTitle(entry.Title);
    }

    /// <summary>この報告書（期間キー・種別）の、AST が作ったドラフトの写しか（project=ai-stock-trading を持つものだけ）。</summary>
    public static bool IsDraftCopyOf(KnowledgeCatalogEntry entry, ReportKind kind, string periodKey) =>
        IsDraftCopy(entry)
        && entry.Attributes.TryGetValue(PeriodKeyAttribute, out var pk) && string.Equals(pk, periodKey, StringComparison.Ordinal)
        && entry.Attributes.TryGetValue(KindAttribute, out var k) && string.Equals(k, kind.ToString(), StringComparison.Ordinal)
        && entry.Attributes.TryGetValue(KnowledgeAttributeDefaults.ProjectKey, out var project)
        && string.Equals(project, KnowledgeAttributeDefaults.RequiredProject, StringComparison.Ordinal);
}

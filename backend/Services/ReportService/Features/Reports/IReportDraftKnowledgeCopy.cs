using ReportService.Domain;

namespace ReportService.Features.Reports;

// FR-06, FR-08, UC-03, #1300, IADR-0526: 承認待ちの報告書の写し（ドラフト）を KB に 1 件だけ持つポート（planning#784 の利用者裁定 2026-10-10）。
//
// 写しは基盤の組織文書で、露出の 3 属性を全部 `excluded` にする —— SC-03 で読めるが、検索・RAG・グラフ・Wiki・外部 AI エージェント向けの文書一覧・
// 取引判断の KB 検索には出ない。承認待ちへ移るたびに本文を差し替え、確定したら（確定版を作った後で）消す。
//
// 🔴 **どのメソッドも例外を投げない**（呼び出し元の取り消しだけは伝播する）。KB の失敗で報告書の生成・提示・確定を止めない
// （確定時の KB 保存と同じ best-effort）。構成 `ReportDraftKnowledge:Enabled`（既定 false）が偽なら何もしない＝従来の挙動。
public interface IReportDraftKnowledgeCopy
{
    /// <summary>承認待ちにした版の本文で写しを作る（初回）か、同じ写しの本文を差し替える（2 回目以降）。</summary>
    Task PublishAsync(TradingReport report, int version, CancellationToken cancellationToken = default);

    /// <summary>確定版の写しを作った後に、この報告書のドラフトの写しを消す。</summary>
    Task RemoveAsync(ReportKind kind, string periodKey, CancellationToken cancellationToken = default);
}

// FR-08, #1300, IADR-0526 決定 5: 機能の門（構成 `ReportDraftKnowledge:Enabled`・既定 false）。
// 🔴 MSP#1886（`ccbc4a3b` 以降）の配備の前に有効にしない —— それより前の基盤は Wiki 同期が露出を見ず、ドラフトを Wiki.js に載せる。
public sealed record ReportDraftKnowledgeOptions(bool Enabled)
{
    public const string EnabledKey = "ReportDraftKnowledge:Enabled";

    public static ReportDraftKnowledgeOptions Read(string? value) =>
        new(bool.TryParse(value, out var enabled) && enabled);
}

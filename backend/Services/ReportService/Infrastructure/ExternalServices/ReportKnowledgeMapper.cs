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
    public static KnowledgeDocument ToDocument(TradingReport report, ILogger? logger = null)
    {
        ArgumentNullException.ThrowIfNull(report);

        var kind = report.Kind.ToString();
        var attributes = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["periodKey"] = report.PeriodKey,
            ["kind"] = kind,
            ["assumptionsVersion"] = report.AssumptionsVersion.ToString(CultureInfo.InvariantCulture),
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
            Title: $"確定報告書 {kind} {report.PeriodKey}",
            Content: hasBody ? report.Body : null,
            Confidentiality: KnowledgeConfidentiality.Internal,
            Tags: ["report", kind.ToLowerInvariant()],
            SourceUri: null,
            ContentType: hasBody ? "text/markdown" : null,
            Attributes: attributes);
    }
}

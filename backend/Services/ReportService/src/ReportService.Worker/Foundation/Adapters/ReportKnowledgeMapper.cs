using System.Globalization;
using AiStockTrading.Report.Domain;
using AiStockTrading.Shared.KnowledgeBase;

namespace AiStockTrading.Report.Worker.Foundation.Adapters;

// FR-08, IADR-0069/0071 決定3: 確定報告書を KB カタログ文書（KnowledgeDocument）へ写像する純関数。
// 本文（Markdown 実体）は現行 platform POST /documents が受けない（オブジェクトストレージ経路は platform 依存・IADR-0069）ため
// 送らない＝カタログ登録（Title/属性/タグ）のみ。機密区分は internal（取引の判断根拠は社外秘扱いが妥当）。
internal static class ReportKnowledgeMapper
{
    public static KnowledgeDocument ToDocument(TradingReport report)
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

        return new KnowledgeDocument(
            Title: $"確定報告書 {kind} {report.PeriodKey}",
            Content: null,
            Confidentiality: KnowledgeConfidentiality.Internal,
            Tags: ["report", kind.ToLowerInvariant()],
            SourceUri: null,
            ContentType: null,
            Attributes: attributes);
    }
}

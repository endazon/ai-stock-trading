using ReportService.Domain;
using ReportService.Infrastructure.ExternalServices;
using AiStockTrading.Shared.KnowledgeBase;
using AwesomeAssertions;
using Xunit;

namespace ReportService.Tests;

// FR-08, IADR-0069/0071 決定3: 確定報告書→KB カタログ文書の写像を検証する。機密区分 internal・タグ・属性を持ち、本文は送らない。
public class ReportKnowledgeMapperTests
{
    private static TradingReport Confirmed() => new()
    {
        PeriodKey = "daily-2026-07-18",
        Kind = ReportKind.Daily,
        PeriodStart = new DateOnly(2026, 7, 18),
        State = ReportState.Confirmed,
        AssumptionsVersion = 3,
        PolicySummary = "翌営業日は継続",
        ConfirmedAt = new DateTimeOffset(2026, 7, 18, 6, 0, 0, TimeSpan.Zero),
    };

    [Fact]
    public void タイトルに種別と期間キーを含む()
    {
        ReportKnowledgeMapper.ToDocument(Confirmed()).Title.Should().Be("確定報告書 Daily daily-2026-07-18");
    }

    [Fact]
    public void 機密区分は_internal_本文は送らない()
    {
        var doc = ReportKnowledgeMapper.ToDocument(Confirmed());

        doc.Confidentiality.Should().Be(KnowledgeConfidentiality.Internal);
        doc.Content.Should().BeNull(); // 本文（Markdown 実体）は platform POST /documents が受けないため送らない（IADR-0069）。
    }

    [Fact]
    public void タグと属性に期間キー_種別_前提条件バージョン_確定日時を含む()
    {
        var doc = ReportKnowledgeMapper.ToDocument(Confirmed());

        doc.Tags.Should().Contain(["report", "daily"]);
        doc.Attributes!["periodKey"].Should().Be("daily-2026-07-18");
        doc.Attributes["kind"].Should().Be("Daily");
        doc.Attributes["assumptionsVersion"].Should().Be("3");
        doc.Attributes.Should().ContainKey("confirmedAt");
    }
}

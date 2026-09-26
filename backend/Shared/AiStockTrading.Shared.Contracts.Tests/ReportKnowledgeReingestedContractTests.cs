using System.Text.Json;
using AiStockTrading.Shared.Contracts.Events;
using AiStockTrading.Shared.Contracts.Operations;
using AwesomeAssertions;
using Xunit;

namespace AiStockTrading.Shared.Contracts.Tests;

// T-10-1507, FR-08, FR-11, #1028, IADR-0436 決定 4: 入れ直しの監査イベントは wire（Web 既定）と監査台帳の Detail
// （AuditDetailJson）の両方で欠けずに往復する。内訳の行の型はイベントではない（EventTypeDiscovery に数えない）。
public class ReportKnowledgeReingestedContractTests
{
    private static ReportKnowledgeReingested Sample() => new(
        Guid.NewGuid(), "owner", "daily-2026-07-01..monthly-2026-08", RefreshExisting: true, "Completed", AbortReason: null,
        Targeted: 6, Created: 1, BodyAttached: 1, BodyRefreshed: 0, AlreadyPresent: 0,
        SkippedEmptyBody: 1, SkippedBodyTooLarge: 0, Failed: 1, Unknown: 1, NotAttempted: 1,
        DuplicatesInKb: 2, DuplicatePeriodKeys: ["daily-2026-07-14", "weekly-2026-W29"],
        [
            new ReportKnowledgeReingestEntry("daily-2026-07-10", "SkippedEmptyBody", "本文が空です。", null),
            new ReportKnowledgeReingestEntry("daily-2026-07-13", "Unknown", "タイムアウト", Guid.NewGuid()),
        ],
        BreakdownOmitted: 3, new DateTimeOffset(2026, 9, 26, 3, 0, 0, TimeSpan.Zero));

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void 入れ直しの監査イベントは欠けずに往復する(bool auditDetail)
    {
        var options = auditDetail ? AuditDetailJson.Options : new JsonSerializerOptions(JsonSerializerDefaults.Web);
        var original = Sample();

        var back = JsonSerializer.Deserialize<ReportKnowledgeReingested>(JsonSerializer.Serialize(original, options), options)!;

        back.Should().BeEquivalentTo(original);
        back.Breakdown.Should().Equal(original.Breakdown);
        back.DuplicatePeriodKeys.Should().Equal(original.DuplicatePeriodKeys);
        back.NotAttempted.Should().Be(1);
    }

    [Fact]
    public void 内訳の行の型はイベントとして数えない()
    {
        EventTypeDiscovery.GetEventTypes().Should().Contain(typeof(ReportKnowledgeReingested));
        EventTypeDiscovery.GetEventTypes().Should().NotContain(typeof(ReportKnowledgeReingestEntry));
    }
}

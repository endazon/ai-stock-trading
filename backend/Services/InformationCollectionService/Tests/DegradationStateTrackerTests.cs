using InformationCollectionService.Domain;
using InformationCollectionService.Hosted;
using AiStockTrading.Shared.Contracts.Events;
using AwesomeAssertions;
using Xunit;

namespace InformationCollectionService.Tests;

// FR-01, FR-09, FR-11, #336, ADR-0020 決定2-3: 欠測の遷移判定。
// **発生時刻・継続時間・該当サイクル数**（日報・月報が要求する 3 点）が回復イベントに載ることを固定する。
public class DegradationStateTrackerTests
{
    private static readonly DateTimeOffset T0 = new(2026, 8, 28, 9, 0, 0, TimeSpan.Zero);

    private static CollectionDegradation NewsOutage() => DegradationEvaluator.Evaluate(
        InformationSourceCatalog.Default,
        [SourceOutcome.Failed("finnhub-news"), SourceOutcome.Failed("google-news")]);

    private static CollectionDegradation Healthy() => DegradationEvaluator.Evaluate(
        InformationSourceCatalog.Default,
        [SourceOutcome.Ok("finnhub-news"), SourceOutcome.Ok("google-news")]);

    [Fact]
    public void 欠測へ入った瞬間だけ発行する()
    {
        var tracker = new DegradationStateTracker();

        var first = tracker.Observe(NewsOutage(), T0);
        var second = tracker.Observe(NewsOutage(), T0.AddMinutes(30));

        first.Should().ContainSingle().Which.Should().BeOfType<InformationSourceDegraded>();
        second.Should().BeEmpty("続いていることは日報が期間で示す（巡回ごとに発行しない）");
    }

    [Fact]
    public void 回復時に継続時間と該当サイクル数を載せる()
    {
        var tracker = new DegradationStateTracker();
        tracker.Observe(NewsOutage(), T0);                    // 1 巡回目（欠測へ）
        tracker.Observe(NewsOutage(), T0.AddMinutes(30));     // 2 巡回目
        tracker.Observe(NewsOutage(), T0.AddMinutes(60));     // 3 巡回目

        var events = tracker.Observe(Healthy(), T0.AddMinutes(90));

        var recovered = events.Should().ContainSingle().Which.Should().BeOfType<InformationSourceRecovered>().Which;
        recovered.Category.Should().Be(InformationSourceCatalog.NewsCategory);
        recovered.DegradedAt.Should().Be(T0);
        recovered.AffectedCycles.Should().Be(3);
        recovered.OutageDuration.Should().Be(TimeSpan.FromMinutes(90));
    }

    [Fact]
    public void 正常が続く間は何も発行しない()
    {
        var tracker = new DegradationStateTracker();

        tracker.Observe(Healthy(), T0).Should().BeEmpty();
        tracker.Observe(Healthy(), T0.AddMinutes(30)).Should().BeEmpty();
    }

    // 🔴 発行に失敗した分は状態を戻す。戻さないと「発行済み」として記録され、次の機会にも二度と出なくなる。
    [Fact]
    public void 発行失敗を巻き戻すと次の巡回で再発行する()
    {
        var tracker = new DegradationStateTracker();
        var published = tracker.Observe(NewsOutage(), T0).Single();

        tracker.Rollback(published);
        var retried = tracker.Observe(NewsOutage(), T0.AddMinutes(30));

        retried.Should().ContainSingle().Which.Should().BeOfType<InformationSourceDegraded>();
    }

    // カテゴリごとに独立して劣化・回復する（ニュース系と他の必須ソースが 1 本に混ざらない）。
    [Fact]
    public void 欠測はカテゴリごとに独立して追跡する()
    {
        var tracker = new DegradationStateTracker();
        var both = DegradationEvaluator.Evaluate(
            InformationSourceCatalog.Default,
            [SourceOutcome.Failed("finnhub-news"), SourceOutcome.Failed("google-news"), SourceOutcome.Failed("sec-edgar")]);

        var events = tracker.Observe(both, T0);

        events.OfType<InformationSourceDegraded>().Select(e => e.Category)
            .Should().BeEquivalentTo([InformationSourceCatalog.NewsCategory, "sec-edgar"]);
    }
}

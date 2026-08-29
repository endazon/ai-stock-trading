using InformationCollectionService.Domain;
using InformationCollectionService.Hosted;
using AiStockTrading.Shared.Contracts.Events;
using AwesomeAssertions;
using Xunit;

namespace InformationCollectionService.Tests;

// FR-01, FR-09, FR-11, #336, ADR-0020 決定2-3: 欠測の遷移判定。
// **発生時刻・継続時間・該当サイクル数**（日報・月報が要求する 3 点）が回復イベントに載ることを固定する。
//
// FR-10, #564, IADR-0267: あわせて**現況観測は毎巡回 1 件出る**ことを固定する。遷移イベントの抑止
// （洪水防止）と、現況観測の非抑止（鮮度の維持）は**別の規律**であり、片方の変更で他方が壊れないよう
// 両方を明示的に表明する。
public class DegradationStateTrackerTests
{
    private static readonly DateTimeOffset T0 = new(2026, 8, 28, 9, 0, 0, TimeSpan.Zero);

    private static readonly TimeSpan Validity = TimeSpan.FromHours(1);

    private static DegradationStateTracker NewTracker() => new(Validity);

    // 現況観測を除いた遷移イベントだけを見る（既存の表明の意図＝「遷移でのみ発行する」を保つ）。
    private static IReadOnlyList<object> Transitions(IReadOnlyList<object> events) =>
        [.. events.Where(e => e is not InformationSourceStateObserved)];

    private static InformationSourceStateObserved Observation(IReadOnlyList<object> events) =>
        events.OfType<InformationSourceStateObserved>().Single();

    private static CollectionDegradation NewsOutage() => DegradationEvaluator.Evaluate(
        InformationSourceCatalog.Default,
        [SourceOutcome.Failed("finnhub-news"), SourceOutcome.Failed("google-news")]);

    private static CollectionDegradation Healthy() => DegradationEvaluator.Evaluate(
        InformationSourceCatalog.Default,
        [SourceOutcome.Ok("finnhub-news"), SourceOutcome.Ok("google-news")]);

    [Fact]
    public void 欠測へ入った瞬間だけ発行する()
    {
        var tracker = NewTracker();

        var first = tracker.Observe(NewsOutage(), T0);
        var second = tracker.Observe(NewsOutage(), T0.AddMinutes(30));

        Transitions(first).Should().ContainSingle().Which.Should().BeOfType<InformationSourceDegraded>();
        Transitions(second).Should().BeEmpty("続いていることは日報が期間で示す（巡回ごとに発行しない）");
    }

    [Fact]
    public void 回復時に継続時間と該当サイクル数を載せる()
    {
        var tracker = NewTracker();
        tracker.Observe(NewsOutage(), T0);                    // 1 巡回目（欠測へ）
        tracker.Observe(NewsOutage(), T0.AddMinutes(30));     // 2 巡回目
        tracker.Observe(NewsOutage(), T0.AddMinutes(60));     // 3 巡回目

        var events = tracker.Observe(Healthy(), T0.AddMinutes(90));

        var recovered = Transitions(events).Should().ContainSingle()
            .Which.Should().BeOfType<InformationSourceRecovered>().Which;
        recovered.Category.Should().Be(InformationSourceCatalog.NewsCategory);
        recovered.DegradedAt.Should().Be(T0);
        recovered.AffectedCycles.Should().Be(3);
        recovered.OutageDuration.Should().Be(TimeSpan.FromMinutes(90));
    }

    [Fact]
    public void 正常が続く間は何も発行しない()
    {
        var tracker = NewTracker();

        Transitions(tracker.Observe(Healthy(), T0)).Should().BeEmpty();
        Transitions(tracker.Observe(Healthy(), T0.AddMinutes(30))).Should().BeEmpty();
    }

    // 🔴 発行に失敗した分は状態を戻す。戻さないと「発行済み」として記録され、次の機会にも二度と出なくなる。
    [Fact]
    public void 発行失敗を巻き戻すと次の巡回で再発行する()
    {
        var tracker = NewTracker();
        var published = Transitions(tracker.Observe(NewsOutage(), T0)).Single();

        tracker.Rollback(published);
        var retried = tracker.Observe(NewsOutage(), T0.AddMinutes(30));

        Transitions(retried).Should().ContainSingle().Which.Should().BeOfType<InformationSourceDegraded>();
    }

    // カテゴリごとに独立して劣化・回復する（ニュース系と他の必須ソースが 1 本に混ざらない）。
    [Fact]
    public void 欠測はカテゴリごとに独立して追跡する()
    {
        var tracker = NewTracker();
        var both = DegradationEvaluator.Evaluate(
            InformationSourceCatalog.Default,
            [SourceOutcome.Failed("finnhub-news"), SourceOutcome.Failed("google-news"), SourceOutcome.Failed("sec-edgar")]);

        var events = tracker.Observe(both, T0);

        events.OfType<InformationSourceDegraded>().Select(e => e.Category)
            .Should().BeEquivalentTo([InformationSourceCatalog.NewsCategory, "sec-edgar"]);
    }

    // ------------------------------------------------------------------
    // #564: 現況観測（毎巡回 1 件・遷移とは独立）
    // ------------------------------------------------------------------

    // 🔴 **本件の中核。** 遷移が出ない巡回（縮退が続いている静かな区間）でも現況が出るからこそ、
    // 受け手はいつ再起動しても 1 巡回で停止を復元できる。
    [Fact]
    public void 縮退が続く巡回でも現況観測は毎回出る()
    {
        var tracker = NewTracker();
        tracker.Observe(NewsOutage(), T0);

        var second = tracker.Observe(NewsOutage(), T0.AddMinutes(30));

        Transitions(second).Should().BeEmpty("遷移は出ない");
        var observed = Observation(second);
        observed.BlockingCategories.Should().BeEquivalentTo([InformationSourceCatalog.NewsCategory]);
        observed.BlocksNewEntries.Should().BeTrue();
        observed.ValidFor.Should().Be(Validity);
        observed.ObservedAt.Should().Be(T0.AddMinutes(30));
    }

    // 対の肯定形: 健全な巡回でも観測は出る（**空集合の宣言**）。出さないと受け手は
    // 「観測して健全だった」と「まだ何も聞いていない」を区別できず、既定が fail-open へ戻る。
    [Fact]
    public void 縮退が無い巡回でも空の現況観測が出る()
    {
        var tracker = NewTracker();

        var observed = Observation(tracker.Observe(Healthy(), T0));

        observed.BlockingCategories.Should().BeEmpty();
        observed.BlocksNewEntries.Should().BeFalse();
        observed.ClosesAllowed.Should().BeTrue();
    }

    // 否定形: 現況観測は**新規建てを止めない縮退を載せない**（受け手が Behavior を再解釈して停止範囲を広げない）。
    [Fact]
    public void 新規建てを止めない縮退は現況観測に載らない()
    {
        var tracker = NewTracker();
        // sec-edgar の欠測は RecordAndNotifyOnly（新規建ては止めない）。ニュース系は生きている。
        var degradation = DegradationEvaluator.Evaluate(
            InformationSourceCatalog.Default,
            [SourceOutcome.Ok("finnhub-news"), SourceOutcome.Ok("google-news"), SourceOutcome.Failed("sec-edgar")]);

        var events = tracker.Observe(degradation, T0);

        events.OfType<InformationSourceDegraded>().Should().ContainSingle()
            .Which.BlocksNewEntries.Should().BeFalse("記録・通知のみの縮退である");
        Observation(events).BlockingCategories.Should().BeEmpty("止めない縮退は現況観測に載せない");
    }

    // 否定形: 現況観測は遷移イベントを**置き換えない**（初回の巡回は遷移＋現況の 2 件）。
    [Fact]
    public void 現況観測は遷移イベントを置き換えない()
    {
        var tracker = NewTracker();

        var events = tracker.Observe(NewsOutage(), T0);

        events.Should().HaveCount(2);
        events.OfType<InformationSourceDegraded>().Should().ContainSingle();
        events.OfType<InformationSourceStateObserved>().Should().ContainSingle();
    }

    // 否定形: 現況観測は抑止状態を持たないため、巻き戻しても次の巡回の現況は変わらない
    // （＝発行に失敗しても次の巡回で必ず出る）。
    [Fact]
    public void 現況観測の巻き戻しは次の巡回に影響しない()
    {
        var tracker = NewTracker();
        var observed = Observation(tracker.Observe(NewsOutage(), T0));

        tracker.Rollback(observed);

        Observation(tracker.Observe(NewsOutage(), T0.AddMinutes(30)))
            .BlockingCategories.Should().BeEquivalentTo([InformationSourceCatalog.NewsCategory]);
    }
}

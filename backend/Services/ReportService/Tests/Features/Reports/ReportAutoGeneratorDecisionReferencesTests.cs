using AiStockTrading.Shared.Contracts.Trading;
using AwesomeAssertions;
using ReportService.Common.Abstractions;
using ReportService.Domain;
using ReportService.Features.Reports;
using ReportService.Infrastructure.ExternalServices;
using ReportService.Infrastructure.Persistence;
using Xunit;
using Rec = ReportService.Domain.DecisionReferenceFeedBuilder.DecisionRecord;

namespace ReportService.Tests;

// FR-06, FR-08, #1356, IADR-0541 決定 2: 自動生成の出口（永続化された日報の本文）に、判断が参照した出典が届く。
// 判断の時刻で窓に絞る（前の日報の窓の判断を数えない）。照会の失敗は未供給として記録する（「参照なし」と書かない）。
public class ReportAutoGeneratorDecisionReferencesTests
{
    // 2026-07-08（水）16:00 JST ＝ 07:00 UTC。日報だけが生成境界を越えている時刻。
    private static readonly DateTimeOffset WedAfterClose = new(2026, 7, 8, 7, 0, 0, TimeSpan.Zero);

    private sealed class FixedClock(DateTimeOffset now) : IClock
    {
        public DateTimeOffset UtcNow { get; } = now;
    }

    private sealed class StubDrafter : IReportNarrativeDrafter
    {
        public Task<string> DraftNarrativeAsync(ReportNarrativeContext context, CancellationToken cancellationToken = default) =>
            Task.FromResult("自動生成の散文");
    }

    private sealed class StubSource(IReadOnlyList<Rec>? decisions) : IDecisionReferenceSource
    {
        public Task<IReadOnlyList<Rec>?> GetDecisionsAsync(DateOnly from, DateOnly to, CancellationToken cancellationToken = default) =>
            Task.FromResult(decisions);
    }

    private static async Task<TradingReport> Generate(IDecisionReferenceSource? source)
    {
        var store = new InMemoryReportStore();
        await new ReportAutoGenerator(store, new ReportDraftService(new StubDrafter()), new NoOpPeriodFillSource(),
            new FixedClock(WedAfterClose), new ReportAutoGenerationSettings(), decisionReferenceSource: source).RunOnceAsync();
        return store.Get("daily-2026-07-08")!.Report;
    }

    private static Rec Decision(string title, DateTimeOffset at) =>
        new("7203", at, new DecisionReferences("retrieved", [new DecisionReference(title, "https://example.com/" + title, at)]));

    [Fact]
    public async Task 当日の窓の判断の出典が日報の本文に届き_前の窓の判断は数えない()
    {
        var report = await Generate(new StubSource(
        [
            Decision("today", new DateTimeOffset(2026, 7, 8, 1, 0, 0, TimeSpan.Zero)),      // 10:00 JST（当日の窓）
            Decision("yesterday", new DateTimeOffset(2026, 7, 7, 1, 0, 0, TimeSpan.Zero)),  // 前日 10:00 JST（前の日報の窓）
        ]));

        report.Body.Should().Contain("[today](https://example.com/today)");
        report.Body.Should().NotContain("yesterday");
        report.UnsuppliedInputs.Should().NotContain(ReportInput.DecisionReferences);
    }

    [Fact]
    public async Task 照会できなければ未供給として記録し参照なしと書かない()
    {
        var report = await Generate(new StubSource(null));

        report.UnsuppliedInputs.Should().Contain(ReportInput.DecisionReferences);
        report.Body.Should().Contain("**照会していません（または照会できませんでした）**");
    }

    // 本番の組み立て（Audit:BaseUrl 未設定）が結線する本物の未構成の供給元は、常に未供給（「参照なし」へ倒さない）。
    [Fact]
    public async Task 監査台帳が未構成の供給元は未供給として記録する()
    {
        (await new UnsuppliedDecisionReferenceSource().GetDecisionsAsync(new DateOnly(2026, 7, 8), new DateOnly(2026, 7, 8)))
            .Should().BeNull();

        var report = await Generate(new UnsuppliedDecisionReferenceSource());

        report.UnsuppliedInputs.Should().Contain(ReportInput.DecisionReferences);
    }

    [Fact]
    public async Task 供給元が未注入なら照会せず未供給にも数えない()
    {
        var report = await Generate(null);

        report.UnsuppliedInputs.Should().NotContain(ReportInput.DecisionReferences);
    }
}

using AuditService.Common.Abstractions;
using AuditService.Features.AuditEvents;
using AuditService.Infrastructure.Persistence;
using AuditService.Infrastructure.Steps;
using AiStockTrading.Shared.Contracts.Events;
using AiStockTrading.Shared.Contracts.Llm;
using AiStockTrading.TestSupport.Messaging;
using AwesomeAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Wolverine;
using Wolverine.Tracking;
using Xunit;

namespace AuditService.Tests;

// FR-04, FR-11, UC-01, UC-07, ADR-0017 決定2・決定4-(3), #335, #347, IADR-0216/0217/0219:
// LLM 統制の 3 イベントが**実際に台帳へ着地する**ことを、本番と同じ発見範囲のホストで確かめる。
//
// 🔴 既存の AuditConsumerCoverageTests は「そのイベントを扱えるハンドラが**発見される**」までしか見ていない。
// ADR-0017 決定4-(3) が要求する③月報集計は**台帳に行があること**が前提であり、
// 発見と着地の間（ハンドラ本体・写像・冪等キー）が抜けていても前者だけなら緑になる。
public class LlmGovernanceAuditHandlersTests
{
    private static readonly DateTimeOffset OccurredAt = new(2026, 8, 3, 1, 30, 0, TimeSpan.Zero);

    private static Task<IHost> BuildHostAsync(InMemoryAuditEventStore store) =>
        Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Services.AddSingleton<IClock, SystemClock>();
                opts.Services.AddSingleton<IAuditEventStore>(store);
                // 本番（Program.cs）と同じ発見範囲。
                opts.Discovery.IncludeAssembly(typeof(PriceMovementDetectedAuditHandler).Assembly);
                opts.StubAllExternalTransports();
            })
            .StartAsync();

    // ADR-0017 決定4-(3): 発火は台帳へ残す（月報の「当月の発火回数」の供給元）。
    [Fact]
    public async Task フォールバック発火は月で束ねた相関で台帳へ記録される()
    {
        var store = new InMemoryAuditEventStore();
        using var host = await BuildHostAsync(store);

        var evt = new LlmFallbackFired(
            LlmPurposes.ReportMonthly, LlmAssignments.Opus5, "claude-sonnet-5",
            nameof(LlmAssignmentOutcome.FallbackFired), OccurredAt);

        var session = await host.TrackActivityForTest().InvokeMessageAndWaitAsync(evt);
        session.Executed.MessagesOf<LlmFallbackFired>().Should().NotBeEmpty();

        var correlation = AuditEntryFactory.From(evt, Guid.NewGuid(), OccurredAt).CorrelationId;
        var entry = store.GetByCorrelation(correlation)
            .Should().ContainSingle(e => e.EventType == nameof(LlmFallbackFired)).Subject;
        entry.Summary.Should().Contain(LlmAssignments.Opus5);
        entry.Summary.Should().Contain("claude-sonnet-5");

        await host.StopAsync();
    }

    // 🔴 **同月の発火は 1 本の相関に積み上がる**（抑止しない）。抑止されると月報の回数が実態より小さくなり、
    // 「恒常的に発火している＝設定が誤っている」という決定4 の判断材料が失われる。
    [Fact]
    public async Task 同月のフォールバック発火は件数ぶん台帳へ残る()
    {
        var store = new InMemoryAuditEventStore();
        using var host = await BuildHostAsync(store);

        var first = new LlmFallbackFired(
            LlmPurposes.ReportDaily, LlmAssignments.Sonnet5, LlmAssignments.Haiku45,
            nameof(LlmAssignmentOutcome.FallbackFired), OccurredAt);
        var second = first with { OccurredAt = OccurredAt.AddDays(4) };

        await host.TrackActivityForTest().InvokeMessageAndWaitAsync(first);
        await host.TrackActivityForTest().InvokeMessageAndWaitAsync(second);

        var correlation = AuditEntryFactory.From(first, Guid.NewGuid(), OccurredAt).CorrelationId;
        store.GetByCorrelation(correlation).Should().HaveCount(2, "発火は 1 件ずつ数えられること");

        await host.StopAsync();
    }

    // ADR-0017 決定2: 見送りは障害ではなく正常な結果だが、**沈黙させない**。
    [Fact]
    public async Task 取引判断の見送りは台帳へ記録される()
    {
        var store = new InMemoryAuditEventStore();
        using var host = await BuildHostAsync(store);

        var evt = new TradeDecisionSkipped(
            LlmPurposes.TradeDecision, nameof(LlmAssignmentOutcome.Unassigned),
            LlmAssignments.Sonnet5, "claude-haiku-4-5", OccurredAt);

        var session = await host.TrackActivityForTest().InvokeMessageAndWaitAsync(evt);
        session.Executed.MessagesOf<TradeDecisionSkipped>().Should().NotBeEmpty();

        var correlation = AuditEntryFactory.From(evt, Guid.NewGuid(), OccurredAt).CorrelationId;
        store.GetByCorrelation(correlation)
            .Should().ContainSingle(e => e.EventType == nameof(TradeDecisionSkipped))
            .Which.Summary.Should().Contain("見送り");

        await host.StopAsync();
    }

    // NFR（費用）, #347: 上限の**対象外**（報告書生成）の費用も台帳へ残る。
    // 抑制しないことと記録しないことは別であり、月報の実績はこの行から作る。
    [Fact]
    public async Task 上限対象外の用途のLLM費用も台帳へ記録される()
    {
        var store = new InMemoryAuditEventStore();
        using var host = await BuildHostAsync(store);

        var evt = new LlmCostIncurred(9.005m, OccurredAt, LlmPurposes.ReportMonthly, "claude-opus-5");
        LlmCostScope.IsGoverned(evt.Purpose).Should().BeFalse("前提: 報告書生成は月次上限の対象外である");

        var session = await host.TrackActivityForTest().InvokeMessageAndWaitAsync(evt);
        session.Executed.MessagesOf<LlmCostIncurred>().Should().NotBeEmpty();

        var correlation = AuditEntryFactory.From(evt, Guid.NewGuid(), OccurredAt).CorrelationId;
        store.GetByCorrelation(correlation)
            .Should().ContainSingle(e => e.EventType == nameof(LlmCostIncurred))
            .Which.Summary.Should().Contain(LlmPurposes.ReportMonthly);

        await host.StopAsync();
    }
}

using ReportService.Features.Reports;
using ReportService.Common.Abstractions;
using ReportService.Infrastructure.ExternalServices;
using AiStockTrading.Shared.Contracts.Events;
using AiStockTrading.Shared.Contracts.Llm;
using AiStockTrading.Shared.Infrastructure.Composable.Llm;
using AiStockTrading.TestSupport.Messaging;
using AiStockTrading.TestSupport.PlatformShim.Foundation.Extensions;
using AwesomeAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Wolverine;
using Wolverine.Runtime;
using Wolverine.Tracking;
using Xunit;

namespace ReportService.Tests;

// FR-04, FR-06, FR-09, FR-16, NFR（費用）, ADR-0017 決定4-(2)/(3), #335, #347, IADR-0217/0219:
// 報告書サービスの**発行アダプタ**。可視化 3 経路のうち②通知・③台帳と、費用実績の供給は
// ここで publish されたイベントが唯一の出口である。
//
// 🔴 **アダプタが呼ばれること（HttpReportNarrativeDrafterVisibilityTests）と、呼ばれた結果が
// メッセージバスへ出ることは別の事実である。** 前者だけを検証していると、publish しない実装
//（既定の NoOp をそのまま配線した等）でも緑になり、警告通知も月報集計も沈黙する。
//
// ADR-0013, IADR-0129, #354: 本番と同じ Wolverine 配線でホストを起こし、送信先だけ stub へ倒す。
// これらのアダプタは singleton（HttpReportNarrativeDrafter が singleton）なので、scoped な IMessageBus では
// なく singleton の IWolverineRuntime を受け取る——その配線でも実際に送信が成立することを確かめる。
public class PublishingLlmReportersTests
{
    private const string ServiceName = "ai-stock-trading.report-service";
    private static readonly DateTimeOffset Now = new(2026, 8, 31, 2, 0, 0, TimeSpan.Zero);

    private sealed class FixedClock : IClock
    {
        public DateTimeOffset UtcNow => Now;
    }

    // IADR-0122 決定4 の投入値（換算率 163.71・2026-07 時点）。values-local.yaml と同じ表。
    private static LlmPriceTable Prices() => LlmPriceTable.From(
    [
        ("claude-opus-5", "0.819", "4.093"),
        ("claude-sonnet-5", "0.327", "1.637"),
        ("claude-haiku-4-5", "0.164", "0.819"),
    ]);

    private static Task<IHost> BuildHostAsync() =>
        Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.UseAiStockTradingRabbitMq(ServiceName, "amqp://guest:guest@localhost:5672");
                opts.StubAllExternalTransports();
            })
            .StartAsync();

    // ---- ③費用: 用途つきで LlmCostIncurred を publish する（#347・#282 の是正） ----------------

    // 🔴 **用途を必ず載せる。** 購読側（費用統制サービス）は purpose で上限の対象／対象外を判別するため、
    // 用途の無い計上は「対象内」へ倒れるか捨てられるかのどちらかになり、どちらも計画 §6.1 に反する。
    [Fact]
    public async Task 報告書生成の費用は用途と実効モデルを載せて発行する()
    {
        using var host = await BuildHostAsync();
        var reporter = new PublishingLlmUsageReporter(
            host.Services.GetRequiredService<IWolverineRuntime>(),
            new FixedClock(), Prices(), NullLogger<PublishingLlmUsageReporter>.Instance);

        var session = await host.TrackActivityForTest().ExecuteAndWaitAsync(
            _ => reporter.ReportAsync(new LlmUsage(LlmPurposes.ReportMonthly, 1000, 2000, "claude-opus-5")));

        var published = session.Sent.MessagesOf<LlmCostIncurred>().Should().ContainSingle().Subject;
        published.Purpose.Should().Be(LlmPurposes.ReportMonthly);
        published.Model.Should().Be("claude-opus-5");
        published.At.Should().Be(Now);
        // IADR-0122 決定1: 単価は応答が名乗った実効モデルから引く。1000×0.819 + 2000×4.093（円/1k）。
        published.Amount.Should().Be(9.005m);
        // 🔴 報告書生成は月次上限の**対象外**である（§6.1）。対象内の用途で発行すると日報確定が止まる連鎖が生じる。
        LlmCostScope.IsGoverned(published.Purpose).Should().BeFalse();

        await host.StopAsync();
    }

    // IADR-0122 決定1: 要求側の希望ではなく応答が名乗った実効モデルの単価で計上する。
    // フォールバックで安いモデルへ落ちたのに第 1 候補の単価で積むと、実績が過大になる。
    [Fact]
    public async Task フォールバック先のモデルで応答したらその単価で計上する()
    {
        using var host = await BuildHostAsync();
        var reporter = new PublishingLlmUsageReporter(
            host.Services.GetRequiredService<IWolverineRuntime>(),
            new FixedClock(), Prices(), NullLogger<PublishingLlmUsageReporter>.Instance);

        var session = await host.TrackActivityForTest().ExecuteAndWaitAsync(
            _ => reporter.ReportAsync(new LlmUsage(LlmPurposes.ReportMonthly, 1000, 2000, "claude-sonnet-5")));

        var published = session.Sent.MessagesOf<LlmCostIncurred>().Single();
        published.Amount.Should().Be(3.601m);
        published.Model.Should().Be("claude-sonnet-5");

        await host.StopAsync();
    }

    // ---- ②通知・③台帳: フォールバック発火を publish する（#335） ------------------------------

    // ADR-0017 決定4-(2)/(3): 発火は通知（②）と監査台帳（③月報集計の供給元）へ流れる。
    // どちらも本イベントの購読者であり、publish されなければ両方とも沈黙する。
    [Fact]
    public async Task フォールバック発火は用途と期待_実効モデルを載せて発行する()
    {
        using var host = await BuildHostAsync();
        var reporter = new PublishingLlmGovernanceReporter(
            host.Services.GetRequiredService<IWolverineRuntime>(),
            new FixedClock(), NullLogger<PublishingLlmGovernanceReporter>.Instance);

        var evaluation = LlmAssignmentEvaluator.Evaluate(LlmPurposes.ReportMonthly, "claude-sonnet-5");
        var session = await host.TrackActivityForTest().ExecuteAndWaitAsync(
            _ => reporter.FallbackFiredAsync(evaluation, LlmPurposes.ReportMonthly));

        var published = session.Sent.MessagesOf<LlmFallbackFired>().Should().ContainSingle().Subject;
        published.Purpose.Should().Be(LlmPurposes.ReportMonthly);
        published.ExpectedModel.Should().Be(LlmAssignments.Opus5);
        published.EffectiveModel.Should().Be("claude-sonnet-5");
        published.Outcome.Should().Be(nameof(LlmAssignmentOutcome.FallbackFired));
        published.OccurredAt.Should().Be(Now);

        await host.StopAsync();
    }

    // 割当表に無いモデルへ落ちた場合（基盤の DefaultModel への無音の落下・platform IADR-0102）も
    // **原因を区別して**発行する。「未知だった」と「第 2 候補だった」は運用上の意味が違う。
    [Fact]
    public async Task 割当外のモデルへ落ちた場合は原因を_Unassigned_として発行する()
    {
        using var host = await BuildHostAsync();
        var reporter = new PublishingLlmGovernanceReporter(
            host.Services.GetRequiredService<IWolverineRuntime>(),
            new FixedClock(), NullLogger<PublishingLlmGovernanceReporter>.Instance);

        var evaluation = LlmAssignmentEvaluator.Evaluate(LlmPurposes.ReportDaily, "claude-opus-4-8");
        var session = await host.TrackActivityForTest().ExecuteAndWaitAsync(
            _ => reporter.FallbackFiredAsync(evaluation, LlmPurposes.ReportDaily));

        var published = session.Sent.MessagesOf<LlmFallbackFired>().Single();
        published.Outcome.Should().Be(nameof(LlmAssignmentOutcome.Unassigned));
        published.EffectiveModel.Should().Be("claude-opus-4-8");

        await host.StopAsync();
    }
}

using System.Globalization;
using CostControlService.Common.Abstractions;
using CostControlService.Features.CostControl;
using CostControlService.Domain;
using CostControlService.Infrastructure.ExternalServices;
using CostControlService.Infrastructure.Persistence;
using CostControlService.Infrastructure.Steps;
using AiStockTrading.Shared.Contracts.Events;
using AiStockTrading.Shared.Contracts.Llm;
using AiStockTrading.TestSupport.Messaging;
using AiStockTrading.TestSupport.PlatformShim.Foundation.Extensions;
using AwesomeAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Wolverine;
using Wolverine.Runtime;
using Wolverine.Tracking;
using Xunit;
using AppSvc = CostControlService.Features.CostControl.CostControlAppService;

namespace CostControlService.Tests;

// NFR（費用）, 05_trading-assumptions §6.1, #347, IADR-0218:
// **月次 LLM 費用上限の対象範囲の判別**（否定形が主眼）。
//
// 🔴 計画 §6.1 の実装上の注意（本テストが再発を防ぐ事故）:
//   「報告書生成の費用を同じカウンタに積むと、100% 到達で報告書生成が止まる。日報が確定しないと
//    翌日の取引が止まる（UC-01 の事前条件）ため、**費用統制が取引を止める連鎖**が生じる。
//    カウンタは対象範囲どおり分離する。」
public class LlmCostScopeConsumerTests
{
    private const string ServiceName = "ai-stock-trading.cost-control-service";

    private static string CurrentMonth() =>
        DateTimeOffset.UtcNow.ToString("yyyy-MM", CultureInfo.InvariantCulture);

    private static Task<IHost> BuildHostAsync(ICostLedger ledger) =>
        Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Services.AddSingleton<IClock, SystemClock>();
                opts.Services.AddSingleton<ICostLimitsProvider, DefaultCostLimitsProvider>();
                opts.Services.AddSingleton(ledger);
                opts.Services.AddSingleton<IProcessedMessageStore>(new InMemoryProcessedMessageStore());
                opts.Services.AddScoped<AppSvc>();
                opts.UseAiStockTradingRabbitMq(
                    ServiceName, "amqp://guest:guest@localhost:5672", typeof(LlmCostIncurredHandler).Assembly);
                opts.StubAllExternalTransports();
            })
            .StartAsync();

    private static Task<ITrackedSession> DeliverAsync(IHost host, object message) =>
        host.TrackActivityForTest().ExecuteAndWaitAsync(_ =>
        {
            var runtime = host.Services.GetRequiredService<IWolverineRuntime>();
            var envelope = new Envelope(message)
            {
                Id = Guid.NewGuid(),
                Destination = new Uri($"local://{message.GetType().Name.ToLowerInvariant()}"),
            };
            return runtime.EnqueueDirectlyAsync([envelope]).AsTask();
        });

    // 🔴 **否定形（#347 の受け入れ基準）**: 報告書生成・情報収集の費用は上限カウンタへ積まれない。
    [Theory]
    [InlineData(LlmPurposes.ReportMonthly)]
    [InlineData(LlmPurposes.ReportWeekly)]
    [InlineData(LlmPurposes.ReportDaily)]
    [InlineData("information-collection")]
    public async Task 報告書生成と情報収集の費用は上限カウンタへ積まれない(string purpose)
    {
        var ledger = new InMemoryCostLedger();
        using var host = await BuildHostAsync(ledger);

        await DeliverAsync(host, new LlmCostIncurred(9_999m, DateTimeOffset.UtcNow, purpose, "claude-opus-5"));

        var month = CurrentMonth();
        // 上限の対象（Llm）は 1 円も動かない。
        ledger.GetMonthlyTotal(month, CostCategory.Llm).Should().Be(0m);
        // 一方で**記録はされる**（対象外＝計上しない、ではない。§6.1「月報に実績を記載する」）。
        ledger.GetMonthlyTotal(month, CostCategory.LlmUncapped).Should().Be(9_999m);

        await host.StopAsync();
    }

    [Theory]
    [InlineData(LlmPurposes.TradeDecision)]
    [InlineData(LlmPurposes.TradeDecisionScreening)]
    public async Task 取引判断サイクルの費用は上限カウンタへ積まれる(string purpose)
    {
        var ledger = new InMemoryCostLedger();
        using var host = await BuildHostAsync(ledger);

        await DeliverAsync(host, new LlmCostIncurred(250m, DateTimeOffset.UtcNow, purpose, "claude-sonnet-5"));

        ledger.GetMonthlyTotal(CurrentMonth(), CostCategory.Llm).Should().Be(250m);
        ledger.GetMonthlyTotal(CurrentMonth(), CostCategory.LlmUncapped).Should().Be(0m);

        await host.StopAsync();
    }

    // 用途を持たない従来の形（取引判断サービスの旧発行）は上限側へ倒す＝過小計上を作らない。
    [Fact]
    public async Task 用途を持たない従来の形は上限側へ計上する()
    {
        var ledger = new InMemoryCostLedger();
        using var host = await BuildHostAsync(ledger);

        await DeliverAsync(host, new LlmCostIncurred(120m, DateTimeOffset.UtcNow));

        ledger.GetMonthlyTotal(CurrentMonth(), CostCategory.Llm).Should().Be(120m);

        await host.StopAsync();
    }

    // 🔴 **否定形の核心**: 既定の上限は 15,000 円で 80%＝12,000 円が Throttled の境界である。
    // 報告書費用だけで境界を大きく超える額を積んでも、**しきい値到達（CostThresholdReached）は発行されない**。
    // ここが「費用統制が報告書生成・取引を止める連鎖」を断つ点である。
    [Fact]
    public async Task 対象外の費用だけでは上限しきい値に到達せず通知も出ない()
    {
        var ledger = new InMemoryCostLedger();
        using var host = await BuildHostAsync(ledger);

        var session = await DeliverAsync(
            host, new LlmCostIncurred(30_000m, DateTimeOffset.UtcNow, LlmPurposes.ReportMonthly, "claude-opus-5"));

        session.Sent.MessagesOf<CostThresholdReached>().Should().BeEmpty();
        ledger.GetMonthlyTotal(CurrentMonth(), CostCategory.Llm).Should().Be(0m);

        await host.StopAsync();
    }

    // 対照群: 取引判断の費用が境界（12,000 円）を越えれば従来どおり Throttled が発行される
    // （上の否定形が「そもそも通知が出ない実装」でも緑になるのを防ぐ）。
    [Fact]
    public async Task 取引判断の費用が_80パーセント_を越えれば通知が出る()
    {
        var ledger = new InMemoryCostLedger();
        using var host = await BuildHostAsync(ledger);

        var session = await DeliverAsync(
            host, new LlmCostIncurred(12_000m, DateTimeOffset.UtcNow, LlmPurposes.TradeDecision, "claude-sonnet-5"));

        var reached = session.Sent.MessagesOf<CostThresholdReached>().Should().ContainSingle().Subject;
        reached.State.Should().Be("Throttled");
        reached.Category.Should().Be(nameof(CostCategory.Llm));

        await host.StopAsync();
    }
}

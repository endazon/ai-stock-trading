using System.Globalization;
using AiStockTrading.CostControl.Application.Adapters;
using AiStockTrading.CostControl.Application.Ports;
using AiStockTrading.CostControl.Domain;
using AiStockTrading.CostControl.Infrastructure.Composable.Steps;
using AiStockTrading.Shared.Contracts.Events;
using AwesomeAssertions;
using MassTransit;
using MassTransit.Testing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using AppSvc = AiStockTrading.CostControl.Application.Services.CostControlService;

namespace AiStockTrading.CostControl.Infrastructure.Tests;

// NFR（費用）, FR-04, IADR-0055 決定1/5: LlmCostIncurred を購読して LLM 費用を計上する Consumer を
// MassTransit テストハーネス＋インメモリ台帳/重複排除ストアで検証する（HTTP /costs/record は OwnerOnly のため使わない）。
// 既定の LLM 月次上限は 15,000 円（80%=12,000 で Throttled へ上方遷移）。
public class LlmCostIncurredConsumerTests
{
    private static string CurrentMonth() =>
        DateTimeOffset.UtcNow.ToString("yyyy-MM", CultureInfo.InvariantCulture);

    private static ServiceProvider BuildProvider(ICostLedger ledger, IProcessedMessageStore processed) =>
        new ServiceCollection()
            .AddLogging()
            .AddSingleton<IClock, SystemClock>()
            .AddSingleton<ICostLimitsProvider, DefaultCostLimitsProvider>()
            .AddSingleton(ledger)
            .AddSingleton(processed)
            .AddScoped<AppSvc>()
            .AddMassTransitTestHarness(x => x.AddConsumer<LlmCostIncurredConsumer>())
            .BuildServiceProvider(true);

    [Fact]
    public async Task LlmCostIncurred_を_Llm_カテゴリで月次台帳へ計上する()
    {
        var ledger = new InMemoryCostLedger();
        await using var provider = BuildProvider(ledger, new InMemoryProcessedMessageStore());
        var harness = provider.GetRequiredService<ITestHarness>();
        await harness.Start();

        await harness.Bus.Publish(new LlmCostIncurred(250m, DateTimeOffset.UtcNow));
        (await harness.Consumed.Any<LlmCostIncurred>()).Should().BeTrue();

        ledger.GetMonthlyTotal(CurrentMonth(), CostCategory.Llm).Should().Be(250m);

        await harness.Stop();
    }

    // IADR-0055 決定5: at-least-once の再配信で二重計上しない（費用は月次累計のため統制判定を誤らせる）。
    [Fact]
    public async Task 同一_MessageId_の再配信では二重計上しない()
    {
        var ledger = new InMemoryCostLedger();
        await using var provider = BuildProvider(ledger, new InMemoryProcessedMessageStore());
        var harness = provider.GetRequiredService<ITestHarness>();
        await harness.Start();

        var messageId = NewId.NextGuid();
        var message = new LlmCostIncurred(100m, DateTimeOffset.UtcNow);

        await harness.Bus.Publish(message, ctx => ctx.MessageId = messageId);
        await harness.Bus.Publish(message, ctx => ctx.MessageId = messageId);

        // 両方の消費が終わるまで待つ（バスがアイドルになるまで）。
        await harness.InactivityTask;

        // 2 回消費しても計上は 1 回だけ。
        ledger.GetMonthlyTotal(CurrentMonth(), CostCategory.Llm).Should().Be(100m);

        await harness.Stop();
    }

    // 別 MessageId（別の LLM 呼び出し）は当然それぞれ計上される。
    [Fact]
    public async Task 別_MessageId_はそれぞれ計上される()
    {
        var ledger = new InMemoryCostLedger();
        await using var provider = BuildProvider(ledger, new InMemoryProcessedMessageStore());
        var harness = provider.GetRequiredService<ITestHarness>();
        await harness.Start();

        await harness.Bus.Publish(new LlmCostIncurred(100m, DateTimeOffset.UtcNow), ctx => ctx.MessageId = NewId.NextGuid());
        await harness.Bus.Publish(new LlmCostIncurred(50m, DateTimeOffset.UtcNow), ctx => ctx.MessageId = NewId.NextGuid());

        await harness.InactivityTask;

        ledger.GetMonthlyTotal(CurrentMonth(), CostCategory.Llm).Should().Be(150m);

        await harness.Stop();
    }

    // IADR-0027: しきい値の上方遷移時は /costs/record と同様に CostThresholdReached を発行する。
    [Fact]
    public async Task しきい値の上方遷移で_CostThresholdReached_を発行する()
    {
        var ledger = new InMemoryCostLedger();
        await using var provider = BuildProvider(ledger, new InMemoryProcessedMessageStore());
        var harness = provider.GetRequiredService<ITestHarness>();
        await harness.Start();

        // LLM 上限 15,000 の 80%=12,000 に到達 → Throttled へ上方遷移。
        await harness.Bus.Publish(new LlmCostIncurred(12_000m, DateTimeOffset.UtcNow));
        (await harness.Consumed.Any<LlmCostIncurred>()).Should().BeTrue();

        (await harness.Published.Any<CostThresholdReached>()).Should().BeTrue();
        var published = await harness.Published.SelectAsync<CostThresholdReached>().First();
        published.Context.Message.Category.Should().Be(nameof(CostCategory.Llm));
        published.Context.Message.State.Should().Be(nameof(CostControlState.Throttled));
        published.Context.Message.Percent.Should().Be(80m);

        await harness.Stop();
    }

    // IADR-0055 決定5: 計上に失敗したらマークを戻し、再配信で再試行できるようにする（計上欠落を避ける）。
    [Fact]
    public async Task 計上に失敗したらマークを戻す()
    {
        var processed = new InMemoryProcessedMessageStore();
        await using var provider = BuildProvider(new ThrowingCostLedger(), processed);
        var harness = provider.GetRequiredService<ITestHarness>();
        await harness.Start();

        var messageId = NewId.NextGuid();
        await harness.Bus.Publish(new LlmCostIncurred(10m, DateTimeOffset.UtcNow), ctx => ctx.MessageId = messageId);
        (await harness.Consumed.Any<LlmCostIncurred>()).Should().BeTrue();

        // マークが戻っている＝再配信で再度処理できる（TryMark が true を返す）。
        processed.TryMarkProcessed(messageId, DateTimeOffset.UtcNow).Should().BeTrue();

        await harness.Stop();
    }

    // 計上時に必ず失敗する台帳（Unmark の検証用）。
    private sealed class ThrowingCostLedger : ICostLedger
    {
        public LlmCostRecordOutcome Record(string month, CostCategory category, decimal amount, DateTimeOffset at) =>
            throw new InvalidOperationException("台帳の計上に失敗");

        public decimal GetMonthlyTotal(string month, CostCategory category) => 0m;

        public decimal GetMonthlyTotalAll(string month) => 0m;
    }
}

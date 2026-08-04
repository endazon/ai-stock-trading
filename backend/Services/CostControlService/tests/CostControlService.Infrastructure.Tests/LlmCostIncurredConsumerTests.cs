using System.Globalization;
using AiStockTrading.CostControl.Application.Adapters;
using AiStockTrading.CostControl.Application.Ports;
using AiStockTrading.CostControl.Domain;
using AiStockTrading.CostControl.Infrastructure.Composable.Steps;
using AiStockTrading.Shared.Contracts.Events;
using AiStockTrading.TestSupport.PlatformShim.Foundation.Extensions;
using AwesomeAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Wolverine;
using Wolverine.Runtime;
using Wolverine.Tracking;
using Xunit;
using AppSvc = AiStockTrading.CostControl.Application.Services.CostControlService;

namespace AiStockTrading.CostControl.Infrastructure.Tests;

// NFR（費用）, FR-04, IADR-0055 決定1/5: LlmCostIncurred を購読して LLM 費用を計上する Handler を
// Wolverine のテストハーネス（Wolverine.Tracking）＋インメモリ台帳/重複排除ストアで検証する
// （HTTP /costs/record は OwnerOnly のため使わない）。既定の LLM 月次上限は 15,000 円（80%=12,000 で Throttled へ上方遷移）。
//
// ADR-0013, IADR-0129, #354: MassTransit の ITestHarness からの移行。表明の意味は保つ。
// - `harness.Bus.Publish(msg)` ＋ `harness.Consumed.Any<T>()` → 明示 Envelope を実行経路へ流し `session.Executed` で確認
// - `ctx.MessageId = id` → `Envelope.Id = id`（Wolverine の冪等性キー。`EnqueueDirectlyAsync` で ID を指定して流す）
// - `harness.Published.Any<T>()` → `session.Sent`（宛先 URI も確認できる）
// - `harness.InactivityTask` → `TrackActivity()` の収束待ち
// 実ブローカへは接続しない（`StubAllExternalTransports`）。
public class LlmCostIncurredConsumerTests
{
    private const string ServiceName = "ai-stock-trading.cost-control-service";

    private static string CurrentMonth() =>
        DateTimeOffset.UtcNow.ToString("yyyy-MM", CultureInfo.InvariantCulture);

    // withProductionMessaging=true: 本番と同じ配線（キュー名・fan-out・再試行・DLQ）を用い、送信先だけ stub へ倒す。
    // false: 再試行方針を持たない素の構成（旧 MassTransit テストが素の AddMassTransitTestHarness を使い、
    //        再試行なしで 1 回だけ消費させていたのと同じ条件。失敗経路の検証に用いる）。
    private static Task<IHost> BuildHostAsync(
        ICostLedger ledger, IProcessedMessageStore processed, bool withProductionMessaging = true) =>
        Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Services.AddSingleton<IClock, SystemClock>();
                opts.Services.AddSingleton<ICostLimitsProvider, DefaultCostLimitsProvider>();
                opts.Services.AddSingleton(ledger);
                opts.Services.AddSingleton(processed);
                opts.Services.AddScoped<AppSvc>();

                if (withProductionMessaging)
                {
                    opts.UseAiStockTradingRabbitMq(
                        ServiceName,
                        "amqp://guest:guest@localhost:5672",
                        typeof(LlmCostIncurredHandler).Assembly);
                }
                else
                {
                    opts.ServiceName = ServiceName;
                    opts.Discovery.IncludeAssembly(typeof(LlmCostIncurredHandler).Assembly);
                }

                opts.StubAllExternalTransports();
            })
            .StartAsync();

    // 指定した MessageId（Envelope.Id）でメッセージを実行経路へ流す。ブローカからの受信と同じく
    // 「ID を持つ 1 通の封筒」が届く形を再現する（MassTransit の ctx.MessageId 指定に対応する）。
    // Wolverine の PublishAsync/InvokeAsync は封筒 ID を自動採番するため ID を指定できず、再配信
    // （＝同一 ID の再到達）を表現できない。実行経路そのものは同じ（HandlerPipeline）である。
    private static Task<ITrackedSession> DeliverAsync(
        IHost host, object message, Guid messageId, bool expectHandlerFailure = false)
    {
        var tracking = host.TrackActivity();
        // 失敗経路の検証では、ハンドラが投げた例外で追跡自体を失敗させない（例外の発生こそが前提条件）。
        if (expectHandlerFailure) tracking = tracking.DoNotAssertOnExceptionsDetected();

        return tracking.ExecuteAndWaitAsync(_ =>
        {
            var runtime = host.Services.GetRequiredService<IWolverineRuntime>();
            var envelope = new Envelope(message)
            {
                Id = messageId,
                // 受信済み封筒として扱わせるため配送先を持たせる（未設定だとパイプラインが解決に失敗する）。
                Destination = new Uri($"local://{message.GetType().Name.ToLowerInvariant()}"),
            };
            return runtime.EnqueueDirectlyAsync([envelope]).AsTask();
        });
    }

    [Fact]
    public async Task LlmCostIncurred_を_Llm_カテゴリで月次台帳へ計上する()
    {
        var ledger = new InMemoryCostLedger();
        using var host = await BuildHostAsync(ledger, new InMemoryProcessedMessageStore());

        var session = await DeliverAsync(host, new LlmCostIncurred(250m, DateTimeOffset.UtcNow), Guid.NewGuid());
        session.Executed.MessagesOf<LlmCostIncurred>().Should().NotBeEmpty();

        ledger.GetMonthlyTotal(CurrentMonth(), CostCategory.Llm).Should().Be(250m);

        await host.StopAsync();
    }

    // IADR-0055 決定5: at-least-once の再配信で二重計上しない（費用は月次累計のため統制判定を誤らせる）。
    [Fact]
    public async Task 同一_MessageId_の再配信では二重計上しない()
    {
        var ledger = new InMemoryCostLedger();
        using var host = await BuildHostAsync(ledger, new InMemoryProcessedMessageStore());

        var messageId = Guid.NewGuid();
        var message = new LlmCostIncurred(100m, DateTimeOffset.UtcNow);

        await DeliverAsync(host, message, messageId);
        await DeliverAsync(host, message, messageId);

        // 2 回消費しても計上は 1 回だけ。
        ledger.GetMonthlyTotal(CurrentMonth(), CostCategory.Llm).Should().Be(100m);

        await host.StopAsync();
    }

    // 別 MessageId（別の LLM 呼び出し）は当然それぞれ計上される。
    [Fact]
    public async Task 別_MessageId_はそれぞれ計上される()
    {
        var ledger = new InMemoryCostLedger();
        using var host = await BuildHostAsync(ledger, new InMemoryProcessedMessageStore());

        await DeliverAsync(host, new LlmCostIncurred(100m, DateTimeOffset.UtcNow), Guid.NewGuid());
        await DeliverAsync(host, new LlmCostIncurred(50m, DateTimeOffset.UtcNow), Guid.NewGuid());

        ledger.GetMonthlyTotal(CurrentMonth(), CostCategory.Llm).Should().Be(150m);

        await host.StopAsync();
    }

    // IADR-0027: しきい値の上方遷移時は /costs/record と同様に CostThresholdReached を発行する。
    // IADR-0129 決定 2: 宛先はメッセージ型ごとの共有 fanout exchange（購読側サービスがここに bind する）。
    [Fact]
    public async Task しきい値の上方遷移で_CostThresholdReached_を発行する()
    {
        var ledger = new InMemoryCostLedger();
        using var host = await BuildHostAsync(ledger, new InMemoryProcessedMessageStore());

        // LLM 上限 15,000 の 80%=12,000 に到達 → Throttled へ上方遷移。
        var session = await DeliverAsync(host, new LlmCostIncurred(12_000m, DateTimeOffset.UtcNow), Guid.NewGuid());
        session.Executed.MessagesOf<LlmCostIncurred>().Should().NotBeEmpty();

        var published = session.Sent.MessagesOf<CostThresholdReached>().Should().ContainSingle().Subject;
        published.Category.Should().Be(nameof(CostCategory.Llm));
        published.State.Should().Be(nameof(CostControlState.Throttled));
        published.Percent.Should().Be(80m);

        session.Sent.Envelopes().Should().Contain(e =>
            e.Message is CostThresholdReached
            && e.Destination!.ToString()
                == "rabbitmq://exchange/AiStockTrading.Shared.Contracts.Events.CostThresholdReached");

        await host.StopAsync();
    }

    // IADR-0055 決定5: 計上に失敗したらマークを戻し、再配信で再試行できるようにする（計上欠落を避ける）。
    [Fact]
    public async Task 計上に失敗したらマークを戻す()
    {
        var processed = new InMemoryProcessedMessageStore();
        using var host = await BuildHostAsync(new ThrowingCostLedger(), processed, withProductionMessaging: false);

        var messageId = Guid.NewGuid();
        var session = await DeliverAsync(
            host, new LlmCostIncurred(10m, DateTimeOffset.UtcNow), messageId, expectHandlerFailure: true);
        session.ExecutionStarted.MessagesOf<LlmCostIncurred>().Should().NotBeEmpty();

        // マークが戻っている＝再配信で再度処理できる（TryMark が true を返す）。
        processed.TryMarkProcessed(messageId, DateTimeOffset.UtcNow).Should().BeTrue();

        await host.StopAsync();
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

using AiStockTrading.Shared.Contracts.Events;
using AiStockTrading.Shared.Contracts.Llm;
using AiStockTrading.Shared.Infrastructure.Composable.Llm;
using AiStockTrading.TestSupport.Messaging;
using TradeDecisionService.Application.Ports;
using TradeDecisionService.Infrastructure.Adapters;
using AiStockTrading.TestSupport.PlatformShim.Foundation.Extensions;
using AwesomeAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Wolverine;
using Wolverine.Tracking;
using Xunit;

namespace TradeDecisionService.Infrastructure.Tests;

// NFR（費用）, FR-04, IADR-0055 決定2/3, IADR-0122: 応答が名乗った実効モデルの単価で計上額を決める。
// 用途別モデル割当（ADR-0014 / MSP/IADR-0112）で trade-decision=claude-sonnet-5 になったため、
// opus 単価（¥0.819/¥4.093）のままでは約 2.5 倍の過大計上になる（#303）。
//
// ADR-0013, IADR-0129, #354: MassTransit のテストハーネス（harness.Bus + harness.Published）から
// Wolverine.Tracking（IMessageBus + session.Sent）へ移行した。表明の意味は同じ
//（発行された LlmCostIncurred の金額・時刻を見る）。
public class PublishingLlmUsageReporterTests
{
    private const string ServiceName = "ai-stock-trading.trade-decision-service";
    private static readonly DateTimeOffset Now = new(2026, 7, 31, 1, 0, 0, TimeSpan.Zero);

    private sealed class FixedClock : IClock
    {
        public DateTimeOffset UtcNow => Now;
    }

    // IADR-0122 決定4 の投入値（換算率 163.71・2026-07 時点）。values-local.yaml と同じ表。
    private static LlmPriceTable Prices() => LlmPriceTable.From(
    [
        ("claude-fable-5", "1.637", "8.186"),
        ("claude-opus-5", "0.819", "4.093"),
        ("claude-opus-4-8", "0.819", "4.093"),
        ("claude-sonnet-5", "0.327", "1.637"),
        ("claude-haiku-4-5", "0.164", "0.819"),
    ]);

    private static async Task<decimal> ReportAsync(LlmUsage usage, LlmPriceTable prices) =>
        (await PublishAsync(usage, prices)).Amount;

    // #335, IADR-0212: 発行された計上イベントそのものを返す（用途の写しを見るため）。
    private static async Task<LlmCostIncurred> PublishAsync(LlmUsage usage, LlmPriceTable prices)
    {
        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                // 本番と同じ配線（キュー名・fan-out・再試行・DLQ）を用い、送信先だけ stub へ倒す。
                // ルーティングを入れないと発行先が 1 つも無く、送信そのものが起きない。
                opts.UseAiStockTradingRabbitMq(ServiceName, "amqp://guest:guest@localhost:5672");
                opts.StubAllExternalTransports();
            })
            .StartAsync();

        // 本アダプタは scoped。Wolverine の IMessageBus も scoped であり、スコープから解決する。
        using var scope = host.Services.CreateScope();
        // #347, IADR-0218 / #335, IADR-0212: 用途は**計測ごと**（LlmUsage.Purpose）に載る。
        // アダプタは purpose を持たない —— 持たせると二段判断の層が混ざる。
        var reporter = new PublishingLlmUsageReporter(
            scope.ServiceProvider.GetRequiredService<IMessageBus>(),
            new FixedClock(), prices, NullLogger<PublishingLlmUsageReporter>.Instance);

        var session = await host.TrackActivityForTest().ExecuteAndWaitAsync(_ => reporter.ReportAsync(usage));

        var published = session.Sent.MessagesOf<LlmCostIncurred>().Single();
        published.At.Should().Be(Now);
        await host.StopAsync();
        return published;
    }

    // 基準2（#303）: trade-decision は claude-sonnet-5。入力 1000 × 0.327 + 出力 2000 × 1.637 = 3.601 円。
    // 従来の opus 単価なら 0.819 + 8.186 = 9.005 円で、約 2.5 倍の過大計上だった。
    [Fact]
    public async Task trade_decision_は_sonnet_5_の単価で計上する()
    {
        var amount = await ReportAsync(new LlmUsage(LlmPurposes.TradeDecision, 1000, 2000, "claude-sonnet-5"), Prices());

        amount.Should().Be(3.601m);
    }

    // 基準3（#303）: 報告書 3 種別が別モデルへ解決されても、それぞれの単価で計上できる（#282 で経路を足す）。
    [Theory]
    [InlineData("claude-fable-5", 18.009)]   // report-monthly: 1.637 + 2×8.186
    [InlineData("claude-opus-5", 9.005)]     // report-weekly:  0.819 + 2×4.093
    [InlineData("claude-sonnet-5", 3.601)]   // report-daily:   0.327 + 2×1.637
    public async Task 報告書の種別ごとのモデルでも実効単価で計上する(string model, double expected)
    {
        var amount = await ReportAsync(new LlmUsage(LlmPurposes.TradeDecision, 1000, 2000, model), Prices());

        amount.Should().Be((decimal)expected);
    }

    // 基準4（#303）: 未知モデル・モデル名なしは最大単価（fable-5）へ倒す。0 に倒すと月次上限が効かなくなる。
    [Theory]
    [InlineData("claude-sonnet-4-6")]
    [InlineData(null)]
    public async Task 未知のモデルは最大単価で計上する(string? model)
    {
        var amount = await ReportAsync(new LlmUsage(LlmPurposes.TradeDecision, 1000, 2000, model), Prices());

        amount.Should().Be(18.009m);
    }

    // 🔴 NFR（費用）, #335, #347, IADR-0212/0218: **計上イベントの用途は、計測ごとの用途がそのまま載る。**
    // 二段判断は一次（trade-decision-screening）と二次（trade-decision）で用途が違い、月次上限の対象範囲は
    // 購読側が purpose だけを見て決める。アダプタ構築時に固定していた頃は**両層が同じ用途で積まれ**、
    // 層別の内訳が取れなかった（金額は合っているので、症状が内訳の欠落だけで気づけない）。
    [Theory]
    [InlineData(LlmPurposes.TradeDecision)]
    [InlineData(LlmPurposes.TradeDecisionScreening)]
    public async Task 計上イベントには計測ごとの用途がそのまま載る(string purpose)
    {
        var published = await PublishAsync(new LlmUsage(purpose, 1000, 2000, "claude-sonnet-5"), Prices());

        published.Purpose.Should().Be(purpose);
    }

    // IADR-0055: 単価未設定でも publish する（金額 0 は統制判定に無害で、計上経路の健全性を保てる）。
    [Fact]
    public async Task 単価未設定でも金額_0_で発行する()
    {
        var amount = await ReportAsync(new LlmUsage(LlmPurposes.TradeDecision, 1000, 2000, "claude-sonnet-5"), LlmPriceTable.From([]));

        amount.Should().Be(0m);
    }
}

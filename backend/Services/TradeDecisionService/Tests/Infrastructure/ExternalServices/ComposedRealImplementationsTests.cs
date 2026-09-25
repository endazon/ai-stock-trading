using AiStockTrading.Shared.Contracts.Backtest;
using AiStockTrading.Shared.Contracts.Events;
using AiStockTrading.TestSupport.Messaging;
using AiStockTrading.TestSupport.PlatformShim.Foundation.Extensions;
using AwesomeAssertions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using TradeDecisionService.Features.TradeDecision.RecordStage0Decisions;
using TradeDecisionService.Infrastructure.ExternalServices;
using Wolverine;
using Wolverine.Tracking;
using Xunit;

namespace TradeDecisionService.Tests;

// NFR, #947, IADR-0397: 本番の組み立てが結線する**本物**を試験で 1 度は通す。
//
// 組み立てガード（W3）の所見: 次の 3 ポートは、試験が偽物（RecordingReporter / FakeUnconfirmedNotifier / CapturingSink 等）
// だけを使い、本番の組み立てが実際に結線する実装を 1 度も通していなかった。本体を壊しても（発行しない・true を返す・
// 例外を投げる）、既存の試験は全部緑のままになる。
public class ComposedRealImplementationsTests
{
    private const string ServiceName = "ai-stock-trading.trade-decision-service";

    // FR-02, FR-04, FR-06, FR-11, #337, IADR-0247: 縮退の記録は ScreeningContextReduced として発行される
    // （監査台帳へ残り、月報が分割と切り詰めを分けて数える経路になる）。発行しなければ「静かに材料が減った」が残らない。
    [Fact]
    public async Task スクリーニング縮退の記録はScreeningContextReducedとして発行する()
    {
        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                // 本番と同じ配線（キュー名・fan-out）を用い、送信先だけ stub へ倒す。
                opts.UseAiStockTradingRabbitMq(ServiceName, "amqp://guest:guest@localhost:5672");
                opts.StubAllExternalTransports();
            })
            .StartAsync();
        var reduction = new ScreeningContextReduced(
            ["AAPL", "MSFT"], BatchCount: 2, Split: true, DroppedRagCount: 3, DroppedNewsCount: 0,
            UnresolvableOverflow: false, BudgetChars: 12_000, OccurredAt: new DateTimeOffset(2026, 9, 25, 0, 0, 0, TimeSpan.Zero));

        var session = await host.TrackActivityForTest().ExecuteAndWaitAsync(bus =>
            new PublishingScreeningReductionReporter(bus, NullLogger<PublishingScreeningReductionReporter>.Instance)
                .ReportAsync(reduction));

        session.Sent.MessagesOf<ScreeningContextReduced>().Should().ContainSingle()
            .Which.Should().BeEquivalentTo(reduction);

        await host.StopAsync();
    }

    // FR-15, ADR-0033 決定5, #632, IADR-0318: 出力先が未構成の安全既定は「書き出せなかった（false）」を返す。
    // true を返すと、記録が 1 件も残らないのに保存できたと報告される（費用だけ消費した実行が成功に見える）。
    [Fact]
    public async Task 記録の出力先が未構成の安全既定は保存できなかったと返す()
    {
        var set = new Stage0DecisionRecordSet(
            new DateOnly(2026, 9, 1), new DateOnly(2026, 9, 5), [], new DateOnly(2025, 1, 1),
            new DateTimeOffset(2026, 9, 25, 0, 0, 0, TimeSpan.Zero), "model", "strategy", []);

        var saved = await new NoStage0DecisionRecordSink().SaveAsync(set);

        saved.Should().BeFalse();
    }

    // UC-01, FR-09, IADR-0096 決定2: 日報未確定通知の安全既定は「通知しない」であり、判断の見送り経路を壊さない
    // （例外を投げれば、未確定の見送りそのものが失敗する）。
    [Fact]
    public async Task 日報未確定通知の安全既定は例外を出さずに完了する()
    {
        var notify = () => new NoOpDailyPolicyUnconfirmedNotifier().NotifyAsync();

        await notify.Should().NotThrowAsync();
    }
}

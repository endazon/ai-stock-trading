using RiskManagementService.Infrastructure.Steps;
using AiStockTrading.Shared.Contracts.Events;
using AiStockTrading.Shared.Contracts.Trading;
using AiStockTrading.TestSupport.Messaging;
using AiStockTrading.TestSupport.PlatformShim.Foundation.Extensions;
using AwesomeAssertions;
using Microsoft.Extensions.Hosting;
using Wolverine;
using Wolverine.Tracking;
using Xunit;

namespace RiskManagementService.Tests;

// FR-10, FR-03, UC-02, #331, IADR-0210 決定5: 損切りの実行機構はブローカー側逆指値へ一本化された
// （planning#88 裁定）。StopLossTriggered の購読は**検知の記録のみ**であり、
// **システムは決済注文（OrderApproved）を発行しない**——issue #331 受け入れ基準の否定形テスト。
// 二重決済（システム決済＋ブローカー逆指値）と、決済後に残る注文が反対建玉を生む事故の再発防止。
public class StopLossTriggeredConsumerTests
{
    private const string ServiceName = "ai-stock-trading.risk-management-service";

    // 本番と同じ配線（キュー名・fan-out・再試行・DLQ）を用い、送信先だけ stub へ倒す。
    private static Task<IHost> BuildHostAsync() =>
        Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.UseAiStockTradingRabbitMq(ServiceName, "amqp://guest:guest@localhost:5672");
                opts.Discovery.DisableConventionalDiscovery()
                    .IncludeType<StopLossTriggeredHandler>();
                opts.StubAllExternalTransports();
            })
            .StartAsync();

    private static StopLossTriggered Triggered(TradeSide positionSide = TradeSide.Buy) =>
        new(Guid.NewGuid(), "AAPL", Market.UnitedStates, positionSide, 10, 950m, 970m, DateTimeOffset.UtcNow);

    [Fact]
    public async Task 損切りライン到達を検知してもシステムは決済注文を発行しない_否定形()
    {
        using var host = await BuildHostAsync();

        var session = await host.TrackActivityForTest().InvokeMessageAndWaitAsync(Triggered());

        // 検知イベント自体は処理される（記録のみ）。
        session.Executed.MessagesOf<StopLossTriggered>().Should().NotBeEmpty();

        // 到達検知時は記録・通知のみ——決済（Close の OrderApproved）を発行しない（FR-10・逆指値一本化）。
        session.Sent.MessagesOf<OrderApproved>().Should().BeEmpty(
            "損切りの実行はブローカー側の逆指値が担う。システム側が決済注文を発行すると二重決済になる（planning#88）");

        await host.StopAsync();
    }

    [Fact]
    public async Task ショート建玉の損切り検知でも決済注文を発行しない_否定形()
    {
        // 逆指値の同時発注必須は建玉方向を問わない（ADR-0016 決定2(b)）。検知側の不発行も方向を問わない。
        using var host = await BuildHostAsync();

        var session = await host.TrackActivityForTest().InvokeMessageAndWaitAsync(Triggered(TradeSide.Sell));

        session.Sent.MessagesOf<OrderApproved>().Should().BeEmpty();

        await host.StopAsync();
    }
}

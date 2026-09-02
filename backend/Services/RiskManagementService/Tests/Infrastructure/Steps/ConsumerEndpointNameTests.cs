using AiStockTrading.Shared.Contracts.Events;
using AiStockTrading.TestSupport.PlatformShim.Foundation.Extensions;
using AwesomeAssertions;
using Xunit;

namespace RiskManagementService.Tests;

// FR-10, UC-01, UC-02, IADR-0106, IADR-0129, #258, #354: 取引判断の承認/拒否は本サービスが単独で担う。
// 他サービス（MarketMonitorService）も TradeDecisionMade を購読するため、同一キューを共有すると
// competing consumer になり判断を取りこぼす（#258 の事故）。本サービス側のキュー名を固定して、
// 相手側の変更がこちらの意図しない名前変更で相殺されないようにする。
//
// **不変条件は同じだが、その根拠が変わった**（ADR-0013 / IADR-0129 決定 1 の Wolverine 移行）:
//   旧: MassTransit の既定キュー名は consumer クラス名のみから導かれるため、クラス名がキューの一意性を担っていた。
//   新: Wolverine はキュー名の導出にハンドラのクラス名を一切使わない。キューは `<ServiceName>.<メッセージ型名>` であり、
//       一意性は ServiceName の一意性へ帰着する（重複は scripts/check-consumer-endpoint-names.js が CI で検出する）。
//
// なお **1 サービス内では「1 イベント型 = 1 キュー」になる**（旧 MassTransit は consumer ごとに別キューだった）。
// OrderApproved / OrderExecuted は 2 ハンドラが同一キュー・同一チェーンで動く。その意味変化（再試行の共有）を
// 許容できる根拠は IADR-0129 決定 10（双方の書き込みが冪等）に記載した。
public class ConsumerEndpointNameTests
{
    private const string ServiceName = "ai-stock-trading.risk-management-service";
    private const string MarketMonitorServiceName = "ai-stock-trading.market-monitor-service";

    [Fact]
    public void 取引判断購読のキュー名を固定する()
    {
        WolverineExtensions.QueueNameFor(ServiceName, typeof(TradeDecisionMade))
            .Should().Be("ai-stock-trading.risk-management-service.TradeDecisionMade");

        // 同じイベントを購読する MarketMonitorService とは構造的に衝突しない（#258 の再発経路が閉じている）。
        WolverineExtensions.QueueNameFor(ServiceName, typeof(TradeDecisionMade))
            .Should().NotBe(WolverineExtensions.QueueNameFor(MarketMonitorServiceName, typeof(TradeDecisionMade)));
    }

    [Fact]
    public void 本サービス内の各購読が互いに異なるキュー名を持つ()
    {
        var names = new[]
        {
            WolverineExtensions.QueueNameFor(ServiceName, typeof(TradeDecisionMade)),
            WolverineExtensions.QueueNameFor(ServiceName, typeof(StopLossTriggered)),
            WolverineExtensions.QueueNameFor(ServiceName, typeof(OrderApproved)),
            WolverineExtensions.QueueNameFor(ServiceName, typeof(OrderExecuted)),
            WolverineExtensions.QueueNameFor(ServiceName, typeof(OrderModified)),
            WolverineExtensions.QueueNameFor(ServiceName, typeof(OrderCancelled)),
            WolverineExtensions.QueueNameFor(ServiceName, typeof(BacktestEvaluated)),
            WolverineExtensions.QueueNameFor(ServiceName, typeof(BrokerPositionsObserved)),
        };

        names.Should().OnlyHaveUniqueItems("同一キューを共有すると購読が競合し、イベントを取りこぼす");
    }
}

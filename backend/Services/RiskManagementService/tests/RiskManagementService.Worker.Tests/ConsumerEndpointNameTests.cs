using AiStockTrading.RiskManagement.Worker.Composable.Steps;
using FluentAssertions;
using MassTransit;
using Xunit;

namespace AiStockTrading.RiskManagement.Worker.Tests;

// FR-10, UC-01, UC-02, IADR-0106, #258: 取引判断の承認/拒否は本サービスが単独で担う。
// MassTransit の既定エンドポイント名（キュー名）は consumer クラス名のみから導かれ namespace を含まないため、
// 他サービスが同名の consumer を持つと同一キューを共有して competing consumer になり、判断を取りこぼす
// （MarketMonitorService の同名 consumer との衝突が #258 の原因）。本サービス側のキュー名を固定して、
// 相手側の改名がこちらの意図しない名前変更で相殺されないようにする。
public class ConsumerEndpointNameTests
{
    [Fact]
    public void 取引判断購読のキュー名を固定する()
    {
        DefaultEndpointNameFormatter.Instance.Consumer<TradeDecisionMadeConsumer>()
            .Should().Be("TradeDecisionMade");
    }

    [Fact]
    public void 本サービス内の各購読が互いに異なるキュー名を持つ()
    {
        var names = new[]
        {
            DefaultEndpointNameFormatter.Instance.Consumer<TradeDecisionMadeConsumer>(),
            DefaultEndpointNameFormatter.Instance.Consumer<StopLossTriggeredConsumer>(),
            DefaultEndpointNameFormatter.Instance.Consumer<OrderApprovedLedgerConsumer>(),
            DefaultEndpointNameFormatter.Instance.Consumer<OrderExecutedLedgerConsumer>(),
            DefaultEndpointNameFormatter.Instance.Consumer<OrderApprovedActivityConsumer>(),
            DefaultEndpointNameFormatter.Instance.Consumer<OrderExecutedActivityConsumer>(),
            DefaultEndpointNameFormatter.Instance.Consumer<OrderModifiedActivityConsumer>(),
            DefaultEndpointNameFormatter.Instance.Consumer<OrderCancelledActivityConsumer>(),
            DefaultEndpointNameFormatter.Instance.Consumer<BacktestEvaluatedProjectionConsumer>(),
        };

        names.Should().OnlyHaveUniqueItems("同一キューを共有すると購読が競合し、イベントを取りこぼす");
    }
}

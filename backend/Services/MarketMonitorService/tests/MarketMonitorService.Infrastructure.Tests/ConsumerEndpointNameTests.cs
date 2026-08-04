using AiStockTrading.Shared.Contracts.Events;
using AiStockTrading.TestSupport.PlatformShim.Foundation.Extensions;
using AwesomeAssertions;
using Xunit;

namespace AiStockTrading.MarketMonitor.Infrastructure.Tests;

// FR-03, UC-02, IADR-0106, IADR-0129, #258, #354: 同じ TradeDecisionMade を購読する RiskManagementService と
// キューを取り合わないことを固定する。実測（取引フェーズ2 検証）では両サービス計 4 Pod が同一キューを
// consumers=4 で奪い合い、MarketMonitor 側へ配送された取引判断が承認も拒否も出さずに消えた（#258）。
//
// **不変条件は同じだが、その根拠が変わった**（ADR-0013 / IADR-0129 決定 1 の Wolverine 移行）:
//   旧: MassTransit の既定キュー名は consumer クラス名のみ（namespace 非包含）から導かれるため、
//       クラス名 `TradeDecisionMadeBaselineConsumer` が RiskManagement の `TradeDecisionMadeConsumer` と
//       違うことがキュー分離の唯一の根拠だった（＝人間の命名がキューの一意性を担っていた）。
//   新: Wolverine はキュー名の導出にハンドラのクラス名を**一切**使わない。分離は
//       `<ServiceName>.<メッセージ型名>` が担い、一意性は ServiceName の一意性へ帰着する
//       （ServiceName の重複は scripts/check-consumer-endpoint-names.js が CI で検出する）。
public class ConsumerEndpointNameTests
{
    private const string MarketMonitorServiceName = "ai-stock-trading.market-monitor-service";
    private const string RiskManagementServiceName = "ai-stock-trading.risk-management-service";

    [Fact]
    public void 取引判断購読のキュー名がリスク統制のキューと衝突しない()
    {
        var name = WolverineExtensions.QueueNameFor(MarketMonitorServiceName, typeof(TradeDecisionMade));

        name.Should().Be(
            "ai-stock-trading.market-monitor-service.TradeDecisionMade",
            "MarketMonitor の基準値更新は RiskManagement の承認/拒否判定とは別キューで全件受け取る");
        name.Should().NotBe(
            WolverineExtensions.QueueNameFor(RiskManagementServiceName, typeof(TradeDecisionMade)),
            "RiskManagementService の取引判断購読が用いるキュー名（#258 の衝突相手）");
    }
}

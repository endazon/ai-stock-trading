using AiStockTrading.MarketMonitor.Infrastructure.Composable.Steps;
using AwesomeAssertions;
using MassTransit;
using Xunit;

namespace AiStockTrading.MarketMonitor.Infrastructure.Tests;

// FR-03, UC-02, IADR-0106, #258: MassTransit の既定エンドポイント名（キュー名）は consumer クラス名のみから
// 導かれ、namespace を含まない（`DefaultEndpointNameFormatter`＝`Consumer` 接尾辞を落としたクラス名）。
// RiskManagementService も同じ `TradeDecisionMade` を購読する consumer を持つため、クラス名が衝突すると
// 両サービスが同一キューを宣言して competing consumer になり、pub/sub のつもりが取り合いになる。
// 実測（取引フェーズ2 検証）では両サービス計 4 Pod が同一キューを consumers=4 で奪い合い、MarketMonitor 側へ
// 配送された取引判断が承認も拒否も出さずに消えた。ここでキュー名の分離を固定する。
public class ConsumerEndpointNameTests
{
    [Fact]
    public void 取引判断購読のキュー名がリスク統制のキューと衝突しない()
    {
        var name = DefaultEndpointNameFormatter.Instance.Consumer<TradeDecisionMadeBaselineConsumer>();

        name.Should().Be(
            "TradeDecisionMadeBaseline",
            "MarketMonitor の基準値更新は RiskManagement の承認/拒否判定とは別キューで全件受け取る");
        name.Should().NotBe(
            "TradeDecisionMade",
            "RiskManagementService の TradeDecisionMadeConsumer が用いるキュー名（#258 の衝突相手）");
    }
}

using AiStockTrading.RiskManagement.Application.Adapters;
using AiStockTrading.RiskManagement.Domain;
using AiStockTrading.RiskManagement.Domain.Manipulation;
using AiStockTrading.Shared.Contracts.Trading;
using AwesomeAssertions;
using Xunit;

namespace AiStockTrading.RiskManagement.Application.Tests.Manipulation;

// FR-19, IADR-0006/0040: 判定ポート具体実装が intent の（銘柄, 市場）の直近窓を取得して純関数コアで判定することを固定する。
public class ManipulativeOrderPatternDetectorTests
{
    private static readonly DateTimeOffset Now = new(2026, 7, 11, 9, 35, 0, TimeSpan.Zero);

    private static OrderIntent Intent(string symbol = "AAPL", Market market = Market.UnitedStates) =>
        new(symbol, market, TradeSide.Buy, ProductType.Cash, BrokerProvider.InternalPaper, 10, 1_000m);

    private static PortfolioSnapshot Snapshot() => new() { Capital = 100_000m };

    // 見せ玉パターン: 6 発注すべて約定なし・即取消・同一方向。窓内（Now 直前）に配置する。
    private static void SeedManipulativeActivity(InMemoryOrderActivitySource source, string symbol, Market market)
    {
        for (var i = 0; i < 6; i++)
        {
            var placedAt = Now.AddSeconds(-60 + i);
            source.Record(symbol, market, new OrderActivityRecord
            {
                PlacedAt = placedAt,
                Side = TradeSide.Buy,
                Quantity = 10,
                FilledQuantity = 0,
                Status = OrderStatus.Cancelled,
                TerminalAt = placedAt.AddSeconds(1),
            });
        }
    }

    private static ManipulativeOrderPatternDetector CreateDetector(InMemoryOrderActivitySource source) =>
        new(source, new FakeClock(Now, new DateOnly(2026, 7, 11)),
            TradingDefaults.CreateManipulationDetectionSettings());

    [Fact]
    public void 該当履歴のある銘柄の注文は相場操縦とみなす()
    {
        var source = new InMemoryOrderActivitySource();
        SeedManipulativeActivity(source, "AAPL", Market.UnitedStates);
        var detector = CreateDetector(source);

        detector.IsSuspectedManipulation(Intent("AAPL"), Snapshot()).Should().BeTrue();
    }

    [Fact]
    public void 履歴のない銘柄の注文は相場操縦とみなさない()
    {
        var source = new InMemoryOrderActivitySource();
        SeedManipulativeActivity(source, "AAPL", Market.UnitedStates);
        var detector = CreateDetector(source);

        // 別銘柄（該当履歴なし）は空窓で無嫌疑。
        detector.IsSuspectedManipulation(Intent("MSFT"), Snapshot()).Should().BeFalse();
    }

    [Fact]
    public void 別市場の同一コードは混同しない()
    {
        // AAPL の該当履歴は米国市場のみ。日本市場の同一コード注文は該当履歴を持たず無嫌疑。
        var source = new InMemoryOrderActivitySource();
        SeedManipulativeActivity(source, "AAPL", Market.UnitedStates);
        var detector = CreateDetector(source);

        detector.IsSuspectedManipulation(Intent("AAPL", Market.Japan), Snapshot()).Should().BeFalse();
    }

    [Fact]
    public void 正常な履歴の銘柄は相場操縦とみなさない()
    {
        var source = new InMemoryOrderActivitySource();
        for (var i = 0; i < 6; i++)
        {
            var placedAt = Now.AddSeconds(-120 + i * 10);
            source.Record("AAPL", Market.UnitedStates, new OrderActivityRecord
            {
                PlacedAt = placedAt,
                Side = TradeSide.Buy,
                Quantity = 10,
                FilledQuantity = 10,
                Status = OrderStatus.Filled,
                TerminalAt = placedAt.AddSeconds(3),
            });
        }

        var detector = CreateDetector(source);

        detector.IsSuspectedManipulation(Intent("AAPL"), Snapshot()).Should().BeFalse();
    }
}

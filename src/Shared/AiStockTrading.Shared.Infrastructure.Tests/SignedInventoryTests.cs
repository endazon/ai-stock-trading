using AiStockTrading.Shared.Contracts.Trading;
using FluentAssertions;
using Xunit;

namespace AiStockTrading.Shared.Infrastructure.Tests;

// FR-10, FR-16, IADR-0033: 符号付き在庫・平均取得単価法の畳み込み（共有純関数）の各分岐を検証する。
// リスク管理 PortfolioProjection と報告書 PnlAggregator の単一情報源。
public class SignedInventoryTests
{
    [Fact]
    public void 建玉なしへの約定は新規建てで実現ゼロ()
    {
        var r = SignedInventory.Apply(new InventoryLot(0, 0m), signedQuantity: 10, price: 1_000m);

        r.Lot.Should().Be(new InventoryLot(10, 1_000m));
        r.RealizedPnl.Should().Be(0m);
        r.Reduced.Should().BeFalse();
    }

    [Fact]
    public void 同方向は加重平均で取得単価を更新し実現ゼロ()
    {
        // 10@1,000 に 10@1,400 を積み増し → 20@1,200。
        var r = SignedInventory.Apply(new InventoryLot(10, 1_000m), 10, 1_400m);

        r.Lot.Should().Be(new InventoryLot(20, 1_200m));
        r.RealizedPnl.Should().Be(0m);
        r.Reduced.Should().BeFalse();
    }

    [Fact]
    public void 一部決済は実現損益を計上し取得単価は不変()
    {
        // ロング 10@1,000 のうち 6 を @1,200 で決済 → 実現 (1200-1000)*6=1,200、残 4@1,000。
        var r = SignedInventory.Apply(new InventoryLot(10, 1_000m), -6, 1_200m);

        r.Lot.Should().Be(new InventoryLot(4, 1_000m));
        r.RealizedPnl.Should().Be(1_200m);
        r.Reduced.Should().BeTrue();
    }

    [Fact]
    public void 全決済は在庫ゼロで実現損益を計上する()
    {
        var r = SignedInventory.Apply(new InventoryLot(10, 1_000m), -10, 900m);

        r.Lot.Should().Be(new InventoryLot(0, 0m));
        r.RealizedPnl.Should().Be(-1_000m); // (900-1000)*10
        r.Reduced.Should().BeTrue();
    }

    [Fact]
    public void 反転は元建玉を全決済し余りを約定単価で新規建てする()
    {
        // ロング 5@1,000 に 売り 8@1,100 → 5 決済で実現 (1100-1000)*5=500、余り 3 をショート @1,100。
        var r = SignedInventory.Apply(new InventoryLot(5, 1_000m), -8, 1_100m);

        r.Lot.Should().Be(new InventoryLot(-3, 1_100m));
        r.RealizedPnl.Should().Be(500m);
        r.Reduced.Should().BeTrue();
    }

    [Fact]
    public void ショートの決済は取得単価マイナス決済単価で実現する()
    {
        // ショート -10@1,000 を 買い戻し 10@900 → 実現 (1000-900)*10=1,000。
        var r = SignedInventory.Apply(new InventoryLot(-10, 1_000m), 10, 900m);

        r.Lot.Should().Be(new InventoryLot(0, 0m));
        r.RealizedPnl.Should().Be(1_000m);
        r.Reduced.Should().BeTrue();
    }
}

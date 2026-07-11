namespace AiStockTrading.Shared.Contracts.Trading;

// FR-10, FR-16, IADR-0033: 符号付き在庫・平均取得単価法の畳み込み（純関数・単一情報源）。
// リスク管理の PortfolioProjection（#63 台帳射影）と報告書の PnlAggregator（損益集計）が共有し、方式変更時の
// 片側ドリフトを防ぐ。集約は「1 約定の在庫適用」に限定し、実現損益の合計・費用/税・当日境界などサービス固有の集計は含まない。

// 符号付き在庫（+ ロング / − ショート）と平均取得単価。数量 0 は建玉なし（AverageCost は 0）。
public readonly record struct InventoryLot(int Quantity, decimal AverageCost);

// 1 約定を適用した結果。Lot は適用後の在庫、RealizedPnl は在庫が減少した分の実現損益（増加のみなら 0）、
// Reduced は在庫が減少した（決済が発生した）か。
public readonly record struct InventoryFillResult(InventoryLot Lot, decimal RealizedPnl, bool Reduced);

public static class SignedInventory
{
    // 符号付き在庫へ 1 約定（signedQuantity: + 買い / − 売り、price: 約定単価）を適用する。
    public static InventoryFillResult Apply(InventoryLot current, int signedQuantity, decimal price)
    {
        if (current.Quantity == 0)
        {
            // 新規建て（実現なし）。
            return new InventoryFillResult(new InventoryLot(signedQuantity, price), 0m, false);
        }

        if (Math.Sign(current.Quantity) == Math.Sign(signedQuantity))
        {
            // 同方向 = 建て増し。加重平均で取得単価を更新（実現なし）。
            var newQty = current.Quantity + signedQuantity;
            var total = current.AverageCost * Math.Abs(current.Quantity) + price * Math.Abs(signedQuantity);
            return new InventoryFillResult(new InventoryLot(newQty, total / Math.Abs(newQty)), 0m, false);
        }

        // 反対方向 = 建玉の減少（および反転の可能性）。減少分に実現損益を計上する。
        var reduce = Math.Min(Math.Abs(current.Quantity), Math.Abs(signedQuantity));
        // ロング（Qty>0）を売り決済: (決済単価 − 取得単価) × 数量。ショートは (取得単価 − 決済単価) × 数量。
        var realized = current.Quantity > 0
            ? (price - current.AverageCost) * reduce
            : (current.AverageCost - price) * reduce;

        var resultQty = current.Quantity + signedQuantity;
        InventoryLot lot;
        if (resultQty == 0)
            lot = new InventoryLot(0, 0m);
        else if (Math.Sign(resultQty) != Math.Sign(current.Quantity))
            lot = new InventoryLot(resultQty, price); // 反転: 元の建玉を全決済し、余りを新規建玉として約定単価で建てる
        else
            lot = new InventoryLot(resultQty, current.AverageCost); // 同方向のまま一部決済: 取得単価は不変

        return new InventoryFillResult(lot, realized, true);
    }
}

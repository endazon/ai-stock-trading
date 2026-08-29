using AiStockTrading.Shared.Contracts.Trading;

namespace RiskManagementService.Features.RiskManagement;

// FR-05, FR-10, FR-11, #292, IADR-0118: 取引台帳の建玉射影とブローカ実ポジションを突き合わせる純関数。
//
// 比較するのは (銘柄, 市場) ごとの**符号付き数量のみ**。平均取得単価は手数料・端数・為替で必ずズレるため
// 判定に使わない（使えば恒常的に鳴り続け、本当の乖離が埋もれる）。
// どちらが正しいかは断定しない。双方の数量を並べて人の判断に委ねる（自動是正はしない・IADR-0118）。
public static class PositionDriftDetector
{
    public static IReadOnlyList<PositionDriftItem> Detect(
        IReadOnlyList<OpenPosition> ledgerPositions,
        IReadOnlyList<BrokerPositionSnapshot> brokerPositions)
    {
        ArgumentNullException.ThrowIfNull(ledgerPositions);
        ArgumentNullException.ThrowIfNull(brokerPositions);

        // 台帳の OpenPosition は「方向 × 正の数量」。ブローカの符号付き表現へ揃えてから比較する。
        var ledger = new Dictionary<(string Symbol, Market Market), int>();
        foreach (var p in ledgerPositions)
        {
            var signed = p.Side == TradeSide.Buy ? p.Quantity : -p.Quantity;
            if (signed == 0)
                continue;
            ledger[(p.Symbol, p.Market)] = ledger.GetValueOrDefault((p.Symbol, p.Market)) + signed;
        }

        var broker = new Dictionary<(string Symbol, Market Market), int>();
        foreach (var p in brokerPositions)
        {
            // 数量 0 の行（全決済済み）を返すブローカがあっても「保有」として扱わない。
            if (p.Quantity == 0)
                continue;
            broker[(p.Symbol, p.Market)] = broker.GetValueOrDefault((p.Symbol, p.Market)) + p.Quantity;
        }

        var drifts = new List<PositionDriftItem>();
        foreach (var key in ledger.Keys.Union(broker.Keys).OrderBy(k => k.Symbol, StringComparer.Ordinal).ThenBy(k => k.Market))
        {
            var ledgerQuantity = ledger.GetValueOrDefault(key);
            var brokerQuantity = broker.GetValueOrDefault(key);
            if (ledgerQuantity == brokerQuantity)
                continue;

            var kind = ledgerQuantity == 0 ? PositionDriftKind.BrokerOnly
                : brokerQuantity == 0 ? PositionDriftKind.LedgerOnly
                : PositionDriftKind.QuantityMismatch;

            drifts.Add(new PositionDriftItem(key.Symbol, key.Market, ledgerQuantity, brokerQuantity, kind));
        }

        return drifts;
    }
}

namespace AiStockTrading.Shared.Contracts.Trading;

// FR-05, FR-10, #292, IADR-0118: ブローカが保持する建玉 1 件のスナップショット。
//
// Quantity は**符号付き**（+ ロング / − ショート）。取引台帳の射影（OpenPosition の Side × Quantity）と
// 同じ表現に揃えることで、突合が符号付き整数の一致比較だけで済む。
// AverageCost は文脈として載せるが突合の判定には使わない（手数料・端数・為替で必ずズレるため）。
public sealed record BrokerPositionSnapshot(
    string Symbol,
    Market Market,
    int Quantity,
    decimal AverageCost);

using AiStockTrading.Shared.Contracts.Trading;

namespace AiStockTrading.RiskManagement.Application.State;

// FR-03, FR-10, IADR-0030: 市場監視（#10）へ公開する保有ポジション。市場監視の HeldPosition と同形
// （Symbol・Market・Side・Quantity・EntryPrice・StopLossPrice）で JSON 往復する。StopLossPrice は
// 既定損切り比率を平均取得単価へ適用した近似値（OpenPositionsService が導出）。
public sealed record OpenPositionView(
    string Symbol,
    Market Market,
    TradeSide Side,
    int Quantity,
    decimal EntryPrice,
    decimal StopLossPrice);

using AiStockTrading.MarketMonitor.Domain;

namespace AiStockTrading.MarketMonitor.Application.Ports;

// FR-03, FR-10: 損切りライン検知のための保有ポジション（銘柄・建玉方向・数量・損切り価格）の供給。
// 実データは発注執行（#13）・損益/監査（#17）連携で得る。本 Slice ではポートのみ定義し、後続で実体を配線する。
public interface IPositionStore
{
    IReadOnlyCollection<HeldPosition> GetOpenPositions();
}

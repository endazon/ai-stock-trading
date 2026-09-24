using TradeDecisionService.Features.TradeDecision;
using AiStockTrading.Shared.Contracts.Trading;

namespace TradeDecisionService.Infrastructure.ExternalServices;

// FR-04, #292, IADR-0119: 建玉照会の安全既定。常に null（不明）を返す。
// 不明のもとでは売り判断が見送りへ倒れる（＝裸の新規売りを出さない）。実照会は Worker が
// RiskManagement:BaseUrl 設定時に HttpHeldPositionProvider を配線したときのみ有効。
public sealed class NoOpHeldPositionProvider : IHeldPositionProvider
{
    // #865, IADR-0358: 未結線。「照会したが分からなかった」ではなく「照会していない」であるため、
    // 不明を理由に新規建てを止めない（既定構成の挙動を変えない）。
    public bool IsEnabled => false;

    public Task<int?> GetSignedQuantityAsync(
        string symbol, Market market, CancellationToken cancellationToken = default) =>
        Task.FromResult<int?>(null);

    // #854, IADR-0351 決定1: 保有状況も常に不明。プロンプトは「不明」と明示し、「保有なし」とは書かない。
    public Task<HeldPosition?> GetPositionAsync(
        string symbol, Market market, CancellationToken cancellationToken = default) =>
        Task.FromResult<HeldPosition?>(null);

    // #934, IADR-0390 決定2: 未約定の新規建て注文も常に不明（「無い」とは返さない）。
    public Task<WorkingEntryOrders?> GetWorkingEntryOrdersAsync(
        string symbol, Market market, CancellationToken cancellationToken = default) =>
        Task.FromResult<WorkingEntryOrders?>(null);
}

using AiStockTrading.Shared.Contracts.Trading;
using RiskManagementService.Features.RiskManagement;

namespace RiskManagementService.Infrastructure.ExternalServices;

// FR-10, ADR-0016 決定3, #967, IADR-0425 決定4: 借株可否の照会先（OrderExecution:BaseUrl）が構成されていないときの供給。
// **常に「分からない」を返す**＝空売り文脈は組まれず、新規の売り建ては BorrowUnavailable で拒否される（照会できないなら空売りしない）。
// 「借りられない（false）」と返さないのは、照会していないことを「照会した結果」と読ませないためである（Principle A）。
public sealed class UnavailableShortSellBorrowSource : IShortSellBorrowSource
{
    public const string Reason = "supplier-not-configured";

    public Task<ShortSellBorrowObservation> GetAsync(
        string symbol, Market market, CancellationToken cancellationToken = default) =>
        Task.FromResult(ShortSellBorrowObservation.Unknown(Reason));
}

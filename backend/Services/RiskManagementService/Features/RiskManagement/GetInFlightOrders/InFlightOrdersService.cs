using AiStockTrading.Shared.Contracts.Trading;
using RiskManagementService.Common.Abstractions;

namespace RiskManagementService.Features.RiskManagement.GetInFlightOrders;

// NFR, #1502, IADR-0600: live の E2E の取引影響層の門が読む「承認済みで終端していない注文」（新規建て・決済）。
// 走査の窓は直近 InFlightLookback（2 日）。🔴 終端のイベントが届かずに残った承認行（残骸）も窓の中なら返す
// —— 門はそれで止まる（安全側。IADR-0600 決定 3）。窓の外の残骸は返さない（門が永久に止まらないため）。
public sealed class InFlightOrdersService(IInFlightOrderSource source, IClock clock)
{
    /// <summary>未終端の注文を走査する窓（WorkingEntryOrdersService の 2 日と同じ幅）。</summary>
    public static readonly TimeSpan InFlightLookback = TimeSpan.FromDays(2);

    public IReadOnlyList<InFlightOrderView> Build() =>
        [.. source.GetInFlightOrders(clock.UtcNow - InFlightLookback)
            .OrderBy(o => o.ApprovedAt)
            .Select(o => new InFlightOrderView(
                o.Symbol,
                o.Market,
                o.PositionEffect == PositionEffect.Close ? InFlightOrderKind.Close : InFlightOrderKind.Entry,
                o.State,
                o.ApprovedAt))];
}

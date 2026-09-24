using AiStockTrading.Shared.Contracts.Trading;

namespace RiskManagementService.Features.RiskManagement;

/// <summary>
/// FR-10, #935, IADR-0394: 取引台帳の<b>決済（Close）の承認 1 件</b>と、その約定の時刻。
/// 損切りの射影（<c>StopOutProjection</c>）の入力である。
/// </summary>
/// <param name="DecisionId">承認の相関キー。</param>
/// <param name="Symbol">銘柄コード。</param>
/// <param name="Market">市場。</param>
/// <param name="Side">
/// <b>決済の売買方向</b>。売り（<see cref="TradeSide.Sell"/>）ならロング建玉を、買いならショート建玉を閉じた。
/// </param>
/// <param name="Source">承認行の由来。🔴 <c>null</c> は「由来が記録されていない」＝不明（損切りではない、ではない）。</param>
/// <param name="ApprovedAt">承認（S0 は武装、S1 は発動）の時刻。</param>
/// <param name="FillTimes">この承認に付いた約定（約定数量 &gt; 0 の行）の時刻。無ければ空。</param>
public sealed record LedgerCloseApproval(
    Guid DecisionId,
    string Symbol,
    Market Market,
    TradeSide Side,
    ApprovalSource? Source,
    DateTimeOffset ApprovedAt,
    IReadOnlyList<DateTimeOffset> FillTimes);

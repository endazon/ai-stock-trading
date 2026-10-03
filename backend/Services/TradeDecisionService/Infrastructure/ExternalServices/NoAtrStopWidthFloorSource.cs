using AiStockTrading.Shared.Contracts.Trading;
using TradeDecisionService.Features.TradeDecision;

namespace TradeDecisionService.Infrastructure.ExternalServices;

// FR-10, ADR-0049 決定2・決定5, #1120, IADR-0465 決定1: ATR(14) を供給しない既定の実装（`StopWidthFloor:Atr14:Enabled` が false のとき）。
// 常に null を返し（要求は 1 回も出さない）、取引判断は参照価格（アンカー後）の 2% を下限とする。プロンプトは従来のまま。
// #1122, IADR-0486 決定1: ATR の実装（Atr14StopWidthFloorSource）は設定で選ぶ（Program.cs）。発注執行の遡及は、取引判断が新規建ての
// 発注意図に立てる印（OrderIntent.StopFloorSource）で「下限を掛けて建てた行」を見分ける（IADR-0486 決定5・IADR-0472 の 2026-10-03 追記）。
public sealed class NoAtrStopWidthFloorSource : IStopWidthFloorSource
{
    public bool IsEnabled => false;

    public ValueTask<StopWidthFloor?> GetFloorAsync(
        string symbol, Market market, CancellationToken cancellationToken = default) =>
        ValueTask.FromResult<StopWidthFloor?>(null);

    public ValueTask<StopWidthFloor?> GetFloorAsOfAsync(
        string symbol, Market market, DateOnly tradingDay, CancellationToken cancellationToken = default) =>
        ValueTask.FromResult<StopWidthFloor?>(null);
}

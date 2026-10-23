using OrderExecutionService.Common.Abstractions;
using AiStockTrading.Shared.Contracts.Trading;

namespace OrderExecutionService.Tests;

// FR-10, NFR, #1444, IADR-0574 決定1（2026-10-23 追記）: 市場時間を問わない試験の組み立てに明示で渡す「常に場中」の偽物。
// 本番のアセンブリには置かない（ガード・実行器の市場時間の口は省略できない引数であり、既定の fail-open を持たない）。
public sealed class AlwaysOpenSessionGate : IMarketSessionGate
{
    public static readonly AlwaysOpenSessionGate Instance = new();

    public bool IsOpen(Market market, DateTimeOffset instant) => true;

    public DateTimeOffset? NextOpen(Market market, DateTimeOffset instant) => instant;
}

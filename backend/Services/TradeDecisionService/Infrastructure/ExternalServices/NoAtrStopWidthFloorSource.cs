using AiStockTrading.Shared.Contracts.Trading;
using TradeDecisionService.Features.TradeDecision;

namespace TradeDecisionService.Infrastructure.ExternalServices;

// FR-10, ADR-0049 決定2・決定5, #1120, IADR-0465 決定1: ATR(14) を供給しない既定の実装（配備までの暫定手段）。
// 常に null を返し、取引判断は参照価格（アンカー後）の 2% を下限とする。日足が判断へ通ってから ATR の実装へ差し替える。
// 🔴 #1136 独立監査 F2, IADR-0472（2026-10-01 追記）: **差し替える前に、発注執行の遡及（SoftwareStopFloorRetrofitter）の対象判定を見直す。**
// 遡及は「ラインが参照価格から 2% 以上離れているか」で下限の導入前の行を見分けており、参照価格の 2% より狭い ATR の下限で建てた行を
// 導入前の行と取り違えて 2% まで広げる（サイジングの想定より広い損切り）。下限の適用を発注執行へ渡して行に残すか、遡及を止める（#1122）。
public sealed class NoAtrStopWidthFloorSource : IStopWidthFloorSource
{
    public ValueTask<StopWidthFloor?> GetFloorAsync(
        string symbol, Market market, decimal anchoredPrice, CancellationToken cancellationToken = default) =>
        ValueTask.FromResult<StopWidthFloor?>(null);
}

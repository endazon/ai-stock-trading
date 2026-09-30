using AiStockTrading.Shared.Contracts.Trading;
using TradeDecisionService.Features.TradeDecision;

namespace TradeDecisionService.Infrastructure.ExternalServices;

// FR-04, ADR-0048 決定 3, #1118, IADR-0467 決定 6: 判断の出来高が無効（既定）のときの日足の口。
// 🔴 **外へ 1 回も要求しない**（取得枠に触れない）。判断は IsEnabled=false を見て出来高を引かず、プロンプトは従来の「未提供」の行のまま。
public sealed class NoOpDailyBarsProvider : IDailyBarsProvider
{
    public bool IsEnabled => false;

    public Task<ConfirmedDailyBars?> GetConfirmedBarsAsync(
        string symbol, Market market, CancellationToken cancellationToken = default) =>
        Task.FromResult<ConfirmedDailyBars?>(null);
}

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

    // #1139, IADR-0479 決定 1: Stage 0 の as-of の取得も要求しない（無効の間は記録の出来高も従来の「未提供」の行）。
    public Task<ConfirmedDailyBars?> GetConfirmedBarsAsOfAsync(
        string symbol, Market market, DateOnly tradingDay, CancellationToken cancellationToken = default) =>
        Task.FromResult<ConfirmedDailyBars?>(null);
}

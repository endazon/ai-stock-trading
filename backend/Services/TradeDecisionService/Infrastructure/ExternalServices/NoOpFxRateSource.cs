using AiStockTrading.Shared.Contracts.Ports;
using AiStockTrading.Shared.Contracts.Trading;
using Microsoft.Extensions.Logging;

namespace TradeDecisionService.Infrastructure.ExternalServices;

// FR-10, #257, #364, IADR-0107/0152: IFxRateSource の既定実装＝非基準通貨のレートを解決しない（null）。
// 実接続は Fx:Provider の明示指定でのみ有効化する（既定では外部へ 1 リクエストも出さない）。
// 基準通貨は定義上レート 1 のため、FX を有効化していない環境でも基準通貨の市場（米国株）は従来どおり取引できる。
// 非基準通貨は「レート無し＝新規建て見送り」（IADR-0107 決定3）に倒れるため、差し替え漏れに気づけるよう
// 初回に 1 回だけ警告する（判断サイクルごとのログ氾濫を避ける）。
public sealed class NoOpFxRateSource(ILogger<NoOpFxRateSource> logger) : IFxRateSource
{
    private int _warned;

    public Task<FxRate?> GetRateToBaseAsync(Currency quote, CancellationToken cancellationToken = default)
    {
        if (quote == MarketCurrency.Base)
            return Task.FromResult<FxRate?>(FxRate.Identity(quote));

        if (Interlocked.Exchange(ref _warned, 1) == 0)
        {
            logger.LogWarning(
                "NoOpFxRateSource を使用中: 為替レート源が未接続のため {Quote} 建て銘柄の新規建ては見送られます（IADR-0107）。",
                quote);
        }

        return Task.FromResult<FxRate?>(null);
    }
}

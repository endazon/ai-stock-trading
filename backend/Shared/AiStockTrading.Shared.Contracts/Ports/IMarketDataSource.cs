using AiStockTrading.Shared.Contracts.Trading;

namespace AiStockTrading.Shared.Contracts.Ports;

// FR-01, FR-03, ADR-0004: 情報源アダプタのポート（案A+ の各ソースを実装差し替えで切替）
public interface IMarketDataSource
{
    /// <summary>銘柄の最新の現在値を取得する。取得できない場合は null を返す。</summary>
    Task<Quote?> GetLatestQuoteAsync(string symbol, Market market, CancellationToken cancellationToken = default);
}

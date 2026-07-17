using AiStockTrading.InformationCollection.Application.Ports;
using AiStockTrading.InformationCollection.Application.State;
using AiStockTrading.InformationCollection.Domain;
using AiStockTrading.Shared.Infrastructure.Composable.Adapters.MarketData;

namespace AiStockTrading.InformationCollection.Worker.Composable.Adapters;

// FR-01, ADR-0004/0005: Finnhub（案A+・無料枠）から現在値を取得する最小の情報源アダプタ。構成で明示有効化したときのみ用いる。
// 取得失敗（レート制限・一時エラー）は当該銘柄をスキップしてログする（1 巡回を止めない）。実 API 前提の E2E は CI 対象外。
// IADR-0064: 無料枠は 60 回/分のため、銘柄ごとの要求前に自制する（FinnhubQuoteClient が IRateLimiter で行う）。
//
// IADR-0068: HTTP 呼び出し・応答解析は共有の FinnhubQuoteClient へ抽出した（市況（FR-10）の FinnhubMarketDataSource と
// 同じ呼び出しが 2 実装に割れないようにするため）。本クラスに残るのはスナップショット → RawInformationItem の写像のみで、
// 収集内容（high/low/prevClose を含む）は抽出前から不変。
internal sealed class FinnhubInformationSource(
    FinnhubQuoteClient client,
    IReadOnlyList<string> symbols)
    : IInformationSource
{
    public async Task<IReadOnlyList<RawInformationItem>> FetchAsync(CancellationToken cancellationToken = default)
    {
        var items = new List<RawInformationItem>();

        foreach (var symbol in symbols)
        {
            var quote = await client.GetQuoteAsync(symbol, cancellationToken).ConfigureAwait(false);
            if (quote is null)
                continue;

            items.Add(new RawInformationItem(
                InformationKind.Quote,
                "finnhub",
                symbol,
                $"{symbol} 現在値",
                $"current={quote.Current};high={quote.High};low={quote.Low};prevClose={quote.PreviousClose}",
                quote.AsOf));
        }

        return items;
    }
}

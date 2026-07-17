using System.Collections.Concurrent;
using AiStockTrading.Shared.Contracts.Ports;
using AiStockTrading.Shared.Contracts.Trading;

namespace AiStockTrading.Shared.Infrastructure.Composable.Adapters.MarketData;

// FR-10, IADR-0065: 現在値が取得できないときのフォールバック（#81 受け入れ条件「0 もしくは前回値」）を定義する
// デコレータ。内側のソースが取得できた値はそのまま返して (銘柄, 市場) ごとに保持し、取得不可（null）のときだけ
// 保持期限（maxStaleness）以内の前回値を返す。前回値が無い／期限超過なら null を返す＝当該建玉の含みは 0 になる。
//
// 期限を設ける理由: 期限なしの前回値は、市況断のあいだ古い価格に基づく含み・DD を無期限に信じ込ませる（＝実際には
// 下落していても DD が出ない）。古すぎる値は「取得不可」に落として 0 へ倒す方が保守的である。
//
// 経過時間は Quote.AsOf ではなく取得時刻（受信）を基準とする: 取得成功パスは AsOf を評価せずソースの値をそのまま
// 信じるため、フォールバックも同じ規約に揃える（AsOf の妥当性は当該ソースの責務）。
// 時刻は TimeProvider（本アセンブリの既存慣行＝PaperBrokerAdapter と同じ。各サービスの IClock ポートは共有物から
// 参照できないため）。複数の巡回から同時に引かれうるため保持は ConcurrentDictionary とする。
public sealed class LastKnownQuoteSource(
    IMarketDataSource inner,
    TimeProvider timeProvider,
    TimeSpan maxStaleness) : IMarketDataSource
{
    private readonly ConcurrentDictionary<(string Symbol, Market Market), (Quote Quote, DateTimeOffset FetchedAt)> _lastKnown = new();

    public async Task<Quote?> GetLatestQuoteAsync(string symbol, Market market, CancellationToken cancellationToken = default)
    {
        var key = (symbol, market);
        var quote = await inner.GetLatestQuoteAsync(symbol, market, cancellationToken).ConfigureAwait(false);

        if (quote is not null)
        {
            _lastKnown[key] = (quote, timeProvider.GetUtcNow());
            return quote;
        }

        // 取得不可: 保持期限以内の前回値があればそれを返す（無ければ null＝含み 0）。
        if (!_lastKnown.TryGetValue(key, out var last))
            return null;

        return timeProvider.GetUtcNow() - last.FetchedAt <= maxStaleness ? last.Quote : null;
    }
}

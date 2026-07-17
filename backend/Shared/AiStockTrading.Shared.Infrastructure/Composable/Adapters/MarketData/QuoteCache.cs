using System.Collections.Concurrent;
using AiStockTrading.Shared.Contracts.Trading;

namespace AiStockTrading.Shared.Infrastructure.Composable.Adapters.MarketData;

// FR-10, IADR-0066: 直近に取得できた現在値の保持（(銘柄, 市場) ごと）。#81 受け入れ条件のフォールバック
// 「0 もしくは前回値」の**前回値**側を担う唯一の実装で、保持期限（maxStaleness）の判定もここに集約する。
//
// 期限を設ける理由: 期限なしの前回値は、市況断のあいだ古い価格に基づく含み・DD を無期限に信じ込ませる（＝実際には
// 下落していても DD が出ない）。古すぎる値は「取得不可」として扱い、含みを 0 へ倒す方が保守的である。
//
// 経過時間は Quote.AsOf ではなく取得時刻（受信）を基準とする: 取得成功パスは AsOf を評価せずソースの値をそのまま
// 信じるため、フォールバックも同じ規約に揃える（AsOf の妥当性は当該ソースの責務）。
// 非同期の補充（QuoteRefreshService）と同期の読み出し（CachedCurrentPriceSource）から同時に触られるため
// ConcurrentDictionary とする。プロセス内メモリのみで永続化しない（再起動後は取得できるまで含み 0＝安全側）。
public sealed class QuoteCache
{
    private readonly ConcurrentDictionary<(string Symbol, Market Market), (Quote Quote, DateTimeOffset FetchedAt)> _entries = new();

    /// <summary>取得できた現在値を保持する（同一銘柄は上書き）。</summary>
    public void Set(Quote quote, DateTimeOffset fetchedAt)
    {
        ArgumentNullException.ThrowIfNull(quote);
        _entries[(quote.Symbol, quote.Market)] = (quote, fetchedAt);
    }

    /// <summary>保持期限以内の前回値を返す。無い、または期限超過なら null（＝取得不可として扱う）。</summary>
    public Quote? GetFresh(string symbol, Market market, DateTimeOffset now, TimeSpan maxStaleness)
    {
        if (!_entries.TryGetValue((symbol, market), out var entry))
            return null;

        return now - entry.FetchedAt <= maxStaleness ? entry.Quote : null;
    }
}

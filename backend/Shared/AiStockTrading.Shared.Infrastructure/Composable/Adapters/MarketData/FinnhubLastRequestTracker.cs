namespace AiStockTrading.Shared.Infrastructure.Composable.Adapters.MarketData;

// FR-01, FR-03, ADR-0043（計画）決定 1, #1044 項目 3, IADR-0437 決定 7: 同じ鍵で Finnhub へ送る要求の「直前の送出時刻」。
//
// 429 の分類（`FinnhubRateLimitClassifier`）は、直前の要求から 1 秒以内の「残りがあるのに拒否」を秒次（30 回/秒）の余地として
// 日次の手がかり（4301）にしない。直前の要求を `FinnhubQuoteClient` が自分の送出だけで数えていたため、同じプロセスで同じバケットを
// 共有しながらクライアントを通らない送り手（情報収集の企業ニュース `/company-news`）の直後の 429 を「直前から 1 秒以上」と読み、
// 4301 に記録し得た。同じ鍵の送り手が 1 つの追跡器を共有し、送出のたびに刻む（InformationSourceFactory の Finnhub 系が 1 つだけ作る）。
// 🔴 見えるのは同じプロセスの送出だけで、同じ鍵を使う他のプロセスの送出は見えない（残余リスク。chart README）。
public sealed class FinnhubLastRequestTracker(TimeProvider? timeProvider = null)
{
    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;

    // 直前に要求を送った時刻（UTC ticks。0＝まだ送っていない）。複数の送り手が並行に刻み得るため Interlocked で読み書きする。
    private long _lastRequestTicks;

    /// <summary>いま要求を送ることを刻み、その時刻と、直前の要求（どの送り手でも）からの間隔（初回は null）を返す。</summary>
    public (DateTimeOffset SentAt, TimeSpan? SincePrevious) MarkSent()
    {
        var sentAt = _time.GetUtcNow();
        var previousTicks = Interlocked.Exchange(ref _lastRequestTicks, sentAt.UtcTicks);
        TimeSpan? since = previousTicks > 0 ? sentAt - new DateTimeOffset(previousTicks, TimeSpan.Zero) : null;
        return (sentAt, since);
    }
}

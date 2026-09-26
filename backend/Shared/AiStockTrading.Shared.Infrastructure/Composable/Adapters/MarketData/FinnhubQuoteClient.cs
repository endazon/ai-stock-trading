using System.Net.Http.Json;
using AiStockTrading.Shared.Infrastructure.Composable.RateLimiting;
using Microsoft.Extensions.Logging;

namespace AiStockTrading.Shared.Infrastructure.Composable.Adapters.MarketData;

// FR-01, FR-10, ADR-0004/0005, IADR-0064/0068: Finnhub /quote の呼び出しと応答解析。
//
// IADR-0068 決定 2: 抽出の境界は「HTTP 呼び出し＋応答解析」に置き、Quote への写像には置かない。この上に薄い写像を
// 2 つ載せる。
//   - FinnhubMarketDataSource（共有・IMarketDataSource）: 現在値のみを Quote へ
//   - FinnhubInformationSource（情報収集・IInformationSource）: RawInformationItem へ（high/low/prevClose を保つ）
// Quote は Price しか持たないため、情報収集側を IMarketDataSource 経由にすると収集内容（FR-01）が劣化する。
// 共有するのは「Finnhub をどう呼ぶか」であって「何を取り出すか」ではない。
//
// 非成功応答（レート制限・一時エラー）は null を返し、呼び出し側が当該銘柄をスキップできるようにする
// （1 銘柄の失敗で巡回全体を止めない）。通信例外は握りつぶさずそのまま送出する（抽出前と同じ挙動。
// IMarketDataSource としての「取得不可＝null」への翻訳は FinnhubMarketDataSource が担う）。
// 実 API 前提の検証は手動 opt-in の live 検証（CI 対象外・IADR-0049）。
//
// ADR-0043（計画）決定 1, #1030, IADR-0437: 429 は `FinnhubRateLimitClassifier` で分け、**分次の窓では説明できない 429**
// （残りがあるのに拒否・リセットの後も拒否が続く）は日次上限の手がかりとして別のイベント（<see cref="DailyLimitClueEvent"/>）で記録する。
// 取得は従来どおり null（当該銘柄をスキップ）で、送出の挙動は変えない。
public sealed class FinnhubQuoteClient(
    HttpClient httpClient,
    string apiKey,
    IRateLimiter rateLimiter,
    ILogger logger,
    string baseUrl = FinnhubQuoteClient.DefaultBaseUrl,
    TimeProvider? timeProvider = null)
{
    public const string DefaultBaseUrl = "https://finnhub.io/api/v1";

    /// <summary>分次の窓では説明できない 429（日次上限の手がかり）のログのイベント。運用ログで拾う目印。</summary>
    public static readonly EventId DailyLimitClueEvent = new(4301, "FinnhubDailyLimitClue");

    private readonly string _baseUrl = baseUrl.TrimEnd('/');
    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;

    // 成功を挟まずに直前に受けた 429 のリセット時刻（UNIX 秒。0＝無し）。サービスごとに 1 つのクライアントを
    // 複数の呼び出しが共有し得るため Interlocked で読み書きする。
    private long _lastRejectedResetUnix;

    // #1037 の監査: 直前に要求を送った時刻（UTC ticks。0＝まだ送っていない）。秒次（30 回/秒）の 429 を日次の手がかりと
    // 取り違えないため、直前の要求からの間隔を分類へ渡す。
    private long _lastRequestTicks;

    /// <summary>1 銘柄の現在値スナップショットを取得する。非成功応答・空応答なら null。</summary>
    public async Task<FinnhubQuoteSnapshot?> GetQuoteAsync(string symbol, CancellationToken cancellationToken = default)
    {
        // IADR-0064: 429 を受けてから対処するのでは規約違反そのものを防げないため、送信前に自制する。
        await rateLimiter.WaitAsync(cancellationToken).ConfigureAwait(false);

        var sentAt = _time.GetUtcNow();
        var previousTicks = Interlocked.Exchange(ref _lastRequestTicks, sentAt.UtcTicks);
        TimeSpan? sincePreviousRequest = previousTicks > 0 ? sentAt - new DateTimeOffset(previousTicks, TimeSpan.Zero) : null;

        // API キーはヘッダー（X-Finnhub-Token）で渡す。URL クエリに入れると OTel の HttpClient 計装が
        // リクエスト URL（クエリ含む）をトレースへ出力し、キーが可観測性基盤に漏えいするため。
        var url = $"{_baseUrl}/quote?symbol={Uri.EscapeDataString(symbol)}";
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Add("X-Finnhub-Token", apiKey);

        using var response = await httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (response.StatusCode == System.Net.HttpStatusCode.TooManyRequests)
        {
            LogRateLimited(symbol, response, sincePreviousRequest);
            return null;
        }

        if (!response.IsSuccessStatusCode)
        {
            logger.LogWarning("Finnhub 取得失敗（銘柄 {Symbol}）: {Status}。この銘柄をスキップします。", symbol, (int)response.StatusCode);
            return null;
        }

        // 成功した＝分次の窓が戻っている。前回の 429 の記憶を消す（次の 429 は新しい窓で判定する）。
        Interlocked.Exchange(ref _lastRejectedResetUnix, 0);

        var quote = await response.Content.ReadFromJsonAsync<FinnhubQuoteResponse>(cancellationToken).ConfigureAwait(false);
        if (quote is null)
            return null;

        var asOf = quote.T > 0 ? DateTimeOffset.FromUnixTimeSeconds(quote.T) : DateTimeOffset.UtcNow;
        return new FinnhubQuoteSnapshot(symbol, quote.C, quote.H, quote.L, quote.Pc, asOf);
    }

    // ADR-0043 決定 1: 429 を分次で説明できるかで分けて記録する。前回の 429 のリセット時刻は、この応答のリセットで置き換える。
    private void LogRateLimited(string symbol, HttpResponseMessage response, TimeSpan? sincePreviousRequest)
    {
        var remaining = ReadLong(response, FinnhubRateLimitClassifier.RemainingHeader);
        var resetUnix = ReadLong(response, FinnhubRateLimitClassifier.ResetHeader);
        var reset = resetUnix is > 0 ? DateTimeOffset.FromUnixTimeSeconds(resetUnix.Value) : (DateTimeOffset?)null;
        var previousUnix = Interlocked.Exchange(ref _lastRejectedResetUnix, resetUnix is > 0 ? resetUnix.Value : 0);
        var previous = previousUnix > 0 ? DateTimeOffset.FromUnixTimeSeconds(previousUnix) : (DateTimeOffset?)null;
        var remainingInt = remaining is { } r ? (int)Math.Clamp(r, int.MinValue, int.MaxValue) : (int?)null;

        var kind = FinnhubRateLimitClassifier.Classify(remainingInt, reset, previous, _time.GetUtcNow(), sincePreviousRequest);
        if (FinnhubRateLimitClassifier.IsDailyLimitClue(kind))
        {
            logger.LogWarning(
                DailyLimitClueEvent,
                "Finnhub の 429 は分次の窓では説明できません（銘柄 {Symbol}・判定 {Kind}・残り {Remaining}・リセット {Reset}・前回の 429 のリセット {PreviousReset}）。"
                + "日次上限（未実測）の手がかりとして記録します（ADR-0043 決定1）。計画へ環流してください。この銘柄をスキップします。",
                symbol, kind, remainingInt, reset, previous);
            return;
        }

        logger.LogWarning(
            "Finnhub のレート制限（429。銘柄 {Symbol}・判定 {Kind}・残り {Remaining}・リセット {Reset}）。この銘柄をスキップします。",
            symbol, kind, remainingInt, reset);
    }

    private static long? ReadLong(HttpResponseMessage response, string header) =>
        response.Headers.TryGetValues(header, out var values)
        && long.TryParse(values.FirstOrDefault(), System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : null;

    // Finnhub /quote 応答（c=現在値, h=高値, l=安値, pc=前日終値, t=UNIX 時刻）。
    private sealed record FinnhubQuoteResponse(decimal C, decimal H, decimal L, decimal Pc, long T);
}

/// <summary>Finnhub /quote の取得結果。Finnhub 応答の写しであり共有の抽象ではない（IADR-0068 決定 2）。</summary>
public sealed record FinnhubQuoteSnapshot(
    string Symbol,
    decimal Current,
    decimal High,
    decimal Low,
    decimal PreviousClose,
    DateTimeOffset AsOf);

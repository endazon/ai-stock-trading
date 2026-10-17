using System.Globalization;
using System.Net.Http.Json;
using AiStockTrading.Shared.Contracts.Trading;
using AiStockTrading.Shared.Infrastructure.Composable.RateLimiting;
using BacktestService.Domain;
using BacktestService.Features.Backtest;
using Microsoft.Extensions.Logging;

namespace BacktestService.Infrastructure.ExternalServices;

// FR-15, ADR-0023 決定5, #1308, IADR-0528 決定8: 発注執行の日足の照会（`GET /order-execution/daily-bars`。前復権）から
// バックテストの過去データを取る過去データ源（`Backtest:BarData:Provider=order-execution`）。
//
// 利用者裁定 2026-10-13（#1219 のコメント https://github.com/endazon/ai-stock-trading/issues/1219#issuecomment-6134235872 の 3）:
// **Stage 0 では前復権と費用モデルの不整合を受け入れ、足は発注執行の `/daily-bars` 経由で取る（backtest の Pod に OpenD は配線しない）。**
// ADR-0023 決定5 の「確認するまで流さない」を Stage 0 の記録と評価に限って外す逸脱の受容は IADR-0528 に記録した
// （残余リスク: 借株料の欠落・取得枠の回復周期の未測定）。
//
// - 銘柄ごとに照会する。期間が照会の上限（400 暦日）を超えるときは分けて呼ぶ。
// - `Status=Available` の足だけを使う。それ以外（米国株でない・発注先が OpenD を持たない・自制の予算切れ・照会の失敗）・
//   非 2xx・本文の読み違い・要求と別の銘柄の答えは**その銘柄の欠測**（`HistoricalBarGap`）として残し、他の銘柄は続ける。
//   🔴 半端に取れた分は捨てる（分けた照会の一部だけ取れた銘柄を、部分の履歴で評価しない）。
// - 受け手の DTO は全項目 nullable（送り手の項目名が変わっても既定値で読まない。取引判断の `HttpDailyBarsSource` と同じ規律）。
// - 通信例外・キャンセルは握りつぶさず送出する（Stooq・moomoo の源と同じ方針）。
public sealed class HttpOrderExecutionHistoricalBarSource(
    HttpClient httpClient,
    IRateLimiter rateLimiter,
    ILogger<HttpOrderExecutionHistoricalBarSource> logger) : IHistoricalBarSource
{
    /// <summary>1 回の照会で求められる期間の上限（暦日。両端を含む。発注執行の `DailyBarsEndpoint.MaxRangeDays`）。</summary>
    public const int MaxRangeDays = 400;

    // 送り手 DailyBarsStatus の数値（web 既定で列挙は数値）。送り手の型は参照しない（別サービス）。
    internal const int StatusAvailable = 1;

    /// <summary>送り手 DailyBarsStatus.Unavailable の数値。</summary>
    internal const int StatusUnavailable = 0;

    public Task<HistoricalBarLoad> LoadBarsAsync(
        IReadOnlyList<(string Symbol, Market Market)> symbols,
        DateOnly from,
        DateOnly to,
        CancellationToken cancellationToken = default) =>
        LoadAsync(symbols, from, to, rateLimiter, FetchChunkAsync, logger, cancellationToken);

    // 1 回の照会（1 銘柄 × 1 期間）。非 2xx・本文の読み違いは失敗の理由を返す（その銘柄の欠測）。通信例外・キャンセルは送出する。
    private async Task<ChunkAnswer> FetchChunkAsync(
        string symbol, Market market, DateOnly chunkFrom, DateOnly chunkTo, CancellationToken cancellationToken)
    {
        using var response = await httpClient
            .GetAsync(
                $"/order-execution/daily-bars?symbol={Uri.EscapeDataString(symbol)}&market={(int)market}"
                    + $"&from={Format(chunkFrom)}&to={Format(chunkTo)}",
                cancellationToken)
            .ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
            return ChunkAnswer.Failed($"発注執行の日足の照会が {(int)response.StatusCode} を返した（{Format(chunkFrom)}〜{Format(chunkTo)}）");

        try
        {
            return ChunkAnswer.Answered(
                await response.Content.ReadFromJsonAsync<DailyBarsDto>(cancellationToken).ConfigureAwait(false));
        }
        catch (System.Text.Json.JsonException ex)
        {
            return ChunkAnswer.Failed($"発注執行の日足の応答を読めない: {ex.Message}");
        }
    }

    /// <summary>
    /// 銘柄ごとに期間を分けて照会し、取れた足と欠測を集める（REST と gRPC の実装が共有する 1 つ。NFR, IADR-0559 決定 4, #1391）。
    /// 🔴 半端に取れた分は捨てる（分けた照会の一部だけ取れた銘柄を、部分の履歴で評価しない）。照会の例外は握りつぶさず送出する。
    /// </summary>
    internal static async Task<HistoricalBarLoad> LoadAsync(
        IReadOnlyList<(string Symbol, Market Market)> symbols,
        DateOnly from,
        DateOnly to,
        IRateLimiter rateLimiter,
        Func<string, Market, DateOnly, DateOnly, CancellationToken, Task<ChunkAnswer>> fetchChunk,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(symbols);

        var bars = new List<PriceBar>();
        var gaps = new List<HistoricalBarGap>();
        if (from > to)
            return new HistoricalBarLoad(bars, gaps);

        foreach (var (symbol, market) in symbols)
        {
            var trimmed = (symbol ?? "").Trim();
            if (trimmed.Length == 0)
            {
                gaps.Add(new HistoricalBarGap(symbol ?? "", market, "銘柄が空"));
                continue;
            }

            var fetched = new List<PriceBar>();
            string? failure = null;
            foreach (var (chunkFrom, chunkTo) in Chunks(from, to))
            {
                await rateLimiter.WaitAsync(cancellationToken).ConfigureAwait(false);

                var answer = await fetchChunk(trimmed, market, chunkFrom, chunkTo, cancellationToken).ConfigureAwait(false);
                failure = answer.Failure ?? Interpret(answer.Dto, trimmed, market, chunkFrom, chunkTo, fetched);
                if (failure is not null)
                    break;
            }

            if (failure is not null)
            {
                logger.LogWarning("発注執行の日足の照会で {Symbol} を取得できませんでした（欠測として残します）: {Reason}", trimmed, failure);
                gaps.Add(new HistoricalBarGap(trimmed, market, failure));
                continue;
            }

            if (fetched.Count == 0)
            {
                gaps.Add(new HistoricalBarGap(trimmed, market, "発注執行の日足が 0 本（期間内のデータなし）"));
                continue;
            }

            bars.AddRange(fetched);
        }

        return new HistoricalBarLoad(bars, gaps);
    }

    /// <summary>[from, to] を照会の上限（<see cref="MaxRangeDays"/> 暦日）ごとに分ける（両端を含む・昇順）。</summary>
    public static IEnumerable<(DateOnly From, DateOnly To)> Chunks(DateOnly from, DateOnly to)
    {
        for (var start = from; start <= to;)
        {
            var end = start.AddDays(MaxRangeDays - 1);
            if (end > to)
                end = to;
            yield return (start, end);
            if (end == DateOnly.MaxValue)
                yield break;
            start = end.AddDays(1);
        }
    }

    // 1 回の照会の応答を読む。取れた足を bars へ足して null を返す。取れなければ理由を返す（足は足さない）。
    internal static string? Interpret(
        DailyBarsDto? dto, string symbol, Market market, DateOnly from, DateOnly to, List<PriceBar> bars)
    {
        if (dto is not { Symbol: { } answeredSymbol, Market: { } answeredMarket, Status: { } status })
            return "発注執行の日足の応答に銘柄・市場・状態のいずれかが無い（送り手との契約の食い違い）";
        if (!string.Equals(answeredSymbol, symbol, StringComparison.Ordinal) || answeredMarket != (int)market)
            return $"発注執行の日足の応答が要求と別の銘柄・市場を答えた（{answeredSymbol}/{answeredMarket}）";
        if (status != StatusAvailable)
            return $"発注執行の日足が取得できない（{dto.UnavailableReason ?? "理由なし"}）";

        var chunk = new List<PriceBar>();
        foreach (var bar in dto.Bars ?? [])
        {
            // 期間指定を無視した応答（データ源の揺れ）を素通しさせない。期間外の足は欄を検査せずに捨てる
            // （使わない足が壊れていても銘柄ごと欠測にしない。#1324 の AI レビュー）。日付の無い足は期間の内外を決められないので次で落とす。
            if (bar?.Date is { } barDate && (barDate < from || barDate > to))
                continue;
            if (bar is not { Date: { } date, Open: { } open, High: { } high, Low: { } low, Close: { } close, Volume: { } volume })
                return "発注執行の日足に欄の欠けた足がある（1 本でも欠けたら銘柄ごと欠測にする）";
            chunk.Add(new PriceBar(symbol, market, date, open, high, low, close, volume));
        }

        bars.AddRange(chunk);
        return null;
    }

    private static string Format(DateOnly date) => date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    // GET /order-execution/daily-bars の応答（全項目 nullable。送り手は DailyBarsView）。
    // gRPC の実装も線上の応答をこの形へ写してから Interpret を通す（状態は送り手 DailyBarsStatus の数値。UNSPECIFIED は null）。
    internal sealed record DailyBarsDto(
        string? Symbol, int? Market, int? Status, string? UnavailableReason, IReadOnlyList<DailyBarDto?>? Bars);

    internal sealed record DailyBarDto(DateOnly? Date, decimal? Open, decimal? High, decimal? Low, decimal? Close, long? Volume);

    /// <summary>1 回の照会の答え。<see cref="Failure"/> があればその銘柄の欠測、無ければ <see cref="Dto"/> を <see cref="Interpret"/> へ通す。</summary>
    internal sealed record ChunkAnswer(DailyBarsDto? Dto, string? Failure)
    {
        internal static ChunkAnswer Answered(DailyBarsDto? dto) => new(dto, null);

        internal static ChunkAnswer Failed(string failure) => new(null, failure);
    }
}

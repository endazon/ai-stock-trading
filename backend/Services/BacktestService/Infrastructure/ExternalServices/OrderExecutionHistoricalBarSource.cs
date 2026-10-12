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
public sealed class OrderExecutionHistoricalBarSource(
    HttpClient httpClient,
    IRateLimiter rateLimiter,
    ILogger<OrderExecutionHistoricalBarSource> logger) : IHistoricalBarSource
{
    /// <summary>1 回の照会で求められる期間の上限（暦日。両端を含む。発注執行の `DailyBarsEndpoint.MaxRangeDays`）。</summary>
    public const int MaxRangeDays = 400;

    // 送り手 DailyBarsStatus の数値（web 既定で列挙は数値）。送り手の型は参照しない（別サービス）。
    private const int StatusAvailable = 1;

    public async Task<HistoricalBarLoad> LoadBarsAsync(
        IReadOnlyList<(string Symbol, Market Market)> symbols,
        DateOnly from,
        DateOnly to,
        CancellationToken cancellationToken = default)
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
                failure = await FetchChunkAsync(trimmed, market, chunkFrom, chunkTo, fetched, cancellationToken).ConfigureAwait(false);
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

    // 1 回の照会。取れた足を bars へ足して null を返す。取れなければ理由を返す（足は足さない）。
    private async Task<string?> FetchChunkAsync(
        string symbol, Market market, DateOnly from, DateOnly to, List<PriceBar> bars, CancellationToken cancellationToken)
    {
        using var response = await httpClient
            .GetAsync(
                $"/order-execution/daily-bars?symbol={Uri.EscapeDataString(symbol)}&market={(int)market}"
                    + $"&from={Format(from)}&to={Format(to)}",
                cancellationToken)
            .ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
            return $"発注執行の日足の照会が {(int)response.StatusCode} を返した（{Format(from)}〜{Format(to)}）";

        DailyBarsDto? dto;
        try
        {
            dto = await response.Content.ReadFromJsonAsync<DailyBarsDto>(cancellationToken).ConfigureAwait(false);
        }
        catch (System.Text.Json.JsonException ex)
        {
            return $"発注執行の日足の応答を読めない: {ex.Message}";
        }

        if (dto is not { Symbol: { } answeredSymbol, Market: { } answeredMarket, Status: { } status })
            return "発注執行の日足の応答に銘柄・市場・状態のいずれかが無い（送り手との契約の食い違い）";
        if (!string.Equals(answeredSymbol, symbol, StringComparison.Ordinal) || answeredMarket != (int)market)
            return $"発注執行の日足の応答が要求と別の銘柄・市場を答えた（{answeredSymbol}/{answeredMarket}）";
        if (status != StatusAvailable)
            return $"発注執行の日足が取得できない（{dto.UnavailableReason ?? "理由なし"}）";

        var chunk = new List<PriceBar>();
        foreach (var bar in dto.Bars ?? [])
        {
            if (bar is not { Date: { } date, Open: { } open, High: { } high, Low: { } low, Close: { } close, Volume: { } volume })
                return "発注執行の日足に欄の欠けた足がある（1 本でも欠けたら銘柄ごと欠測にする）";
            // 期間指定を無視した応答（データ源の揺れ）を素通しさせない。
            if (date < from || date > to)
                continue;
            chunk.Add(new PriceBar(symbol, market, date, open, high, low, close, volume));
        }

        bars.AddRange(chunk);
        return null;
    }

    private static string Format(DateOnly date) => date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    // GET /order-execution/daily-bars の応答（全項目 nullable。送り手は DailyBarsView）。
    private sealed record DailyBarsDto(
        string? Symbol, int? Market, int? Status, string? UnavailableReason, IReadOnlyList<DailyBarDto?>? Bars);

    private sealed record DailyBarDto(DateOnly? Date, decimal? Open, decimal? High, decimal? Low, decimal? Close, long? Volume);
}

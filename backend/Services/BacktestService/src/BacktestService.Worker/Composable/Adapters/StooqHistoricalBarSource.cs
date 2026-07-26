using System.Globalization;
using AiStockTrading.Backtest.Application;
using AiStockTrading.Backtest.Domain;
using AiStockTrading.Shared.Contracts.Trading;
using AiStockTrading.Shared.Infrastructure.Composable.RateLimiting;
using Microsoft.Extensions.Logging;

namespace AiStockTrading.Backtest.Worker.Composable.Adapters;

// FR-15, ADR-0004, #208, IADR-0105: Stooq（無料・登録不要・日足 EOD・CSV）からの実過去データ取得。
// ADR-0004 は Stooq を「検証・学習用」の情報源として採用しており、バックテスト（FR-15）はその用途に当たる。
//
// 銘柄ごとに逐次取得し、送信前に必ずレート制御を通す（IADR-0064: 429 を受けてから対処するのでは規約違反そのものを
// 防げない）。Stooq は個人運営で SLA が無く予告なく停止し得るため（06_technical/02_datasource-candidates.md）、
// 非成功応答・データなし・解析不能はその銘柄を欠測（HistoricalBarGap）として記録し、他銘柄の取得は続ける。
// 通信例外は握りつぶさず送出する（FinnhubQuoteClient と同じ方針）。取得が失敗すればバックテストは完走せず、
// verdict も出ない＝昇格は起きない（fail-safe）。
//
// 取得したデータは個人利用の範囲に留め、外部へ再配信しない（計画書 02_datasource-candidates.md の運用制約）。
public sealed class StooqHistoricalBarSource(
    HttpClient httpClient,
    IRateLimiter rateLimiter,
    ILogger<StooqHistoricalBarSource> logger,
    string baseUrl = StooqHistoricalBarSource.DefaultBaseUrl) : IHistoricalBarSource
{
    public const string DefaultBaseUrl = "https://stooq.com";

    private readonly string _baseUrl = baseUrl.TrimEnd('/');

    public async Task<HistoricalBarLoad> LoadBarsAsync(
        IReadOnlyList<(string Symbol, Market Market)> symbols,
        DateOnly from,
        DateOnly to,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(symbols);

        var bars = new List<PriceBar>();
        var gaps = new List<HistoricalBarGap>();

        foreach (var (symbol, market) in symbols)
        {
            if (!StooqSymbolMapper.TryMap(symbol, market, out var stooqSymbol))
            {
                // 外部へ要求せずに欠測とする（誤った記法で叩かない）。
                Record(gaps, symbol, market, "Stooq の銘柄記法へ写像できない");
                continue;
            }

            var csv = await FetchAsync(stooqSymbol, symbol, market, from, to, gaps, cancellationToken)
                .ConfigureAwait(false);
            if (csv is null)
                continue;

            if (!StooqDailyCsvParser.TryParse(csv, symbol, market, out var parsed, out var reason))
            {
                Record(gaps, symbol, market, reason);
                continue;
            }

            // 期間指定を無視した応答（データ源の揺れ）を素通しさせない。
            bars.AddRange(parsed.Where(b => b.Date >= from && b.Date <= to));
        }

        return new HistoricalBarLoad(bars, gaps);
    }

    // 応答本文を返す。取得できなければ欠測として記録し null を返す。
    private async Task<string?> FetchAsync(
        string stooqSymbol,
        string symbol,
        Market market,
        DateOnly from,
        DateOnly to,
        List<HistoricalBarGap> gaps,
        CancellationToken cancellationToken)
    {
        await rateLimiter.WaitAsync(cancellationToken).ConfigureAwait(false);

        var url = $"{_baseUrl}/q/d/l/?s={Uri.EscapeDataString(stooqSymbol)}" +
                  $"&d1={from.ToString("yyyyMMdd", CultureInfo.InvariantCulture)}" +
                  $"&d2={to.ToString("yyyyMMdd", CultureInfo.InvariantCulture)}&i=d";

        using var response = await httpClient.GetAsync(url, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            Record(gaps, symbol, market, $"取得失敗（HTTP {(int)response.StatusCode}）");
            return null;
        }

        return await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
    }

    private void Record(List<HistoricalBarGap> gaps, string symbol, Market market, string reason)
    {
        // 欠測は無音破棄しない。バックテストの合否に影響する（銘柄が丸ごと欠けた検証は合格材料にできない）。
        logger.LogWarning(
            "Stooq から過去データを取得できませんでした（銘柄 {Symbol} / {Market}）: {Reason}。欠測として記録します。",
            symbol, market, reason);
        gaps.Add(new HistoricalBarGap(symbol, market, reason));
    }
}

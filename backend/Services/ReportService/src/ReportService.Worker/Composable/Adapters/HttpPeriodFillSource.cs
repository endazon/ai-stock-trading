using System.Net.Http.Json;
using AiStockTrading.Report.Application.Ports;
using AiStockTrading.Report.Domain;
using AiStockTrading.Shared.Contracts.Trading;
using Microsoft.Extensions.Logging;

namespace AiStockTrading.Report.Worker.Composable.Adapters;

// FR-06, FR-16, IADR-0115 決定5, #280: 期間の約定を権威源（リスク管理 #12 の取引台帳）から s2s 同期照会する
// （GET /risk-controls/fills・OwnerOrService・IADR-0051）。IADR-0095（watchlist）と同型の作法。
//
// 供給不達（非 2xx・timeout・例外・不正応答）はすべて**空列**へ倒す。報告書は発注判断を行わないため、欠測が
// 過大発注へ繋がる経路が無く、「数値 0 のドラフトを提示して気付かせる」ほうが「何も出さない」より安全である。
internal sealed class HttpPeriodFillSource(HttpClient httpClient, ILogger<HttpPeriodFillSource> logger)
    : IPeriodFillSource
{
    public async Task<IReadOnlyList<PeriodTradeFill>> GetFillsAsync(
        DateOnly fromInclusive,
        DateOnly toInclusive,
        CancellationToken cancellationToken = default)
    {
        var path = $"/risk-controls/fills?from={fromInclusive:yyyy-MM-dd}&to={toInclusive:yyyy-MM-dd}";

        try
        {
            using var response = await httpClient.GetAsync(path, cancellationToken).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                logger.LogWarning(
                    "期間約定の照会に失敗しました（{Status}・{From}〜{To}）。数値 0 の報告書として生成を続けます。",
                    (int)response.StatusCode, fromInclusive, toInclusive);
                return [];
            }

            var rows = await response.Content
                .ReadFromJsonAsync<List<LedgerFillDto>>(cancellationToken)
                .ConfigureAwait(false);

            if (rows is null)
            {
                logger.LogWarning("期間約定の応答が不正（null）でした。数値 0 の報告書として生成を続けます。");
                return [];
            }

            return [.. rows.Where(r => !string.IsNullOrWhiteSpace(r.Symbol)).Select(ToFill)];
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning("期間約定の照会がタイムアウトしました（{From}〜{To}）。数値 0 の報告書として生成を続けます。",
                fromInclusive, toInclusive);
            return [];
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "期間約定の照会で例外が発生しました（{From}〜{To}）。数値 0 の報告書として生成を続けます。",
                fromInclusive, toInclusive);
            return [];
        }
    }

    // 台帳の約定単価はローカル通貨（IADR-0107 決定1）。報告書の集計は基準通貨（円）建てのため、
    // 同伴レート（承認時に固定された FxRateToBase）で換算する。LedgerFill.PriceInBase と同じ規則だが、
    // 別サービスの型は参照しないため、ここで一次フィールドから導出する。
    private static PeriodTradeFill ToFill(LedgerFillDto r) => new(
        r.Symbol, r.Market, r.Side, r.PositionEffect, r.Quantity, r.Price * r.FxRateToBase, r.ExecutedAt);

    // 権威源の LedgerFill と同形（camelCase・列挙は数値で往復する）。StopLossPrice は報告書の集計に不要のため持たない。
    private sealed record LedgerFillDto(
        string Symbol,
        Market Market,
        TradeSide Side,
        PositionEffect PositionEffect,
        int Quantity,
        decimal Price,
        DateTimeOffset ExecutedAt,
        decimal FxRateToBase = 1m);
}

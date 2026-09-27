using System.Net.Http.Json;
using AiStockTrading.Shared.Contracts.Trading;
using InformationCollectionService.Features.InformationCollection;
using Microsoft.Extensions.Logging;

namespace InformationCollectionService.Infrastructure.ExternalServices;

// FR-01, FR-13, #1015, IADR-0435: 市場監視（権威源）の GET /monitor/watchlist（OwnerOrService）を s2s（trading-service）で照会する。
// 取引判断の定時サイクル（HttpWatchlistProvider・IADR-0095）と同じ口・同じ作法（短いタイムアウト・サービストークン）。
//
// 🔴 **原則 A: どの失敗も「分からない」（null）で返す。空の一覧へ倒さない。** 非 2xx・タイムアウト・例外・読めない本文に加え、
// **項目の欠けた行・値域外の市場が 1 つでもあれば一覧ごと「分からない」とする**。欠けた行を黙って落とすと、欠けた銘柄だけが
// 収集から消えたまま「読めた」ことになり、直前の値へも倒れない（既定値〔市場 0＝日本〕で読むと米国の銘柄が黙って外れる）。
public sealed class HttpMarketMonitorWatchlistReader(
    HttpClient httpClient,
    ILogger<HttpMarketMonitorWatchlistReader> logger)
    : IWatchlistReader
{
    public async Task<IReadOnlyList<WatchedSymbol>?> ReadAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            using var response = await httpClient
                .GetAsync("/monitor/watchlist", cancellationToken)
                .ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                logger.LogWarning("監視銘柄（watchlist）の照会に失敗しました（{Status}）。", (int)response.StatusCode);
                return null;
            }

            // 送り手 MonitoredSymbol（web 既定＝camelCase・列挙は数値）。欠落を既定値と区別するため nullable で受ける。
            var rows = await response.Content
                .ReadFromJsonAsync<List<WatchlistRow?>>(cancellationToken)
                .ConfigureAwait(false);
            if (rows is null)
            {
                logger.LogWarning("監視銘柄（watchlist）の応答が不正（null）です。");
                return null;
            }

            return Interpret(rows, logger);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning("監視銘柄（watchlist）の照会がタイムアウトしました。");
            return null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "監視銘柄（watchlist）の照会で例外が発生しました。");
            return null;
        }
    }

    // NFR, IADR-0446 決定 4, #1061 (#753): 行の解釈は gRPC 実装（GrpcMarketMonitorWatchlistReader）と共有する（`internal static`）。
    // 1 行でも項目が欠けていれば（行が null・銘柄が空・市場が欠落または値域外）一覧ごと不明（null）。
    internal static IReadOnlyList<WatchedSymbol>? Interpret(IReadOnlyList<WatchlistRow?> rows, ILogger logger)
    {
        var result = new List<WatchedSymbol>(rows.Count);
        foreach (var row in rows)
        {
            if (row is null
                || string.IsNullOrWhiteSpace(row.Symbol)
                || row.Market is not { } market
                || !Enum.IsDefined(market))
            {
                logger.LogWarning("監視銘柄（watchlist）の応答に項目の欠けた行・値域外の市場があるため、一覧ごと不明として扱います。");
                return null;
            }

            result.Add(new WatchedSymbol(row.Symbol.Trim(), market));
        }

        return result;
    }

    // GET /monitor/watchlist の 1 行（MonitoredSymbol と同じ項目名）。gRPC 実装も同じ行へ写してから解釈する。
    internal sealed record WatchlistRow(string? Symbol, Market? Market);
}

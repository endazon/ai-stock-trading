using AiStockTrading.Shared.Contracts.Trading;
using InformationCollectionService.Features.InformationCollection;
using Microsoft.Extensions.Logging;
using Proto = AiStockTrading.Shared.Grpc.MarketMonitor.V1;

namespace InformationCollectionService.Infrastructure.ExternalServices;

// NFR, FR-01, FR-13, MSP:ADR-0029, IADR-0284 決定 5（段 4）, IADR-0435, IADR-0446, #1061 (#753):
// 市場監視の監視銘柄を **gRPC 生成クライアント**（`WatchlistRead/GetWatchlist`）で読む `IWatchlistReader` の 2 つ目の実装。
// REST 実装（HttpMarketMonitorWatchlistReader）と並走する（**既定は REST**。`MarketMonitor:Grpc` を宣言したときだけ選ばれる）。
//
// 🔴 **行の解釈は REST と同じ 1 つ**（`HttpMarketMonitorWatchlistReader.Interpret`）。gRPC は線上の行を同じ nullable の行へ写すだけ
// （銘柄の欠落は null、市場の未指定・未知は null ＝ 1 行でもあれば一覧ごと不明）。
// 🔴 **どの失敗も「分からない」（null）で返す。空の一覧へ倒さない**（REST と同じ）。
public sealed class GrpcMarketMonitorWatchlistReader(
    MarketMonitorGrpcTransport transport,
    ILogger<GrpcMarketMonitorWatchlistReader> logger)
    : IWatchlistReader
{
    public async Task<IReadOnlyList<WatchedSymbol>?> ReadAsync(CancellationToken cancellationToken = default)
    {
        var response = await transport.CallAsync(
            "監視銘柄（watchlist）",
            "分からないものとして扱います（直前に読めた対象を使い続けます）。",
            (client, options) => client.GetWatchlistAsync(new Proto.GetWatchlistRequest(), options),
            cancellationToken).ConfigureAwait(false);

        return response is null
            ? null
            : HttpMarketMonitorWatchlistReader.Interpret([.. response.Items.Select(ToRow)], logger);
    }

    internal static HttpMarketMonitorWatchlistReader.WatchlistRow? ToRow(Proto.WatchlistItem item) =>
        new HttpMarketMonitorWatchlistReader.WatchlistRow(MarketMonitorWire.Symbol(item), MarketMonitorWire.Market(item.Market));
}

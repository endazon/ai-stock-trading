using AiStockTrading.Shared.Contracts.Trading;
using Microsoft.Extensions.Logging;
using TradeDecisionService.Features.TradeDecision;
using Proto = AiStockTrading.Shared.Grpc.MarketMonitor.V1;

namespace TradeDecisionService.Infrastructure.ExternalServices;

// NFR, FR-02, FR-04, FR-13, MSP:ADR-0029, IADR-0284 決定 5（段 4）, IADR-0095, IADR-0440, IADR-0446, #1061 (#753):
// 監視銘柄を **gRPC 生成クライアント**（`WatchlistRead/GetWatchlist`）で照会する `IWatchlistProvider` の 2 つ目の実装。
// REST 実装（HttpWatchlistProvider）と並走する（**既定は REST**。`MarketMonitor:Grpc` を宣言したときだけ選ばれる）。
//
// 🔴 **行の解釈は REST と同じ 1 つ**（`HttpWatchlistProvider.ToCycleWatchlist` / `ToAuthoritativeWatchlist`）。gRPC は線上の行を
// 同じ nullable の行へ写すだけ（銘柄の欠落は null、市場の未指定・未知は null）。
// 🔴 **倒す向きは REST と同じ**: 定時サイクルは読めなければ構成の監視銘柄（fallback）、判断のプロンプトは null（不明）。
public sealed class GrpcWatchlistProvider(
    MarketMonitorGrpcTransport transport,
    IWatchlistProvider fallback,
    ILogger<GrpcWatchlistProvider> logger)
    : IWatchlistProvider
{
    public async Task<IReadOnlyList<WatchedSymbol>> GetWatchlistAsync(CancellationToken cancellationToken = default)
    {
        var rows = await TryFetchAsync(cancellationToken).ConfigureAwait(false);
        if (rows is not null)
            return HttpWatchlistProvider.ToCycleWatchlist(rows);

        logger.LogWarning("監視銘柄（watchlist）を権威源から読めないため、既定 watchlist（構成）へフォールバックします。");
        return await fallback.GetWatchlistAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<WatchedSymbol>?> GetAuthoritativeWatchlistAsync(CancellationToken cancellationToken = default)
    {
        var rows = await TryFetchAsync(cancellationToken).ConfigureAwait(false);
        return rows is null ? null : HttpWatchlistProvider.ToAuthoritativeWatchlist(rows, logger);
    }

    private async Task<IReadOnlyList<HttpWatchlistProvider.WatchlistRow>?> TryFetchAsync(CancellationToken cancellationToken)
    {
        var response = await transport.CallAsync(
            "監視銘柄（watchlist）",
            "読めなかったものとして扱います。",
            (client, options) => client.GetWatchlistAsync(new Proto.GetWatchlistRequest(), options),
            cancellationToken).ConfigureAwait(false);

        return response is null ? null : [.. response.Items.Select(ToRow)];
    }

    internal static HttpWatchlistProvider.WatchlistRow ToRow(Proto.WatchlistItem item) =>
        new(MarketMonitorWire.Symbol(item), MarketMonitorWire.Market(item.Market));
}

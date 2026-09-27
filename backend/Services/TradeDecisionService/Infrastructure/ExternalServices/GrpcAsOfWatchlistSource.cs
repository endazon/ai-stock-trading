using Microsoft.Extensions.Logging;
using TradeDecisionService.Features.TradeDecision.RecordStage0Decisions;
using Proto = AiStockTrading.Shared.Grpc.MarketMonitor.V1;

namespace TradeDecisionService.Infrastructure.ExternalServices;

// NFR, FR-04, FR-15, MSP:ADR-0029, IADR-0284 決定 5（段 4）, IADR-0442, IADR-0446, #1061 (#753):
// 当時の監視銘柄を **gRPC 生成クライアント**（`WatchlistRead/GetWatchlistAsOf`）で読む `IAsOfWatchlistSource` の 2 つ目の実装。
// REST 実装（HttpAsOfWatchlistSource）と並走する（**既定は REST**。`MarketMonitor:Grpc` を宣言したときだけ選ばれる）。
//
// 🔴 **応答の解釈は REST と同じ 1 つ**（`HttpAsOfWatchlistSource.Interpret`）。gRPC は線上の応答を同じ nullable の形へ写すだけ
// （再構成の可否の欠落は null、一覧の入れ物が無ければ一覧は null、銘柄の欠落・市場の未指定は null）。
// 🔴 **読めなかったことを空の一覧へ倒さない**: 照会の失敗は「再構成できない」（理由つき）。時刻は REST と同じ UTC の「Z」付きで送る。
public sealed class GrpcAsOfWatchlistSource(MarketMonitorGrpcTransport transport, ILogger<GrpcAsOfWatchlistSource> logger)
    : IAsOfWatchlistSource
{
    public async Task<AsOfWatchlist> GetWatchlistAtAsync(DateTimeOffset at, CancellationToken cancellationToken = default)
    {
        var response = await transport.CallAsync(
            "当時の監視銘柄",
            "再構成できないものとして扱います（その記録は合否から外れます）。",
            (client, options) => client.GetWatchlistAsOfAsync(
                new Proto.GetWatchlistAsOfRequest { At = HttpAsOfWatchlistSource.WireInstant(at) }, options),
            cancellationToken).ConfigureAwait(false);

        return response is null
            ? HttpAsOfWatchlistSource.Unavailable("市場監視の照会に失敗しました（gRPC）。", logger)
            : HttpAsOfWatchlistSource.Interpret(ToBody(response), logger);
    }

    internal static HttpAsOfWatchlistSource.AsOfBody ToBody(Proto.GetWatchlistAsOfResponse response) =>
        new(
            response.HasReconstructed ? response.Reconstructed : null,
            response.Symbols is null
                ? null
                : [.. response.Symbols.Items.Select(i =>
                    (HttpAsOfWatchlistSource.AsOfRow?)new HttpAsOfWatchlistSource.AsOfRow(
                        MarketMonitorWire.Symbol(i), MarketMonitorWire.Market(i.Market)))],
            response.HasReason ? response.Reason : null);
}

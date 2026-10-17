using AiStockTrading.Shared.Contracts.Trading;
using InformationCollectionService.Features.InformationCollection;
using Microsoft.Extensions.Logging;
using Proto = AiStockTrading.Shared.Grpc.RiskManagement.V1;

namespace InformationCollectionService.Infrastructure.ExternalServices;

// NFR, FR-01, FR-02, MSP:ADR-0029, IADR-0284 決定 5, IADR-0547 決定 1, IADR-0557 決定 2・4, #1388 (#753):
// 収集の対象へ足す保有中の銘柄を **gRPC 生成クライアント**（`RiskControlsRead/GetOpenPositions`）で読む `IHeldSymbolsProvider` の
// 2 つ目の実装。REST 実装（HttpRiskManagementHeldSymbolsProvider）と並走する（**既定は REST**。`RiskManagement:Grpc` を宣言したときだけ選ばれる）。
//
// 🔴 **行の解釈は REST と同じ 1 つ**（`HttpRiskManagementHeldSymbolsProvider.Interpret`）。gRPC は線上の行を同じ nullable の行へ写すだけ
// （銘柄・数量の欠落は null、市場・方向の未指定・未知は null ＝ 1 行でもあれば一覧ごと不明）。
// 🔴 **どの失敗も「分からない」（null）で返す。空の一覧へ倒さない**（REST と同じ。読めない巡回は監視銘柄だけから決める）。
public sealed class GrpcRiskManagementHeldSymbolsProvider(
    RiskManagementGrpcTransport transport,
    ILogger<GrpcRiskManagementHeldSymbolsProvider> logger)
    : IHeldSymbolsProvider
{
    public async Task<IReadOnlyList<WatchedSymbol>?> GetHeldSymbolsAsync(CancellationToken cancellationToken = default)
    {
        var response = await transport.CallAsync(
            "保有銘柄（open-positions）",
            "分からないものとして扱います（この巡回は監視銘柄だけから対象を決めます）。",
            (client, options) => client.GetOpenPositionsAsync(new Proto.GetOpenPositionsRequest(), options),
            cancellationToken).ConfigureAwait(false);

        return response is null
            ? null
            : HttpRiskManagementHeldSymbolsProvider.Interpret([.. response.Positions.Select(ToRow)], logger);
    }

    internal static HttpRiskManagementHeldSymbolsProvider.OpenPositionRow? ToRow(Proto.OpenPositionRow p) =>
        new HttpRiskManagementHeldSymbolsProvider.OpenPositionRow(
            p.HasSymbol ? p.Symbol : null,
            RiskManagementWire.Market(p.Market),
            RiskManagementWire.Side(p.Side),
            p.HasQuantity ? p.Quantity : null);
}

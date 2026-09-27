using AiStockTrading.Shared.Contracts.Trading;
using AiStockTrading.TestSupport.PlatformShim.Foundation.Extensions;
using Grpc.Core;
using MarketMonitorService.Common.Abstractions;
using MarketMonitorService.Domain;
using MarketMonitorService.Features.MarketMonitor.GetWatchlistAsOf;
using Microsoft.AspNetCore.Authorization;
using Proto = AiStockTrading.Shared.Grpc.MarketMonitor.V1;

namespace MarketMonitorService.Features.MarketMonitor;

// NFR, FR-02, FR-04, FR-13, FR-15, MSP:ADR-0029, MSP:ADR-0075, IADR-0284 決定 5（段 4）, IADR-0328, IADR-0331,
// IADR-0442, IADR-0446 決定 2, #1061 (#753):
// 監視銘柄の読み取りの gRPC 面。REST の read サブグループの `GET /monitor/watchlist`（MonitorWatchlistService.GetWatchlist）と
// `GET /monitor/watchlist/as-of`（WatchlistAsOfReconstructor.Reconstruct）と**同じ**サービス・純関数を呼ぶ —— 評価器を 2 つにしない。
//
// 認可: REST の read サブグループと**同じ** `OwnerOrService`（IADR-0051・IADR-0095）。s2s トークンが無ければ `UNAUTHENTICATED`、
// ロールが無ければ `PERMISSION_DENIED`。🔴 履歴の照会（OwnerOnly）・設定の書き込みは gRPC に出さない。
// as-of の時刻の欠落・オフセットの欠落・書式違い（REST の 400）は `INVALID_ARGUMENT`（検証は REST と共有）。
//
// 🔴 **並走中の正は REST である**（MSP:ADR-0029 の 2026-08-04 追記）。REST 面は変えていない。
[Authorize(Policy = AiStockTradingAuthPolicies.OwnerOrService)]
public sealed class WatchlistReadGrpcService(
    MonitorWatchlistService watchlist,
    IMonitorSettingsChangeLog changeLog,
    IMonitorSeedRecord seedRecord,
    IClock clock)
    : Proto.WatchlistRead.WatchlistReadBase
{
    public override Task<Proto.GetWatchlistResponse> GetWatchlist(Proto.GetWatchlistRequest request, ServerCallContext context)
    {
        var response = new Proto.GetWatchlistResponse();
        response.Items.AddRange(watchlist.GetWatchlist().Select(WatchlistWireMapping.ToProto));
        return Task.FromResult(response);
    }

    public override Task<Proto.GetWatchlistAsOfResponse> GetWatchlistAsOf(
        Proto.GetWatchlistAsOfRequest request, ServerCallContext context)
    {
        if (!GetWatchlistAsOfEndpoint.TryParseAt(request.At, out var instant))
            throw new RpcException(new Status(StatusCode.InvalidArgument, GetWatchlistAsOfEndpoint.InvalidAtError));

        return Task.FromResult(WatchlistWireMapping.ToProto(
            WatchlistAsOfReconstructor.Reconstruct(instant, clock.UtcNow, changeLog.GetHistory(), seedRecord.Read())));
    }
}

// NFR, IADR-0446 決定 3: 送り手の型 → 線上表現（提供側の写し）。
// 🔴 C# の null は設定しない。市場は**名前で**写す（C# の 0＝日本は線上で 1）。運ぶのは呼び出し元が読む項目だけ。
public static class WatchlistWireMapping
{
    public static Proto.WatchlistItem ToProto(MonitoredSymbol symbol)
    {
        ArgumentNullException.ThrowIfNull(symbol);

        var item = new Proto.WatchlistItem { Market = ToProto(symbol.Market) };
        if (symbol.Symbol is not null)
            item.Symbol = symbol.Symbol;
        return item;
    }

    public static Proto.GetWatchlistAsOfResponse ToProto(WatchlistAsOfResponse asOf)
    {
        ArgumentNullException.ThrowIfNull(asOf);

        var response = new Proto.GetWatchlistAsOfResponse { Reconstructed = asOf.Reconstructed };
        if (asOf.Symbols is not null)
        {
            response.Symbols = new Proto.WatchlistItems();
            response.Symbols.Items.AddRange(asOf.Symbols.Select(ToProto));
        }

        if (asOf.Reason is not null)
            response.Reason = asOf.Reason;
        return response;
    }

    public static Proto.Market ToProto(Market market) => market switch
    {
        Market.Japan => Proto.Market.Japan,
        Market.UnitedStates => Proto.Market.UnitedStates,
        _ => Proto.Market.Unspecified,
    };
}

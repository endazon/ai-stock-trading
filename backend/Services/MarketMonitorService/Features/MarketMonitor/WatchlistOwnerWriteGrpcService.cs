using System.Text.Json;
using AiStockTrading.Shared.Contracts.Trading;
using AiStockTrading.TestSupport.PlatformShim.Foundation.Extensions;
using Grpc.Core;
using MarketMonitorService.Features.MarketMonitor.ApplyWatchlistProposal;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http.HttpResults;
using Proto = AiStockTrading.Shared.Grpc.MarketMonitor.V1;

namespace MarketMonitorService.Features.MarketMonitor;

// NFR, NFR-06, FR-13, FR-14, MSP:ADR-0029, MSP:ADR-0075, ADR-0047 決定 1〜3, IADR-0284 決定 5（段 5）, IADR-0450, #753:
// 監視銘柄の**所有者限定の書き込み**の gRPC 面（呼び出し元は Discord ボットだけ）。REST の `POST /monitor/watchlist/proposal-apply` と
// **同じ処理関数**（ApplyWatchlistProposalEndpoint.HandleAsync）を呼ぶ —— 代理の解決・案の形の検証・楽観排他・巡回間隔の検査を 2 箇所に書かない。
//
// 🔴 門は **`GrpcOwnerOnly`**。REST の OwnerOnly と同じく s2s には開かない。
// 🔴 **並走中の正は REST である**（MSP:ADR-0029 の 2026-08-04 追記）。REST 面の応答は変えていない。
[Authorize(Policy = AiStockTradingAuthPolicies.GrpcOwnerOnly)]
public sealed class WatchlistOwnerWriteGrpcService(
    MonitorWatchlistService watchlist,
    DelegatedActorOptions delegated,
    WatchlistVolumeEstimator estimator,
    WatchlistCycleFitGuard guard,
    ILoggerFactory loggerFactory)
    : Proto.WatchlistOwnerWrite.WatchlistOwnerWriteBase
{
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    public override async Task<Proto.WatchlistProposalApplicationResponse> ApplyWatchlistProposal(
        Proto.WatchlistProposalApplicationRequest request, ServerCallContext context)
    {
        // 🔴 入れ物の欠落＝ REST の null（処理関数の検証が 400 にする）。市場は**名前で**写し、未指定は null（REST の市場の省略＝ 400）。
        var body = new WatchlistProposalApplyRequest(
            request.ExpectedWatchlist is { } expected
                ? [.. expected.Items.Select(i => new WatchlistSymbolRef(i.HasSymbol ? i.Symbol : null, ToMarket(i.Market)))]
                : null,
            request.Changes is { } changes
                ? [.. changes.Items.Select(c => new WatchlistProposalChangeRequest(
                    c.HasAction ? c.Action : null, c.HasSymbol ? c.Symbol : null, c.HasReason ? c.Reason : null))]
                : null,
            request.ProposalRef,
            request.HasOnBehalfOf ? request.OnBehalfOf : null);

        IResult result;
        try
        {
            result = await ApplyWatchlistProposalEndpoint.HandleAsync(
                body, watchlist, delegated, estimator, guard, loggerFactory, context.GetHttpContext()).ConfigureAwait(false);
        }
        catch (Exception e) when (MonitorSettingsEndpoints.MapException(e) is { } mapped)
        {
            result = mapped;
        }

        var status = (result as IStatusCodeHttpResult)?.StatusCode ?? StatusCodes.Status200OK;
        var value = (result as IValueHttpResult)?.Value;
        if (status == StatusCodes.Status200OK && value is WatchlistProposalApplyResponse applied)
            return ToProto(applied);

        // REST と同じ分類: 400 → INVALID_ARGUMENT、409（案の作成後に変わった・保存の競合）→ ABORTED。**いずれも 1 件も適用していない。**
        var code = status switch
        {
            StatusCodes.Status400BadRequest => StatusCode.InvalidArgument,
            StatusCodes.Status404NotFound => StatusCode.NotFound,
            StatusCodes.Status409Conflict => StatusCode.Aborted,
            StatusCodes.Status422UnprocessableEntity => StatusCode.FailedPrecondition,
            _ => StatusCode.Internal,
        };
        throw new RpcException(new Status(code, ErrorOf(value) ?? string.Empty));
    }

    internal static Market? ToMarket(Proto.Market value) => value switch
    {
        Proto.Market.Japan => Market.Japan,
        Proto.Market.UnitedStates => Market.UnitedStates,
        _ => null,
    };

    // REST の本文の `error`（利用者向けの文言）。
    private static string? ErrorOf(object? value)
    {
        if (value is null)
            return null;

        var json = JsonSerializer.SerializeToElement(value, value.GetType(), Web);
        return json.ValueKind == JsonValueKind.Object
            && json.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.String
                ? error.GetString()
                : null;
    }

    // NFR, IADR-0450 決定 3: 適用の内訳 → 線上表現。C# の null は設定しない。
    public static Proto.WatchlistProposalApplicationResponse ToProto(WatchlistProposalApplyResponse applied)
    {
        ArgumentNullException.ThrowIfNull(applied);

        var response = new Proto.WatchlistProposalApplicationResponse();
        response.Items.AddRange(applied.Items.Select(i =>
        {
            var row = new Proto.WatchlistApplicationItem { Applied = i.Applied };
            if (i.Action is not null) row.Action = i.Action;
            if (i.Symbol is not null) row.Symbol = i.Symbol;
            if (i.SkipReason is not null) row.SkipReason = i.SkipReason;
            return row;
        }));
        if (applied.Actor is not null)
            response.Actor = applied.Actor;
        if (applied.Estimate is { } e)
        {
            response.Estimate = new Proto.FinnhubRequestEstimate { EstimatedDailyRequests = e.EstimatedDailyRequests, Exceeds = e.Exceeds };
            if (e.ProvisionalDailyLimit is { } limit)
                response.Estimate.ProvisionalDailyLimit = limit;
        }
        return response;
    }
}

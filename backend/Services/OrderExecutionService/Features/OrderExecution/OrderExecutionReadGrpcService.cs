using System.Globalization;
using AiStockTrading.Shared.Contracts.Trading;
using AiStockTrading.TestSupport.PlatformShim.Foundation.Extensions;
using Grpc.Core;
using Microsoft.AspNetCore.Authorization;
using OrderExecutionService.Features.OrderExecution.QueryDailyBars;
using OrderExecutionService.Features.OrderExecution.QueryInFlightCloses;
using OrderExecutionService.Features.OrderExecution.QueryShortPermit;
using Proto = AiStockTrading.Shared.Grpc.OrderExecution.V1;

namespace OrderExecutionService.Features.OrderExecution;

// NFR, FR-04, FR-10, FR-15, UC-06, MSP:ADR-0029, MSP:ADR-0075, IADR-0284 決定 5（段 5 の追補 2）, IADR-0328, IADR-0331,
// IADR-0427 決定 2, IADR-0559 決定 1・2, #1391 (#753):
// 発注執行の**読み取り**の gRPC 面。REST の照会の口（`ShortPermitEndpoint`・`DailyBarsEndpoint`・`InFlightClosesEndpoint`）と**同じ**照会サービス・
// **同じ入力の検証**（各 Endpoint の `IsValidQuery`）を呼ぶ —— 照会の規則（ブローカーの枠・自制の予算・キャッシュ）を 2 つにしない。
//
// 入力の検証は REST と同じ向きに揃える: 欠落・値域外（REST の 400）→ `INVALID_ARGUMENT`。市場・方向は名前で写し、未指定・未知は欠落と同じ。
// 日付は `yyyy-MM-dd`（REST の `DateOnly?` の束縛と同じ書式）で、読めなければ欠落と同じ。
//
// 🔴 **並走中の正は REST である**（MSP:ADR-0029 の 2026-08-04 追記）。REST 面は 1 バイトも変えていない（検証を関数へ切り出しただけ）。
// 🔴 NFR-06, ADR-0047 決定 3, IADR-0448, #1067: 門は **`GrpcOwnerOrService`**（deny-by-default。REST の `OwnerOrService` ではない）。
// s2s（trading-service）は同じ、所有者（trading-owner）はトークンの `azp` が Discord ボットの機密クライアントであるときだけ通す。
// 資格情報が無ければ `UNAUTHENTICATED`、ロールが無ければ `PERMISSION_DENIED`（ASP.NET Core の gRPC は認可失敗をこの 2 つへ写す）。
[Authorize(Policy = AiStockTradingAuthPolicies.GrpcOwnerOrService)]
public sealed class OrderExecutionReadGrpcService(
    ShortPermitQueryService shortPermits,
    DailyBarsQueryService dailyBars,
    InFlightClosesQueryService inFlightCloses)
    : Proto.OrderExecutionRead.OrderExecutionReadBase
{
    public override async Task<Proto.GetShortPermitResponse> GetShortPermit(
        Proto.GetShortPermitRequest request, ServerCallContext context)
    {
        var symbol = request.HasSymbol ? request.Symbol : null;
        var market = OrderExecutionReadWireMapping.Market(request.Market);
        if (!ShortPermitEndpoint.IsValidQuery(symbol, (int?)market))
            throw new RpcException(new Status(StatusCode.InvalidArgument, "symbol（銘柄コード）と market（市場）が必要です。"));

        var view = await shortPermits.QueryAsync(symbol!, market!.Value, context.CancellationToken).ConfigureAwait(false);
        return OrderExecutionReadWireMapping.ToProto(view);
    }

    public override async Task<Proto.GetDailyBarsResponse> GetDailyBars(
        Proto.GetDailyBarsRequest request, ServerCallContext context)
    {
        var symbol = request.HasSymbol ? request.Symbol : null;
        var market = OrderExecutionReadWireMapping.Market(request.Market);
        var from = request.HasFrom ? ParseDay(request.From) : null;
        var to = request.HasTo ? ParseDay(request.To) : null;
        if (!DailyBarsEndpoint.IsValidQuery(symbol, (int?)market, from, to))
        {
            throw new RpcException(new Status(
                StatusCode.InvalidArgument,
                $"symbol（{DailyBarsEndpoint.MaxSymbolLength} 文字まで）・market（市場）・from / to（yyyy-MM-dd。from ≦ to・{DailyBarsEndpoint.MaxRangeDays} 日まで）が必要です。"));
        }

        var view = await dailyBars
            .QueryAsync(symbol!, market!.Value, from!.Value, to!.Value, context.CancellationToken)
            .ConfigureAwait(false);
        return OrderExecutionReadWireMapping.ToProto(view);
    }

    public override async Task<Proto.GetInFlightClosesResponse> GetInFlightCloses(
        Proto.GetInFlightClosesRequest request, ServerCallContext context)
    {
        var symbol = request.HasSymbol ? request.Symbol : null;
        var market = OrderExecutionReadWireMapping.Market(request.Market);
        var side = OrderExecutionReadWireMapping.Side(request.CloseSide);
        if (!InFlightClosesEndpoint.IsValidQuery(symbol, (int?)market, (int?)side))
            throw new RpcException(new Status(StatusCode.InvalidArgument, "symbol（銘柄コード）・market（市場）・close_side（決済の方向）が必要です。"));

        var view = await inFlightCloses
            .QueryAsync(symbol!, market!.Value, side!.Value, context.CancellationToken)
            .ConfigureAwait(false);
        return OrderExecutionReadWireMapping.ToProto(view);
    }

    // REST の `DateOnly?` の束縛（yyyy-MM-dd）と同じ受け方。読めなければ欠落（＝INVALID_ARGUMENT）。
    private static DateOnly? ParseDay(string value) =>
        DateOnly.TryParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var day) ? day : null;
}

// NFR, IADR-0427 決定 3, IADR-0559 決定 2: 送り手の型 → 線上表現（送り手側の写し）。受け手の契約テストも本型で線上へ出す。
// 🔴 **原則 A**: 状態は名前で写す（C# の 0＝Unknown / Unavailable を線上の 0＝UNSPECIFIED と取り違えない）。
public static class OrderExecutionReadWireMapping
{
    public static Proto.GetShortPermitResponse ToProto(ShortPermitView view)
    {
        ArgumentNullException.ThrowIfNull(view);
        var response = new Proto.GetShortPermitResponse
        {
            Symbol = view.Symbol,
            Market = ToProto(view.Market),
            Status = view.Status switch
            {
                ShortPermitStatus.Unknown => Proto.ShortPermitState.Unknown,
                ShortPermitStatus.Permitted => Proto.ShortPermitState.Permitted,
                ShortPermitStatus.NotPermitted => Proto.ShortPermitState.NotPermitted,
                _ => Proto.ShortPermitState.Unspecified,
            },
        };
        if (view.UnknownReason is { } reason)
            response.UnknownReason = reason;
        return response;
    }

    public static Proto.GetDailyBarsResponse ToProto(DailyBarsView view)
    {
        ArgumentNullException.ThrowIfNull(view);
        var response = new Proto.GetDailyBarsResponse
        {
            Symbol = view.Symbol,
            Market = ToProto(view.Market),
            Status = view.Status switch
            {
                DailyBarsStatus.Unavailable => Proto.DailyBarsState.Unavailable,
                DailyBarsStatus.Available => Proto.DailyBarsState.Available,
                _ => Proto.DailyBarsState.Unspecified,
            },
        };
        if (view.UnavailableReason is { } reason)
            response.UnavailableReason = reason;
        response.Bars.AddRange(view.Bars.Select(ToProto));
        return response;
    }

    public static Proto.DailyBarRow ToProto(DailyBarView bar)
    {
        ArgumentNullException.ThrowIfNull(bar);
        return new Proto.DailyBarRow
        {
            Date = bar.Date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            Open = ToWire(bar.Open),
            High = ToWire(bar.High),
            Low = ToWire(bar.Low),
            Close = ToWire(bar.Close),
            Volume = bar.Volume,
        };
    }

    public static Proto.GetInFlightClosesResponse ToProto(InFlightClosesView view)
    {
        ArgumentNullException.ThrowIfNull(view);
        var response = new Proto.GetInFlightClosesResponse
        {
            Symbol = view.Symbol,
            Market = ToProto(view.Market),
            CloseSide = view.CloseSide switch
            {
                TradeSide.Buy => Proto.TradeSide.Buy,
                TradeSide.Sell => Proto.TradeSide.Sell,
                _ => Proto.TradeSide.Unspecified,
            },
            Status = view.Status switch
            {
                InFlightClosesStatus.Unknown => Proto.InFlightClosesState.Unknown,
                InFlightClosesStatus.Covered => Proto.InFlightClosesState.Covered,
                InFlightClosesStatus.NotCovered => Proto.InFlightClosesState.NotCovered,
                _ => Proto.InFlightClosesState.Unspecified,
            },
            InFlightQuantity = view.InFlightQuantity,
        };
        if (view.UnknownReason is { } reason)
            response.UnknownReason = reason;
        response.OrderIds.AddRange(view.OrderIds);
        return response;
    }

    // decimal → 線上。不変文化を明示する（既定文化だと小数点が `,` になる環境で壊れる。IADR-0331 決定 2）。
    public static string ToWire(decimal value) => value.ToString(CultureInfo.InvariantCulture);

    internal static Proto.Market ToProto(Market market) => market switch
    {
        AiStockTrading.Shared.Contracts.Trading.Market.Japan => Proto.Market.Japan,
        AiStockTrading.Shared.Contracts.Trading.Market.UnitedStates => Proto.Market.UnitedStates,
        _ => Proto.Market.Unspecified,
    };

    // 線上 → 送り手の型（要求の写し）。未指定・未知は null（＝欠落）。
    internal static Market? Market(Proto.Market value) => value switch
    {
        Proto.Market.Japan => AiStockTrading.Shared.Contracts.Trading.Market.Japan,
        Proto.Market.UnitedStates => AiStockTrading.Shared.Contracts.Trading.Market.UnitedStates,
        _ => null,
    };

    internal static TradeSide? Side(Proto.TradeSide value) => value switch
    {
        Proto.TradeSide.Buy => TradeSide.Buy,
        Proto.TradeSide.Sell => TradeSide.Sell,
        _ => null,
    };
}

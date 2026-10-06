using System.Globalization;
using AiStockTrading.Shared.Contracts.Trading;
using AiStockTrading.TestSupport.PlatformShim.Foundation.Extensions;
using Grpc.Core;
using Microsoft.AspNetCore.Authorization;
using RiskManagementService.Domain;
using RiskManagementService.Features.RiskManagement.GetDriftAdoptions;
using RiskManagementService.Features.RiskManagement.GetEntryBlockers;
using RiskManagementService.Features.RiskManagement.GetFills;
using RiskManagementService.Features.RiskManagement.GetOpenPositions;
using RiskManagementService.Features.RiskManagement.GetOpeningInventory;
using RiskManagementService.Features.RiskManagement.GetSizingContext;
using RiskManagementService.Features.RiskManagement.GetWorkingEntryOrders;
using Proto = AiStockTrading.Shared.Grpc.RiskManagement.V1;

namespace RiskManagementService.Features.RiskManagement;

// NFR, FR-10, FR-03, FR-04, FR-06, FR-20, FR-21, MSP:ADR-0029, MSP:ADR-0075, IADR-0284 決定 5（段 2）, IADR-0328,
// IADR-0331, IADR-0427 決定 2, #997 (#753):
// リスク管理の**読み取り**の gRPC 面。REST の読み取り群（`RiskControlEndpoints` の `read` 群）と**同じ**サービス・純関数を
// 呼ぶ —— 評価器を 2 つにしない（段 1 の `AssumptionsGrpcService` と同じ作法）。
//
// 認可: REST の読み取り群と同じ `OwnerOrService` に、所有者の分岐だけ呼び出し元のクライアント（`azp`）の確認を足した `GrpcOwnerOrService`（#1067。下の属性）（IADR-0051）。s2s トークンが無ければ `UNAUTHENTICATED`、
// ロールが無ければ `PERMISSION_DENIED`（ASP.NET Core の gRPC は認可失敗をこの 2 つへ写す）。
//
// 入力の検証は REST と同じ向きに揃える:
//   - `from`・`to` の欠落・不正（REST の 400）→ `INVALID_ARGUMENT`。
//   - 逆順は REST と同じ扱い —— fills・drift-adoptions は空（報告書生成を止めない）、
//     buy-in-inferences・session-uptime は `INVALID_ARGUMENT`（空を返すと「推定 0 件」「稼働率 0%」と読まれ得る）。
//   - 処理中の `ArgumentException`（REST では群のフィルタが 400 へ写す）→ `INVALID_ARGUMENT`（IADR-0427 決定 2・監査の指摘）。
//     素通しすると gRPC は `UNKNOWN` を返し、報告書の観測は HTTP 相当 500 ＝**一過性**と記録する（REST の 400 は恒常）。
//     「待てば直る」と誤って見送り続ける向きを作らないため、REST と同じ分類へ揃える。
//     `DbUpdateConcurrencyException`（REST の 409）は写さない —— 読み取りは書き込まないので起きない。
//
// 🔴 **並走中の正は REST である**（MSP:ADR-0029 の 2026-08-04 追記）。REST 面は 1 バイトも変えていない。
// 🔴 NFR-06, ADR-0047 決定 3, IADR-0448, #1067: 門は **`GrpcOwnerOrService`**（REST の `OwnerOrService` ではない）。
// s2s（trading-service）は同じ、所有者（trading-owner）はトークンの `azp` が Discord ボットの機密クライアントであるときだけ通す
// ＝人の利用者のトークンは gRPC 面を通らない（REST の面の判定は変えていない）。
[Authorize(Policy = AiStockTradingAuthPolicies.GrpcOwnerOrService)]
public sealed class RiskControlsReadGrpcService(
    OpenPositionsService openPositions,
    WorkingEntryOrdersService workingEntryOrders,
    SizingContextService sizingContext,
    EntryBlockersService entryBlockers,
    StageGateService stageGate,
    IPortfolioLedgerStore ledger,
    IBuyInInferenceStore inferences,
    IPositionObservationArrivalStore arrivals,
    IBusinessCalendar calendar,
    IStage1TradingDayObservationStore uptimeObservations)
    : Proto.RiskControlsRead.RiskControlsReadBase
{
    public override Task<Proto.GetOpenPositionsResponse> GetOpenPositions(
        Proto.GetOpenPositionsRequest request, ServerCallContext context) =>
        Reply(() =>
        {
            var response = new Proto.GetOpenPositionsResponse();
            response.Positions.AddRange(openPositions.Build().Select(RiskReadWireMapping.ToProto));
            return response;
        });

    public override Task<Proto.GetWorkingEntryOrdersResponse> GetWorkingEntryOrders(
        Proto.GetWorkingEntryOrdersRequest request, ServerCallContext context) =>
        Reply(() =>
        {
            var response = new Proto.GetWorkingEntryOrdersResponse();
            response.Orders.AddRange(workingEntryOrders.Build().Select(RiskReadWireMapping.ToProto));
            return response;
        });

    public override Task<Proto.GetSizingContextResponse> GetSizingContext(
        Proto.GetSizingContextRequest request, ServerCallContext context) =>
        Reply(() => RiskReadWireMapping.ToProto(sizingContext.Build()));

    // FR-10, FR-04, #1113, IADR-0463 決定 3: 銘柄単位の新規建ての可否（REST と同じサービス）。
    // symbol・market の欠落・未指定は INVALID_ARGUMENT（REST の 400）。
    public override Task<Proto.GetEntryBlockersResponse> GetEntryBlockers(
        Proto.GetEntryBlockersRequest request, ServerCallContext context) =>
        Reply(() =>
        {
            var market = request.Market switch
            {
                Proto.Market.Japan => Market.Japan,
                Proto.Market.UnitedStates => Market.UnitedStates,
                _ => (Market?)null,
            };
            if (!request.HasSymbol || string.IsNullOrWhiteSpace(request.Symbol) || market is not { } m)
                throw new RpcException(new Status(StatusCode.InvalidArgument, "symbol・market は必須です。"));

            return RiskReadWireMapping.ToProto(entryBlockers.Build(request.Symbol, m));
        });

    public override Task<Proto.GetStageGateResponse> GetStageGate(
        Proto.GetStageGateRequest request, ServerCallContext context) =>
        // 段 5（IADR-0449 決定 3）: 現段階に加え、Discord ボットの `/stage status` が読む項目を同じ現況から写す（REST と同じ 1 回の GetStatus）。
        Reply(() => RiskReadWireMapping.ToProto(stageGate.GetStatus()));

    public override Task<Proto.GetFillsResponse> GetFills(Proto.GetFillsRequest request, ServerCallContext context) =>
        Reply(() =>
        {
            var (from, to) = RequirePeriod(request.From, request.To, rejectReversed: false);
            var response = new Proto.GetFillsResponse();
            response.Fills.AddRange(
                PeriodFillQuery.InTradingDayRange(ledger.GetFills(), from, to).Select(RiskReadWireMapping.ToProto));
            return response;
        });

    public override Task<Proto.GetDriftAdoptionsResponse> GetDriftAdoptions(
        Proto.GetDriftAdoptionsRequest request, ServerCallContext context) =>
        Reply(() =>
        {
            var (from, to) = RequirePeriod(request.From, request.To, rejectReversed: false);
            var response = new Proto.GetDriftAdoptionsResponse();
            response.Adoptions.AddRange(
                PeriodDriftAdoptionQuery.InTradingDayRange(ledger.GetDriftAdoptions(), from, to)
                    .Select(RiskReadWireMapping.ToProto));
            return response;
        });

    // REST と同じく、観測の到達（期間が覆われているか）と推定行を**同じ応答**で返す（呼び出し側が推定行だけを見て
    // 0 件と判断する経路を作らない。GetBuyInInferencesEndpoint の注記と同じ理由）。
    public override Task<Proto.GetBuyInInferencesResponse> GetBuyInInferences(
        Proto.GetBuyInInferencesRequest request, ServerCallContext context) =>
        Reply(() =>
        {
            var (from, to) = RequirePeriod(request.From, request.To, rejectReversed: true);
            var observedDays = arrivals.GetObservedDaysBetween(from, to);

            var response = new Proto.GetBuyInInferencesResponse
            {
                PeriodCovered = ObservationCoverage.Covers(observedDays, from, to, calendar),
            };
            response.ObservedTradingDays.AddRange(observedDays.Select(RiskReadWireMapping.ToWire));
            response.Inferences.AddRange(inferences.GetInferredBetween(from, to).Select(RiskReadWireMapping.ToProto));
            return response;
        });

    public override Task<Proto.GetSessionUptimeResponse> GetSessionUptime(
        Proto.GetSessionUptimeRequest request, ServerCallContext context) =>
        Reply(() =>
        {
            var (from, to) = RequirePeriod(request.From, request.To, rejectReversed: true);
            var days = new Proto.SessionUptimeDays();
            days.Items.AddRange(
                OpenDUptimeReporting.Days(uptimeObservations.GetSessionUptimesBetween(from, to))
                    .Select(RiskReadWireMapping.ToProto));

            return new Proto.GetSessionUptimeResponse
            {
                Days = days,
                Stage1CumulativeCountedDays = uptimeObservations.GetQualifiedTradingDayCount(),
            };
        });

    // FR-06, FR-16, #1181, IADR-0493 決定 2: 期間開始時点の在庫（REST と同じ純関数）。market・before の欠落・未指定は
    // INVALID_ARGUMENT（REST の 400）。
    public override Task<Proto.GetOpeningInventoryResponse> GetOpeningInventory(
        Proto.GetOpeningInventoryRequest request, ServerCallContext context) =>
        Reply(() =>
        {
            var market = request.Market switch
            {
                Proto.Market.Japan => Market.Japan,
                Proto.Market.UnitedStates => Market.UnitedStates,
                _ => (Market?)null,
            };
            if (market is not { } m || !TryParseDay(request.Before, out var before))
                throw new RpcException(new Status(StatusCode.InvalidArgument, "market・before（yyyy-MM-dd）は必須です。"));

            var response = new Proto.GetOpeningInventoryResponse();
            response.Lots.AddRange(
                OpeningInventoryQuery.AsOf(ledger.GetFills(), m, before).Select(RiskReadWireMapping.ToProto));
            return response;
        });

    // REST の群のフィルタ（RiskControlEndpoints）と同じ分類: ArgumentException は 400 ＝ INVALID_ARGUMENT。
    // 既に RpcException のもの（RequirePeriod の INVALID_ARGUMENT）はそのまま通す。
    private static Task<T> Reply<T>(Func<T> handler)
    {
        try
        {
            return Task.FromResult(handler());
        }
        catch (ArgumentException e)
        {
            throw new RpcException(new Status(StatusCode.InvalidArgument, e.Message));
        }
    }

    // REST の `DateOnly? from, DateOnly? to` の束縛（yyyy-MM-dd）と同じ受け方。欠落・不正は 400 相当。
    private static (DateOnly From, DateOnly To) RequirePeriod(string from, string to, bool rejectReversed)
    {
        if (!TryParseDay(from, out var fromDay) || !TryParseDay(to, out var toDay))
            throw new RpcException(new Status(StatusCode.InvalidArgument, "from・to（yyyy-MM-dd）は必須です。"));

        if (rejectReversed && fromDay > toDay)
            throw new RpcException(new Status(StatusCode.InvalidArgument, "from は to 以前の日付を指定してください。"));

        return (fromDay, toDay);
    }

    private static bool TryParseDay(string value, out DateOnly day) =>
        DateOnly.TryParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out day);
}

using System.Text.Json;
using AiStockTrading.Shared.Contracts.Trading;
using AiStockTrading.Shared.Kernel.Trading;
using AiStockTrading.TestSupport.PlatformShim.Foundation.Extensions;
using Grpc.Core;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http.HttpResults;
using RiskManagementService.Domain;
using RiskManagementService.Features.RiskManagement.AdoptPositionDrift;
using RiskManagementService.Features.RiskManagement.ClearGoodFaithViolations;
using RiskManagementService.Features.RiskManagement.DisengageKillSwitch;
using RiskManagementService.Features.RiskManagement.EngageKillSwitch;
using RiskManagementService.Features.RiskManagement.EvaluateWithdrawal;
using RiskManagementService.Features.RiskManagement.PauseTrading;
using RiskManagementService.Features.RiskManagement.RequestStageTransition;
using RiskManagementService.Features.RiskManagement.ResumeTrading;
using Wolverine;
using Proto = AiStockTrading.Shared.Grpc.RiskManagement.V1;

namespace RiskManagementService.Features.RiskManagement;

// NFR, NFR-06, FR-10, FR-11, FR-14, FR-19, FR-20, MSP:ADR-0029, MSP:ADR-0075, ADR-0047 決定 1〜3, IADR-0284 決定 5（段 5）, IADR-0450, #753:
// リスク管理の**所有者限定の書き込み**の gRPC 面（呼び出し元は Discord ボットだけ）。各 rpc は REST の端点と**同じ処理関数**
// （`<操作>Endpoint.Handle*`）を呼ぶ —— 操作者の解決（DelegatedActorResolver）・検証・監査の発行を 2 箇所に書かない。
// 処理関数の結果（REST の状態と本文）を gRPC の状態へ写すのは RiskWriteGrpcReplies（REST の群のフィルタの例外の写しも同じものを使う）。
//
// 🔴 門は **`GrpcOwnerOnly`**（trading-owner ∧ azp がボットの機密クライアント）。REST の OwnerOnly と同じく s2s には開かない
// （生成AI・自動処理が統制を解けないようにする＝FR-10・ADR-0003）。人の利用者のトークンも通さない（ADR-0047 決定 3）。
// 🔴 **並走中の正は REST である**（MSP:ADR-0029 の 2026-08-04 追記）。REST 面の応答は変えていない。
[Authorize(Policy = AiStockTradingAuthPolicies.GrpcOwnerOnly)]
public sealed class RiskControlsOwnerWriteGrpcService(
    KillSwitchService killSwitch,
    PauseService pause,
    GoodFaithViolationClearingService goodFaith,
    StageGateService stageGate,
    PositionDriftAdoptionService driftAdoption,
    IMessageBus bus,
    DelegatedActorOptions delegated,
    ILoggerFactory loggerFactory)
    : Proto.RiskControlsOwnerWrite.RiskControlsOwnerWriteBase
{
    public override async Task<Proto.KillSwitchChangeResponse> EngageKillSwitch(
        Proto.KillSwitchChangeRequest request, ServerCallContext context)
    {
        var reply = await RiskWriteGrpcReplies.RunAsync(() =>
            EngageKillSwitchEndpoint.Handle(new KillSwitchRequest(request.Reason), killSwitch, context.GetHttpContext()));
        return new Proto.KillSwitchChangeResponse { Engaged = reply.ValueOrThrow<KillSwitchState>().Engaged };
    }

    public override async Task<Proto.KillSwitchChangeResponse> DisengageKillSwitch(
        Proto.KillSwitchChangeRequest request, ServerCallContext context)
    {
        var reply = await RiskWriteGrpcReplies.RunAsync(() =>
            DisengageKillSwitchEndpoint.Handle(new KillSwitchRequest(request.Reason), killSwitch, context.GetHttpContext()));
        return new Proto.KillSwitchChangeResponse { Engaged = reply.ValueOrThrow<KillSwitchState>().Engaged };
    }

    public override async Task<Proto.TradingPauseChangeResponse> PauseTrading(
        Proto.TradingPauseChangeRequest request, ServerCallContext context)
    {
        var reply = await RiskWriteGrpcReplies.RunAsync(() =>
            PauseTradingEndpoint.Handle(new PauseRequest(request.Reason), pause, context.GetHttpContext()));
        return new Proto.TradingPauseChangeResponse { Paused = reply.ValueOrThrow<PauseState>().Paused };
    }

    public override async Task<Proto.TradingPauseChangeResponse> ResumeTrading(
        Proto.TradingPauseChangeRequest request, ServerCallContext context)
    {
        var reply = await RiskWriteGrpcReplies.RunAsync(() =>
            ResumeTradingEndpoint.Handle(new PauseRequest(request.Reason), pause, context.GetHttpContext()));
        return new Proto.TradingPauseChangeResponse { Paused = reply.ValueOrThrow<PauseState>().Paused };
    }

    public override async Task<Proto.GoodFaithViolationClearanceResponse> ClearGoodFaithViolations(
        Proto.GoodFaithViolationClearanceRequest request, ServerCallContext context)
    {
        var reply = await RiskWriteGrpcReplies.RunAsync(() =>
            ClearGoodFaithViolationsEndpoint.HandleAsync(
                new GoodFaithViolationClearRequest(request.Reason), goodFaith, bus, context.GetHttpContext()));
        var cleared = reply.ValueOrThrow<GoodFaithViolationClearResponse>();

        var response = new Proto.GoodFaithViolationClearanceResponse { RemainingCount = cleared.RemainingCount };
        response.ClearedOrderIds.AddRange(cleared.ClearedOrderIds);
        if (cleared.ClearedAt is { } at)
            response.ClearedAt = RiskReadWireMapping.ToWire(at);
        return response;
    }

    public override async Task<Proto.StageTransitionApprovalResponse> RequestStageTransition(
        Proto.StageTransitionApprovalRequest request, ServerCallContext context)
    {
        var reply = await RiskWriteGrpcReplies.RunAsync(() =>
            RequestStageTransitionEndpoint.HandleAsync(
                new StageTransitionRequest(ToStage(request.TargetStage), OnBehalfOf: request.HasOnBehalfOf ? request.OnBehalfOf : null),
                stageGate, bus, delegated, loggerFactory, context.GetHttpContext()));

        // 🔴 受理不能（REST の 422）は結果の本文（拒否の理由・合格条件）を持つので、失敗ではなく応答（accepted=false）で返す。
        if (reply is not { Status: StatusCodes.Status200OK or StatusCodes.Status422UnprocessableEntity, Value: StageTransitionResult result })
            throw reply.ToRpcException();

        return RiskWriteWireMapping.ToProto(result);
    }

    public override async Task<Proto.WithdrawalEvaluationResponse> EvaluateWithdrawal(
        Proto.WithdrawalEvaluationRequest request, ServerCallContext context)
    {
        var reply = await RiskWriteGrpcReplies.RunAsync(() => EvaluateWithdrawalEndpoint.Handle(stageGate));
        return new Proto.WithdrawalEvaluationResponse
        {
            Assessment = RiskWriteWireMapping.ToProto(reply.ValueOrThrow<WithdrawalAssessment>()),
        };
    }

    public override async Task<Proto.DriftAdoptionCommandResponse> AdoptPositionDrift(
        Proto.DriftAdoptionCommandRequest request, ServerCallContext context)
    {
        var reply = await RiskWriteGrpcReplies.RunAsync(() =>
            AdoptPositionDriftEndpoint.HandleAsync(
                new PositionDriftAdoptionRequest(
                    request.Symbol, ToMarket(request.Market), request.Reason, request.HasOnBehalfOf ? request.OnBehalfOf : null),
                driftAdoption, bus, delegated, loggerFactory, context.GetHttpContext()));
        return RiskWriteWireMapping.ToProto(reply.ValueOrThrow<PositionDriftAdoptionResponse>());
    }

    // 線上の列挙 → C#（**名前で**写す）。未指定・未知は null ＝ REST の項目の省略（処理関数が 400 にする）。
    internal static TradingStage? ToStage(Proto.TradingStage value) => value switch
    {
        Proto.TradingStage.Stage0Verification => TradingStage.Stage0Verification,
        Proto.TradingStage.Stage1Simulate => TradingStage.Stage1Simulate,
        Proto.TradingStage.Stage2MinimalLive => TradingStage.Stage2MinimalLive,
        Proto.TradingStage.Stage3ScaledLive => TradingStage.Stage3ScaledLive,
        _ => null,
    };

    internal static Market? ToMarket(Proto.Market value) => value switch
    {
        Proto.Market.Japan => Market.Japan,
        Proto.Market.UnitedStates => Market.UnitedStates,
        _ => null,
    };
}

// NFR, IADR-0450 決定 2: REST の処理関数の結果（`IResult`）→ gRPC の状態。REST の群のフィルタと同じ例外の写し（RiskControlEndpoints.MapException）を通す。
// 🔴 サービスを跨いで共通化しない（報告書・市場監視はそれぞれの写しを持つ＝群のフィルタの例外の種類が違う。IADR-0264 決定 1 と同じ向き）。
internal static class RiskWriteGrpcReplies
{
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    internal static Task<Reply> RunAsync(Func<IResult> handler) => RunAsync(() => Task.FromResult(handler()));

    internal static async Task<Reply> RunAsync(Func<Task<IResult>> handler)
    {
        IResult result;
        try
        {
            result = await handler().ConfigureAwait(false);
        }
        catch (Exception e) when (RiskControlEndpoints.MapException(e) is { } mapped)
        {
            result = mapped;
        }

        return new Reply(
            (result as IStatusCodeHttpResult)?.StatusCode ?? StatusCodes.Status200OK,
            (result as IValueHttpResult)?.Value);
    }

    // REST の状態 → gRPC の状態（REST と同じ分類を保つ）。
    internal static StatusCode ToGrpcStatus(int httpStatus) => httpStatus switch
    {
        StatusCodes.Status400BadRequest => StatusCode.InvalidArgument,
        StatusCodes.Status401Unauthorized => StatusCode.Unauthenticated,
        StatusCodes.Status403Forbidden => StatusCode.PermissionDenied,
        StatusCodes.Status404NotFound => StatusCode.NotFound,
        StatusCodes.Status409Conflict => StatusCode.Aborted,
        StatusCodes.Status422UnprocessableEntity => StatusCode.FailedPrecondition,
        StatusCodes.Status429TooManyRequests => StatusCode.ResourceExhausted,
        _ => StatusCode.Internal,
    };

    internal readonly record struct Reply(int Status, object? Value)
    {
        /// <summary>200 で期待した型の本文なら本文。それ以外は REST と同じ分類の gRPC の失敗（REST の <c>error</c> を詳細に載せる）。</summary>
        internal T ValueOrThrow<T>() where T : class =>
            Status == StatusCodes.Status200OK && Value is T value ? value : throw ToRpcException();

        internal RpcException ToRpcException() =>
            new(new Status(Status == StatusCodes.Status200OK ? StatusCode.Internal : ToGrpcStatus(Status), ErrorOf(Value) ?? string.Empty));
    }

    // REST の本文の `error`（利用者向けの文言）。匿名型・名前付きの型のどちらも JSON の項目名で読む。
    internal static string? ErrorOf(object? value)
    {
        if (value is null)
            return null;

        var json = JsonSerializer.SerializeToElement(value, value.GetType(), Web);
        return json.ValueKind == JsonValueKind.Object
            && json.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.String
                ? error.GetString()
                : null;
    }
}

// NFR, IADR-0450 決定 3: 書き込みの応答型 → 線上表現（提供側の写し）。C# の null は設定しない。列挙は名前で写す（RiskReadWireMapping を使う）。
public static class RiskWriteWireMapping
{
    public static Proto.StageTransitionApprovalResponse ToProto(StageTransitionResult result)
    {
        ArgumentNullException.ThrowIfNull(result);

        var response = new Proto.StageTransitionApprovalResponse
        {
            Accepted = result.Accepted,
            Stage1Criteria = new Proto.Stage1CriteriaRecord
            {
                TargetTradingDays = result.Stage1Criteria.TargetTradingDays,
                MinimumTradeCount = result.Stage1Criteria.MinimumTradeCount,
                MaximumTradingDays = result.Stage1Criteria.MaximumTradingDays,
                BelowStatisticalBasis = result.Stage1Criteria.BelowStatisticalBasis,
            },
        };
        if (result.Transition is { } transition)
            response.ToStage = RiskReadWireMapping.ToProto(transition.ToStage);
        response.RejectionReasons.AddRange(result.RejectionReasons.Select(RiskReadWireMapping.ToProto));
        return response;
    }

    public static Proto.WithdrawalAssessmentRecord ToProto(WithdrawalAssessment assessment)
    {
        ArgumentNullException.ThrowIfNull(assessment);

        var record = new Proto.WithdrawalAssessmentRecord
        {
            Triggered = assessment.Triggered,
            HaltNewEntries = assessment.HaltNewEntries,
        };
        if (assessment.Reason is { } reason)
            record.Reason = RiskReadWireMapping.ToProto(reason);
        if (assessment.ProposedStage is { } proposed)
            record.ProposedStage = RiskReadWireMapping.ToProto(proposed);
        return record;
    }

    public static Proto.DriftAdoptionCommandResponse ToProto(PositionDriftAdoptionResponse adopted)
    {
        ArgumentNullException.ThrowIfNull(adopted);

        var response = new Proto.DriftAdoptionCommandResponse
        {
            AdoptionId = adopted.AdoptionId.ToString(),
            Market = RiskReadWireMapping.ToProto(adopted.Market),
            LedgerQuantityBefore = adopted.LedgerQuantityBefore,
            LedgerQuantityAfter = adopted.LedgerQuantityAfter,
            BrokerQuantity = adopted.BrokerQuantity,
            ObservedAt = RiskReadWireMapping.ToWire(adopted.ObservedAt),
            RealizedPnlRecorded = adopted.RealizedPnlRecorded,
        };
        if (adopted.Symbol is not null)
            response.Symbol = adopted.Symbol;
        if (adopted.Actor is not null)
            response.Actor = adopted.Actor;
        return response;
    }
}

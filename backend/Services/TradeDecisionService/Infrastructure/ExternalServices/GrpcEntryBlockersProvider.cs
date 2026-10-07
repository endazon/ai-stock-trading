using AiStockTrading.Shared.Contracts.Trading;
using Microsoft.Extensions.Logging;
using TradeDecisionService.Features.TradeDecision;
using Proto = AiStockTrading.Shared.Grpc.RiskManagement.V1;

namespace TradeDecisionService.Infrastructure.ExternalServices;

// FR-10, FR-04, NFR, #1113, IADR-0463 決定 3・4, IADR-0427: 新規建ての可否を gRPC（`RiskControlsRead/GetEntryBlockers`）で照会する
// `IEntryBlockersProvider` の 2 つ目の実装。REST 実装と並走し、選ぶのは Program.cs（`RiskManagement:Grpc` の有無。既定は REST）。
// 🔴 **解釈は REST 実装と同じ 1 つ**（`HttpEntryBlockersProvider.Interpret`）。ここは proto を同じ nullable の行へ写すだけ。
// 🔴 原則 A: 方向の入れ物の欠落・未指定の市場・未指定／未知の理由は null（不明）へ写す。
public sealed class GrpcEntryBlockersProvider(
    RiskManagementGrpcTransport transport,
    ILogger<GrpcEntryBlockersProvider> logger)
    : IEntryBlockersProvider
{
    public async Task<EntryBlockers?> GetAsync(
        string symbol, Market market, CancellationToken cancellationToken = default)
    {
        var request = new Proto.GetEntryBlockersRequest
        {
            Symbol = symbol,
            Market = market switch
            {
                Market.Japan => Proto.Market.Japan,
                Market.UnitedStates => Proto.Market.UnitedStates,
                _ => Proto.Market.Unspecified,
            },
        };
        var response = await transport.CallAsync(
            "新規建ての可否",
            "不明として扱います（LLM を呼びます）。",
            (client, options) => client.GetEntryBlockersAsync(request, options),
            cancellationToken).ConfigureAwait(false);
        if (response is null)
            return null;

        return HttpEntryBlockersProvider.Interpret(ToRow(response), symbol, market, logger);
    }

    // 線上 → REST と同じ nullable の行（欠落・未指定は null）。
    internal static HttpEntryBlockersProvider.EntryBlockersDto ToRow(Proto.GetEntryBlockersResponse r) => new(
        r.HasSymbol ? r.Symbol : null,
        RiskManagementWire.Market(r.Market),
        r.LongSide is null ? null : [.. r.LongSide.Reasons.Select(Reason)],
        r.ShortSide is null ? null : [.. r.ShortSide.Reasons.Select(Reason)]);

    // 名前で写す。未指定・未知の番号は null（不明）。
    internal static RejectionReason? Reason(Proto.EntryBlocker value) => value switch
    {
        Proto.EntryBlocker.KillSwitchActive => RejectionReason.KillSwitchActive,
        Proto.EntryBlocker.TradingPaused => RejectionReason.TradingPaused,
        Proto.EntryBlocker.StoppedOutSameDay => RejectionReason.StoppedOutSameDay,
        Proto.EntryBlocker.DecisionExitSameDay => RejectionReason.DecisionExitSameDay,
        Proto.EntryBlocker.GoodFaithViolationLimitReached => RejectionReason.GoodFaithViolationLimitReached,
        Proto.EntryBlocker.MaxPositionsExceeded => RejectionReason.MaxPositionsExceeded,
        Proto.EntryBlocker.DailyLossLimitReached => RejectionReason.DailyLossLimitReached,
        Proto.EntryBlocker.MaxDrawdownReached => RejectionReason.MaxDrawdownReached,
        _ => null,
    };
}

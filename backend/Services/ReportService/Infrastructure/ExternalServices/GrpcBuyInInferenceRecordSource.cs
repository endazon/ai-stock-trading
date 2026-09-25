using AiStockTrading.Shared.Contracts.Events;
using ReportService.Features.Reports;
using Microsoft.Extensions.Logging;
using Proto = AiStockTrading.Shared.Grpc.RiskManagement.V1;

namespace ReportService.Infrastructure.ExternalServices;

// NFR, FR-10, FR-06, FR-21, MSP:ADR-0029, IADR-0284 決定 5（段 2）, IADR-0181, IADR-0427, #997 (#753):
// 強制買戻しの推定を **gRPC 生成クライアント**（`RiskControlsRead/GetBuyInInferences`）で照会する
// `IBuyInInferenceRecordSource` の 2 つ目の実装。REST 実装（HttpBuyInInferenceRecordSource）と並走する（**既定は REST**）。
//
// 🔴 **倒す向きは REST と同じ null（未供給）**。0 件と描くと「強制買戻しは起きていない」と読める（ADR-0016 決定15）。
// 🔴 **期間の被覆の判定は REST と同じ 1 つ**（`HttpBuyInInferenceRecordSource.Interpret`）。
// 🔴 **原則 A**: `period_covered` の欠落は **null**（`bool?`）として渡し、「覆っている」と読まない（REST の旧版応答と同じ扱い）。
//   禁止期限の欠落は REST と同じ null（推定日で代える）。識別・数量・日付・時刻が欠けた行は既定値で作らず、応答全体を未供給にする。
public sealed class GrpcBuyInInferenceRecordSource(
    RiskManagementGrpcTransport transport, ILogger<GrpcBuyInInferenceRecordSource> logger)
    : IBuyInInferenceRecordSource
{
    public async Task<IReadOnlyList<BuyInInferred>?> GetInferencesAsync(
        DateOnly fromInclusive, DateOnly toInclusive, CancellationToken cancellationToken = default)
    {
        var response = await transport.CallAsync(
            "強制買戻しの推定",
            "**未供給として扱います**（0 件とは表示しません）。",
            (client, options) => client.GetBuyInInferencesAsync(
                new Proto.GetBuyInInferencesRequest
                {
                    From = RiskManagementWire.Wire(fromInclusive),
                    To = RiskManagementWire.Wire(toInclusive),
                },
                options),
            cancellationToken).ConfigureAwait(false);
        if (response is null)
            return null;

        HttpBuyInInferenceRecordSource.BuyInInferenceQueryDto body;
        try
        {
            var inferences = new List<HttpBuyInInferenceRecordSource.BuyInInferenceRecordDto>(response.Inferences.Count);
            foreach (var inference in response.Inferences)
            {
                if (ToRow(inference) is not { } row)
                {
                    logger.LogError(
                        "強制買戻しの推定の gRPC 応答に識別・数量・日付・時刻の欠けた行がありました（{Rows} 行中）。"
                            + "送り手との契約の食い違いとみなし、**未供給として扱います**。",
                        response.Inferences.Count);
                    return null;
                }

                inferences.Add(row);
            }

            body = new HttpBuyInInferenceRecordSource.BuyInInferenceQueryDto(
                response.HasPeriodCovered ? response.PeriodCovered : null,
                [.. response.ObservedTradingDays.Select(RiskManagementWire.Day)],
                inferences);
        }
        catch (FormatException ex)
        {
            logger.LogWarning(ex, "強制買戻しの推定の gRPC 応答を読めません（日付・時刻・GUID の書式）。**未供給として扱います**。");
            return null;
        }

        return HttpBuyInInferenceRecordSource.Interpret(body, fromInclusive, toInclusive, logger);
    }

    internal static HttpBuyInInferenceRecordSource.BuyInInferenceRecordDto? ToRow(Proto.BuyInInferenceRow i)
    {
        if (RiskManagementWire.Id(i.HasId, i.Id) is not { } id
            || !i.HasSymbol || string.IsNullOrWhiteSpace(i.Symbol)
            || RiskManagementWire.Market(i.Market) is not { } market
            || !i.HasLedgerShortQuantity || !i.HasBrokerShortQuantity || !i.HasInFlightCloseQuantity
            || !i.HasUnexplainedQuantity || !i.HasNewlyInferredQuantity
            || RiskManagementWire.Day(i.HasInferredOn, i.InferredOn) is not { } inferredOn
            || RiskManagementWire.Timestamp(i.HasObservedAt, i.ObservedAt) is not { } observedAt
            || RiskManagementWire.Timestamp(i.HasInferredAt, i.InferredAt) is not { } inferredAt)
        {
            return null;
        }

        return new HttpBuyInInferenceRecordSource.BuyInInferenceRecordDto(
            id, i.Symbol, market, i.LedgerShortQuantity, i.BrokerShortQuantity, i.InFlightCloseQuantity,
            i.UnexplainedQuantity, i.NewlyInferredQuantity,
            RiskManagementWire.Day(i.HasBanUntil, i.BanUntil), inferredOn, observedAt, inferredAt);
    }
}

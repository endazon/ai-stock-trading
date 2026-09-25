using ReportService.Domain;
using ReportService.Features.Reports;
using Microsoft.Extensions.Logging;
using Proto = AiStockTrading.Shared.Grpc.RiskManagement.V1;

namespace ReportService.Infrastructure.ExternalServices;

// NFR, FR-06, FR-11, MSP:ADR-0029, IADR-0284 決定 5（段 2）, IADR-0360, IADR-0427, #997 (#753):
// 期間の乖離の取り込みを **gRPC 生成クライアント**（`RiskControlsRead/GetDriftAdoptions`）で照会する
// `IPeriodDriftAdoptionSource` の 2 つ目の実装。REST 実装（HttpPeriodDriftAdoptionSource）と並走する（**既定は REST**）。
//
// 🔴 **倒す向きは REST と同じ null（照会できていない）**。空列（該当なし）へ倒さない（#859 の主訴）。
// 🔴 **写しは REST と同じ解釈**（`HttpPeriodDriftAdoptionSource.Interpret`: 銘柄の無い行を落とす）。
// 🔴 **原則 A**: 操作者・理由の欠落は REST と同じく空文字。識別・数量・時刻が欠けた行は既定値で作らず、
//   応答全体を null（照会できていない）として Error で出す（IADR-0427 決定 3）。
public sealed class GrpcPeriodDriftAdoptionSource(
    RiskManagementGrpcTransport transport, ILogger<GrpcPeriodDriftAdoptionSource> logger)
    : IPeriodDriftAdoptionSource
{
    public async Task<IReadOnlyList<PeriodDriftAdoption>?> GetDriftAdoptionsAsync(
        DateOnly fromInclusive, DateOnly toInclusive, CancellationToken cancellationToken = default)
    {
        var response = await transport.CallAsync(
            "手動売買の取り込み",
            "未供給として報告書を続けます。",
            (client, options) => client.GetDriftAdoptionsAsync(
                new Proto.GetDriftAdoptionsRequest
                {
                    From = RiskManagementWire.Wire(fromInclusive),
                    To = RiskManagementWire.Wire(toInclusive),
                },
                options),
            cancellationToken).ConfigureAwait(false);
        if (response is null)
            return null;

        try
        {
            var rows = new List<HttpPeriodDriftAdoptionSource.DriftAdoptionDto>(response.Adoptions.Count);
            foreach (var adoption in response.Adoptions)
            {
                if (ToRow(adoption) is not { } row)
                {
                    logger.LogError(
                        "手動売買の取り込みの gRPC 応答に識別・数量・時刻の欠けた行がありました（{Rows} 行中）。"
                            + "送り手との契約の食い違いとみなし、未供給として報告書を続けます。",
                        response.Adoptions.Count);
                    return null;
                }

                rows.Add(row);
            }

            return HttpPeriodDriftAdoptionSource.Interpret(rows);
        }
        catch (FormatException ex)
        {
            logger.LogWarning(ex, "手動売買の取り込みの gRPC 応答を読めません（時刻・GUID の書式）。未供給として報告書を続けます。");
            return null;
        }
    }

    internal static HttpPeriodDriftAdoptionSource.DriftAdoptionDto? ToRow(Proto.DriftAdoption a)
    {
        var symbol = a.HasSymbol ? a.Symbol : string.Empty;
        if (string.IsNullOrWhiteSpace(symbol))
            return new HttpPeriodDriftAdoptionSource.DriftAdoptionDto(
                Guid.Empty, symbol, default, default, 0, 0, 0, default, null, null, default);

        if (RiskManagementWire.Id(a.HasAdoptionId, a.AdoptionId) is not { } id
            || RiskManagementWire.Market(a.Market) is not { } market
            || RiskManagementWire.Side(a.Side) is not { } side
            || !a.HasQuantity || !a.HasLedgerQuantityBefore || !a.HasBrokerQuantity
            || RiskManagementWire.Timestamp(a.HasObservedAt, a.ObservedAt) is not { } observedAt
            || RiskManagementWire.Timestamp(a.HasAdoptedAt, a.AdoptedAt) is not { } adoptedAt)
        {
            return null;
        }

        return new HttpPeriodDriftAdoptionSource.DriftAdoptionDto(
            id, symbol, market, side, a.Quantity, a.LedgerQuantityBefore, a.BrokerQuantity, observedAt,
            a.HasActor ? a.Actor : null, a.HasReason ? a.Reason : null, adoptedAt);
    }
}

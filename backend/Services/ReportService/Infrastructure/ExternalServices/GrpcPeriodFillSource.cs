using ReportService.Domain;
using ReportService.Features.Reports;
using Microsoft.Extensions.Logging;
using Proto = AiStockTrading.Shared.Grpc.RiskManagement.V1;

namespace ReportService.Infrastructure.ExternalServices;

// NFR, FR-06, FR-16, MSP:ADR-0029, IADR-0284 決定 5（段 2）, IADR-0427, #997 (#753):
// 期間の約定を **gRPC 生成クライアント**（`RiskControlsRead/GetFills`）で照会する `IPeriodFillSource` の 2 つ目の実装。
// REST 実装（HttpPeriodFillSource）と並走し、選ぶのは Program.cs（`RiskManagement:Grpc`。**既定は REST**）。
//
// 🔴 **倒す向きは REST と同じ空列**（数値 0 の報告書として生成を続ける。IADR-0115 決定5）。
// 🔴 **写しは REST と同じ解釈**（`HttpPeriodFillSource.Interpret`: 銘柄の無い行を落とす・同伴レートで基準通貨へ換算）。
// 🔴 **原則 A**:
//   - 発注先の未指定は **null（不明）**（どちらの段にも算入しない。IADR-0271）。認識時レートの欠落は **null（未記録）**。
//   - 同伴レートの欠落は REST と同じく 1、判断 ID の欠落は REST と同じく空の GUID（相関できない）。
//   - 市場・方向・建て／決済・数量・単価・約定時刻が欠けた行は、REST なら既定値（日本・買い・新規・0）で**作り話の約定**
//     になっていた。gRPC では作らず、**応答全体を読めない**（＝空列）として Error で出す（IADR-0427 決定 3）。
public sealed class GrpcPeriodFillSource(RiskManagementGrpcTransport transport, ILogger<GrpcPeriodFillSource> logger)
    : IPeriodFillSource
{
    public async Task<IReadOnlyList<PeriodTradeFill>> GetFillsAsync(
        DateOnly fromInclusive, DateOnly toInclusive, CancellationToken cancellationToken = default)
    {
        var response = await transport.CallAsync(
            "期間約定",
            "数値 0 の報告書として生成を続けます。",
            (client, options) => client.GetFillsAsync(
                new Proto.GetFillsRequest
                {
                    From = RiskManagementWire.Wire(fromInclusive),
                    To = RiskManagementWire.Wire(toInclusive),
                },
                options),
            cancellationToken).ConfigureAwait(false);
        if (response is null)
            return [];

        try
        {
            var rows = new List<HttpPeriodFillSource.LedgerFillDto>(response.Fills.Count);
            foreach (var fill in response.Fills)
            {
                if (ToRow(fill) is not { } row)
                {
                    logger.LogError(
                        "期間約定の gRPC 応答に市場・方向・建て／決済・数量・単価・約定時刻の欠けた行がありました（{Rows} 行中）。"
                            + "送り手との契約の食い違いとみなし、数値 0 の報告書として生成を続けます（既定値で約定を作りません）。",
                        response.Fills.Count);
                    return [];
                }

                rows.Add(row);
            }

            return HttpPeriodFillSource.Interpret(rows);
        }
        catch (FormatException ex)
        {
            logger.LogWarning(ex, "期間約定の gRPC 応答を読めません（10 進・時刻・GUID の書式）。数値 0 の報告書として生成を続けます。");
            return [];
        }
    }

    // 線上 → REST と同じ行。銘柄の無い行は空の銘柄で返し、解釈（Interpret）が REST と同じく落とす。
    // 銘柄のある行で必須の項目が欠けていれば null（＝応答全体を読めない）。
    internal static HttpPeriodFillSource.LedgerFillDto? ToRow(Proto.PeriodFill f)
    {
        var symbol = f.HasSymbol ? f.Symbol : string.Empty;
        if (string.IsNullOrWhiteSpace(symbol))
            return new HttpPeriodFillSource.LedgerFillDto(symbol, default, default, default, 0, 0m, default);

        if (RiskManagementWire.Market(f.Market) is not { } market
            || RiskManagementWire.Side(f.Side) is not { } side
            || RiskManagementWire.Effect(f.PositionEffect) is not { } effect
            || !f.HasQuantity
            || RiskManagementWire.Decimal(f.HasPrice, f.Price) is not { } price
            || RiskManagementWire.Timestamp(f.HasExecutedAt, f.ExecutedAt) is not { } executedAt)
        {
            return null;
        }

        return new HttpPeriodFillSource.LedgerFillDto(
            symbol, market, side, effect, f.Quantity, price, executedAt,
            FxRateToBase: RiskManagementWire.Decimal(f.HasFxRateToBase, f.FxRateToBase) ?? 1m,
            DecisionId: RiskManagementWire.Id(f.HasDecisionId, f.DecisionId) ?? Guid.Empty,
            Provider: RiskManagementWire.Provider(f.Provider),
            FxRateBaseToDisplay: RiskManagementWire.Decimal(f.HasFxRateBaseToDisplay, f.FxRateBaseToDisplay));
    }
}

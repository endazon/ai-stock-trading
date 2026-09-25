using ReportService.Domain;
using ReportService.Features.Reports;
using Microsoft.Extensions.Logging;
using Proto = AiStockTrading.Shared.Grpc.RiskManagement.V1;

namespace ReportService.Infrastructure.ExternalServices;

// NFR, FR-06, FR-20, MSP:ADR-0029, IADR-0284 決定 5（段 2）, IADR-0271, IADR-0427, #997 (#753):
// 期間の OpenD 稼働率を **gRPC 生成クライアント**（`RiskControlsRead/GetSessionUptime`）で照会する `IOpenDUptimeSource` の
// 2 つ目の実装。REST 実装（HttpOpenDUptimeSource）と並走する（**既定は REST**）。
//
// 🔴 **倒す向きは REST と同じ null（未供給）**。稼働率 0% は「終日停止していた」という別の主張になる。
// 🔴 **原則 A**:
//   - 日次の一覧は存在を持つ入れ物で運ぶ。**入れ物の欠落は未供給**（REST の `days: null` と同じ）、空の入れ物は「行なし」
//     （欠けた日を 0% と描かない）。
//   - 累計の算入日数の欠落は **null（未供給）**のまま運ぶ —— 報告書の型（OpenDUptimeRecord）は「権威源が供給しないなら null
//     （0 と書かない）」と定めている。REST の受け手は非 nullable の int で受けており、欠落が 0 に化ける（IADR-0427 決定 3 の
//     「REST の受け手に残る同型の欠落」。REST 面は本 PR で変えない）。
//   - 日付・稼働率の欠けた日は既定値で作らず、応答全体を未供給にする。
public sealed class GrpcOpenDUptimeSource(RiskManagementGrpcTransport transport, ILogger<GrpcOpenDUptimeSource> logger)
    : IOpenDUptimeSource
{
    public async Task<OpenDUptimeRecord?> GetUptimeAsync(
        DateOnly fromInclusive, DateOnly toInclusive, CancellationToken cancellationToken = default)
    {
        var response = await transport.CallAsync(
            "OpenD 稼働率",
            "**未供給として扱います**（稼働率 0% とは書きません）。",
            (client, options) => client.GetSessionUptimeAsync(
                new Proto.GetSessionUptimeRequest
                {
                    From = RiskManagementWire.Wire(fromInclusive),
                    To = RiskManagementWire.Wire(toInclusive),
                },
                options),
            cancellationToken).ConfigureAwait(false);
        if (response is null)
            return null;

        if (response.Days is not { } wrapper)
        {
            logger.LogWarning("OpenD 稼働率の応答が不正（日次の一覧が無い）でした。**未供給として扱います**。");
            return null;
        }

        try
        {
            var days = new List<OpenDUptimeDay>(wrapper.Items.Count);
            foreach (var item in wrapper.Items)
            {
                if (RiskManagementWire.Day(item.HasSessionDateEasternTime, item.SessionDateEasternTime) is not { } date
                    || RiskManagementWire.Decimal(item.HasUptimeRatio, item.UptimeRatio) is not { } ratio)
                {
                    logger.LogError(
                        "OpenD 稼働率の gRPC 応答に日付・稼働率の欠けた日がありました（{Days} 日中）。"
                            + "送り手との契約の食い違いとみなし、**未供給として扱います**。",
                        wrapper.Items.Count);
                    return null;
                }

                days.Add(new OpenDUptimeDay(date, ratio));
            }

            return new OpenDUptimeRecord(
                days, response.HasStage1CumulativeCountedDays ? response.Stage1CumulativeCountedDays : null);
        }
        catch (FormatException ex)
        {
            logger.LogWarning(ex, "OpenD 稼働率の gRPC 応答を読めません（日付・10 進の書式）。**未供給として扱います**。");
            return null;
        }
    }
}

using System.Globalization;
using Microsoft.Extensions.Logging;
using TradeDecisionService.Features.TradeDecision;
using Proto = AiStockTrading.Shared.Grpc.Report.V1;

namespace TradeDecisionService.Infrastructure.ExternalServices;

// NFR, FR-04, FR-07, MSP:ADR-0029, IADR-0284 決定 5（段 4）, IADR-0028, IADR-0446, #1061 (#753):
// 確定済み日報の方針を **gRPC 生成クライアント**（`DailyPolicyRead/GetConfirmedDailyPolicy`）で照会する `IDailyPolicyProvider` の
// 2 つ目の実装。REST 実装（HttpDailyPolicyProvider）と並走する（**既定は REST**。`Reports:Grpc` を宣言したときだけ選ばれる）。
//
// 🔴 **倒す向きは REST と同じ null（＝取引しない）**: 未確定（`policy` の無い応答。REST の 404）・照会の失敗・契約の食い違い。
// 🔴 **原則 A**: 日付・要約のどちらかが欠けた方針、読めない日付は既定値で作らず null（REST は非 nullable の DTO で受けており、
// 欠けた要約が null のまま方針として通っていた）。
public sealed class GrpcDailyPolicyProvider(ReportsGrpcTransport transport, ILogger<GrpcDailyPolicyProvider> logger)
    : IDailyPolicyProvider
{
    public async Task<DailyPolicy?> GetCurrentAsync(CancellationToken cancellationToken = default)
    {
        var response = await transport.CallAsync(
            "確定済み日報方針",
            "取引しない安全側に倒します。",
            (client, options) => client.GetConfirmedDailyPolicyAsync(new Proto.GetConfirmedDailyPolicyRequest(), options),
            cancellationToken).ConfigureAwait(false);

        // 照会の失敗、または未確定（REST の 404 と同じく警告なしで取引しない）。
        if (response?.Policy is not { } policy)
            return null;

        return ToPolicy(policy) ?? LogBroken();
    }

    internal static DailyPolicy? ToPolicy(Proto.DailyPolicyRecord policy) =>
        policy.HasDate
        && DateOnly.TryParseExact(policy.Date, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)
        && policy.HasSummary
            ? new DailyPolicy(date, policy.Summary)
            : null;

    private DailyPolicy? LogBroken()
    {
        logger.LogError("確定済み日報方針の gRPC 応答に日付・要約の欠けた方針がありました。送り手との契約の食い違いとみなし、取引しない安全側に倒します。");
        return null;
    }
}

using System.Globalization;
using AiStockTrading.TestSupport.PlatformShim.Foundation.Extensions;
using Grpc.Core;
using Microsoft.AspNetCore.Authorization;
using Proto = AiStockTrading.Shared.Grpc.Report.V1;

namespace ReportService.Features.Reports;

// NFR, FR-04, FR-07, MSP:ADR-0029, MSP:ADR-0075, IADR-0284 決定 5（段 4）, IADR-0328, IADR-0331, IADR-0446 決定 2,
// #1061 (#753):
// 確定済み日報の方針の gRPC 面。REST の `GET /reports/daily-policy`（GetConfirmedDailyPolicyEndpoint）と**同じ**サービス
// （ReportAppService.GetConfirmedDailyPolicy）を呼ぶ —— 評価器を 2 つにしない。
//
// 認可: REST の read サブグループと同じ `OwnerOrService` に、所有者の分岐だけ呼び出し元のクライアント（`azp`）の確認を足した `GrpcOwnerOrService`（#1067。下の属性）（IADR-0051）。s2s トークンが無ければ `UNAUTHENTICATED`、
// ロールが無ければ `PERMISSION_DENIED`。
// 未確定（REST の 404）は **`policy` の無い応答**で返す（NOT_FOUND にしない。IADR-0446 決定 3）。
//
// 🔴 **並走中の正は REST である**（MSP:ADR-0029 の 2026-08-04 追記）。REST 面は変えていない。
// 🔴 NFR-06, ADR-0047 決定 3, IADR-0448, #1067: 門は **`GrpcOwnerOrService`**（REST の `OwnerOrService` ではない）。
// s2s（trading-service）は同じ、所有者（trading-owner）はトークンの `azp` が Discord ボットの機密クライアントであるときだけ通す
// ＝人の利用者のトークンは gRPC 面を通らない（REST の面の判定は変えていない）。
[Authorize(Policy = AiStockTradingAuthPolicies.GrpcOwnerOrService)]
public sealed class DailyPolicyReadGrpcService(ReportAppService reports) : Proto.DailyPolicyRead.DailyPolicyReadBase
{
    public override Task<Proto.GetConfirmedDailyPolicyResponse> GetConfirmedDailyPolicy(
        Proto.GetConfirmedDailyPolicyRequest request, ServerCallContext context)
    {
        var policy = reports.GetConfirmedDailyPolicy();
        var response = new Proto.GetConfirmedDailyPolicyResponse();
        if (policy is not null)
            response.Policy = DailyPolicyWireMapping.ToProto(policy);
        return Task.FromResult(response);
    }
}

// NFR, IADR-0446 決定 3: 送り手の型 → 線上表現（提供側の写し）。C# の null は設定しない。運ぶのは取引判断が読む 2 項目だけ。
public static class DailyPolicyWireMapping
{
    public static Proto.DailyPolicyRecord ToProto(ConfirmedDailyPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(policy);

        var record = new Proto.DailyPolicyRecord { Date = policy.Date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) };
        if (policy.Summary is not null)
            record.Summary = policy.Summary;
        return record;
    }
}

using System.Globalization;
using AiStockTrading.TestSupport.PlatformShim.Foundation.Extensions;
using Grpc.Core;
using Microsoft.AspNetCore.Authorization;
using ReportService.Features.Reports.GetConfirmedDailyPolicy;
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

    // FR-15, FR-07, #1308, #1388, IADR-0528 決定 3, IADR-0557 決定 3: ある時点で確定していた方針（REST `GET /reports/daily-policy/as-of`）。
    // 同じサービス（GetConfirmedDailyPolicyAsOf）・同じ入力の検証（TryParseInstant。オフセット必須）。欠落・書式違いは INVALID_ARGUMENT（REST の 400）、
    // 無かったときは `policy` の無い応答（REST の 404。NOT_FOUND にしない —— 「無かった」は受け手の正常な答えであり、撃ち直しの対象ではない）。
    public override Task<Proto.GetConfirmedDailyPolicyAsOfResponse> GetConfirmedDailyPolicyAsOf(
        Proto.GetConfirmedDailyPolicyAsOfRequest request, ServerCallContext context)
    {
        if (!GetConfirmedDailyPolicyAsOfEndpoint.TryParseInstant(request.At, out var instant))
            throw new RpcException(new Status(StatusCode.InvalidArgument, GetConfirmedDailyPolicyAsOfEndpoint.InvalidAtError));

        var policy = reports.GetConfirmedDailyPolicyAsOf(instant);
        var response = new Proto.GetConfirmedDailyPolicyAsOfResponse();
        if (policy is not null)
        {
            response.Policy = DailyPolicyWireMapping.ToProto(policy);
            response.ConfirmedAt = DailyPolicyWireMapping.Instant(policy.ConfirmedAt);
        }

        return Task.FromResult(response);
    }
}

// NFR, IADR-0446 決定 3: 送り手の型 → 線上表現（提供側の写し）。C# の null は設定しない。運ぶのは取引判断が読む項目だけ
// （日付・要約に加え、ADR-0068 決定4, #1373, IADR-0548 決定5 で判断の記録へ残す期間キー・版番号）。
public static class DailyPolicyWireMapping
{
    public static Proto.DailyPolicyRecord ToProto(ConfirmedDailyPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(policy);
        var record = Record(policy.Date, policy.Summary, policy.PeriodKey, policy.Version);
        // FR-07, #1423 M-6, IADR-0583 決定3: 停止の印は立っているときだけ載せる（欠落＝停止していない。古い送り手と同じ読み）。
        if (policy.NoResponseHalted)
            record.NoResponseHalted = true;
        return record;
    }

    // FR-15, #1388, IADR-0557 決定 3: 当時の方針（as-of）。運ぶ項目は確定の方針と同じ（日付・要約・期間キー・版）。確定の時刻は応答の別の項目。
    public static Proto.DailyPolicyRecord ToProto(ConfirmedDailyPolicyAsOf policy)
    {
        ArgumentNullException.ThrowIfNull(policy);
        return Record(policy.Date, policy.Summary, policy.PeriodKey, policy.Version);
    }

    private static Proto.DailyPolicyRecord Record(DateOnly date, string? summary, string? periodKey, int version)
    {
        var record = new Proto.DailyPolicyRecord { Date = date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) };
        if (summary is not null)
            record.Summary = summary;
        if (!string.IsNullOrEmpty(periodKey))
            record.PeriodKey = periodKey;
        record.Version = version;
        return record;
    }

    // 🔴 時刻は往復書式でオフセットごと運ぶ（REST の JSON の DateTimeOffset と同じ。UTC へ丸めて情報を落とさない）。
    public static string Instant(DateTimeOffset value) => value.ToString("O", CultureInfo.InvariantCulture);
}

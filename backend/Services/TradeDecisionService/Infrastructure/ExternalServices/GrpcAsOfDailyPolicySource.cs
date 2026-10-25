using System.Globalization;
using Microsoft.Extensions.Logging;
using TradeDecisionService.Features.TradeDecision;
using TradeDecisionService.Features.TradeDecision.RecordStage0Decisions;
using Proto = AiStockTrading.Shared.Grpc.Report.V1;

namespace TradeDecisionService.Infrastructure.ExternalServices;

// NFR, FR-04, FR-07, FR-15, MSP:ADR-0029, IADR-0284 決定 5, IADR-0528 決定 3, IADR-0557 決定 2・4, #1388 (#753):
// Stage 0 の記録の「時刻 at の時点で確定していた日報の方針」を **gRPC 生成クライアント**（`DailyPolicyRead/GetConfirmedDailyPolicyAsOf`）で
// 照会する `IAsOfDailyPolicySource` の 2 つ目の実装。REST 実装（HttpAsOfDailyPolicySource）と並走する
// （**既定は REST**。`Reports:Grpc` を宣言したときだけ選ばれる。選ぶのは Stage0AsOfComposition）。
//
// 🔴 **解釈は REST と同じ 1 つ**（`HttpAsOfDailyPolicySource.Interpret`）。gRPC は線上の応答を同じ nullable の値へ写すだけ。
// 🔴 **「無かった」と「取れなかった」を分ける**: `policy` の無い応答は「無かった」（null・REST の 404）。照会の失敗・日付や要約や
// 確定の時刻の欠けた応答・読めない書式は「取れなかった」（REST と同じ撃ち直し `AsOfRetry` の後に記録を止める）。
// 照会の時刻より後の確定は撃ち直さずに記録を止める（REST と同じ）。
public sealed class GrpcAsOfDailyPolicySource(
    ReportsGrpcTransport transport,
    TimeProvider timeProvider,
    ILogger<GrpcAsOfDailyPolicySource> logger,
    TimeSpan? retryDelay = null)
    : IAsOfDailyPolicySource
{
    public Task<DailyPolicy?> GetAsync(DateTimeOffset at, CancellationToken cancellationToken = default) =>
        AsOfRetry.RunAsync<DailyPolicy?>(
            async ct =>
            {
                var response = await transport.CallAsync(
                    "当時の確定日報の方針",
                    "撃ち直します（取れなければ記録を止めます）。",
                    (client, options) => client.GetConfirmedDailyPolicyAsOfAsync(
                        new Proto.GetConfirmedDailyPolicyAsOfRequest { At = HttpAsOfDailyPolicySource.WireInstant(at) }, options),
                    ct).ConfigureAwait(false);
                if (response is null)
                    return (false, null);
                if (response.Policy is not { } policy)
                    return (true, null);

                return HttpAsOfDailyPolicySource.Interpret(
                    Date(policy),
                    policy.HasSummary ? policy.Summary : null,
                    ConfirmedAt(response),
                    policy.HasPeriodKey ? policy.PeriodKey : null,
                    policy.HasVersion ? policy.Version : null,
                    at,
                    logger,
                    "as-of-grpc");
            },
            $"{at:O} の確定日報の方針（gRPC）",
            retryDelay ?? AsOfRetry.DefaultDelay,
            timeProvider,
            logger,
            cancellationToken);

    // 🔴 原則 A: 欠落・読めない書式は null（既定値の日付・時刻で方針を作らない）。
    internal static DateOnly? Date(Proto.DailyPolicyRecord policy) =>
        policy.HasDate
        && DateOnly.TryParseExact(policy.Date, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)
            ? date
            : null;

    internal static DateTimeOffset? ConfirmedAt(Proto.GetConfirmedDailyPolicyAsOfResponse response) =>
        response.HasConfirmedAt
        && DateTimeOffset.TryParseExact(
            response.ConfirmedAt, "O", CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var confirmedAt)
            ? confirmedAt
            : null;
}

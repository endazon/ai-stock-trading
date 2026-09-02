using RiskManagementService.Domain;

namespace RiskManagementService.Features.RiskManagement.GetSessionUptime;

// FR-06, FR-20, #569, INDEX 決定34, 04_report-templates 日報 §1 / 月報 §6.2, IADR-0271:
// 期間の **OpenD 稼働率**。報告書サービス（#14）が日報の稼働率行・月報の稼働率分布のため同期照会する。
//
// **`from`・`to` の省略・逆順は 400 とする**（`/buy-in-inferences` と同じ向き）——黙って空を返すと、
// それが「稼働率 0%」「算入 0 日」として報告書に載り得る。**照会できなかったことは空の結果ではない。**
//
// 稼働率は Domain の純関数が決める（分母の解釈を SQL・HTTP 層へ写さない）。
// 累計算入日数は**発注先の許可制まで含む権威判定**（GetQualifiedTradingDayCount）をそのまま返す。
internal static class GetSessionUptimeEndpoint
{
    public static void MapGetSessionUptime(this IEndpointRouteBuilder read) =>
        read.MapGet("/session-uptime",
            (DateOnly? from, DateOnly? to, IStage1TradingDayObservationStore observations) =>
        {
            if (from is not { } fromDay || to is not { } toDay)
                return Results.BadRequest(new { error = "from・to（yyyy-MM-dd）は必須です。" });

            if (fromDay > toDay)
                return Results.BadRequest(new { error = "from は to 以前の日付を指定してください。" });

            return Results.Ok(new SessionUptimeView(
                OpenDUptimeReporting.Days(observations.GetSessionUptimesBetween(fromDay, toDay)),
                observations.GetQualifiedTradingDayCount()));
        });
}

// FR-06, FR-20, #569, INDEX 決定34, 04_report-templates 日報 §1 / 月報 §6.2, IADR-0271:
// GET /risk-controls/session-uptime の応答。
//
// 🔴 **Days に現れない取引日は「稼働率 0%」ではない。** 観測窓（ResetWindow）の外・OpenD を経由しない
// 発注先しか観測が無い日は、行そのものが存在しない。呼び出し側は欠けた日を 0% として描いてはならない。
//
// Stage1CumulativeCountedDays は**期間ではなく累計**（現在の観測窓での算入日数）であり、
// **発注先の許可制（moomoo SIMULATE）まで含む権威判定**である（Days の比率から導かれる値ではない）。
internal sealed record SessionUptimeView(
    IReadOnlyList<OpenDSessionUptimeDay> Days,
    int Stage1CumulativeCountedDays);

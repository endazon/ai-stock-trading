namespace RiskManagementService.Features.RiskManagement.GetBuyInInferences;

// 強制買戻しの推定（FR-10, FR-06, FR-21, ADR-0016 決定15, #463, IADR-0181）:
// 報告書サービス（#14）が日報の「発生有無」・月報の「発生回数」のため同期照会する。
//
// **観測の到達（FR-21）と推定行を同じ応答で返す。** 2 つのエンドポイントに分けると、
// 呼び出し側が推定行だけを見て 0 件と判断する経路が作れてしまう——台帳は推定が起きたときにしか
// 行を書かないため、**行数 0 は「観測が一度も届いていない（異常）」と「観測して 0 件だった（正常）」を
// 区別できない**。1 回の応答で両方を運び、判断を分離できないようにする。
//
// **`from`・`to` の省略・逆順は 400 とする**（`/fills` は 200 空列だが、こちらは向きが違う）——
// 黙って空列を返すと、それが「推定 0 件」として報告書に載り得る。**照会できなかったことは
// 空の結果ではない。**
internal static class GetBuyInInferencesEndpoint
{
    public static void MapGetBuyInInferences(this IEndpointRouteBuilder read) =>
        read.MapGet("/buy-in-inferences",
            (DateOnly? from, DateOnly? to,
             IBuyInInferenceStore inferences,
             IPositionObservationArrivalStore arrivals,
             IBusinessCalendar calendar) =>
        {
            if (from is not { } fromDay || to is not { } toDay)
                return Results.BadRequest(new { error = "from・to（yyyy-MM-dd）は必須です。" });

            if (fromDay > toDay)
                return Results.BadRequest(new { error = "from は to 以前の日付を指定してください。" });

            // **［2026-08-08 改定］期間が観測の届いた取引日で覆われているかを返す**
            //（計画 FR-21・裁定 planning#292）。従前は「最終観測時刻が非 null か」であり、
            // **初回観測より前の期間や観測が途中で止まった期間が「正当な 0」として報告されていた**。
            var observedDays = arrivals.GetObservedDaysBetween(fromDay, toDay);

            return Results.Ok(new
            {
                // false＝この期間は観測に覆われていない。**呼び出し側はこのとき件数を 0 と読んではならない。**
                periodCovered = ObservationCoverage.Covers(observedDays, fromDay, toDay, calendar),
                // 診断用（どの日が欠けているかを運用者が特定できるようにする）。判定には使わせない。
                observedTradingDays = observedDays,
                inferences = inferences.GetInferredBetween(fromDay, toDay),
            });
        });
}

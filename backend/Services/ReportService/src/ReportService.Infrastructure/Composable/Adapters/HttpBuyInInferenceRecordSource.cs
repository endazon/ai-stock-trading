using System.Net.Http.Json;
using ReportService.Application.Ports;
using AiStockTrading.Shared.Contracts.Events;
using AiStockTrading.Shared.Contracts.Trading;
using Microsoft.Extensions.Logging;

namespace ReportService.Infrastructure.Adapters;

// FR-10, FR-06, FR-21, UC-06, ADR-0016 決定15（2026-08-07 確定）, #463, IADR-0181:
// 強制買戻しの推定を権威源（リスク管理の推定台帳）から s2s 同期照会する
// （GET /risk-controls/buy-in-inferences・OwnerOrService・IADR-0051）。
//
// 🔴 **同居する HttpPeriodFillSource とは失敗の向きが逆である。**
// あちらは供給不達を**空列**へ倒す（報告書は発注判断を行わないため欠測が過大発注へ繋がらず、
// 「数値 0 のドラフトを提示して気付かせる」ほうが安全）。**こちらは null（未供給）へ倒す。**
// 推定経路は実在し発火し得るため、0 件と表示すると「**強制買戻しは起きていない**」と読める——
// 計画が名指しで禁じた向きである（ADR-0016 決定15・05_screens SC-03 の供給元の表）。
//
// **隣に逆向きの前例があるため、後から「揃える」方向の整理で壊されやすい。** 揃えてはならない。
internal sealed class HttpBuyInInferenceRecordSource(
    HttpClient httpClient,
    ILogger<HttpBuyInInferenceRecordSource> logger)
    : IBuyInInferenceRecordSource
{
    public async Task<IReadOnlyList<BuyInInferred>?> GetInferencesAsync(
        DateOnly fromInclusive,
        DateOnly toInclusive,
        CancellationToken cancellationToken = default)
    {
        var path = $"/risk-controls/buy-in-inferences?from={fromInclusive:yyyy-MM-dd}&to={toInclusive:yyyy-MM-dd}";

        try
        {
            using var response = await httpClient.GetAsync(path, cancellationToken).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                logger.LogWarning(
                    "強制買戻しの推定の照会に失敗しました（{Status}・{From}〜{To}）。**未供給として扱います**（0 件とは表示しません）。",
                    (int)response.StatusCode, fromInclusive, toInclusive);
                return null;
            }

            var body = await response.Content
                .ReadFromJsonAsync<BuyInInferenceQueryDto>(cancellationToken)
                .ConfigureAwait(false);

            if (body is null)
            {
                logger.LogWarning("強制買戻しの推定の応答が不正（null）でした。**未供給として扱います**。");
                return null;
            }

            // FR-21: **報告期間が観測の届いた取引日で覆われていなければ、台帳が空でも未供給である。**
            //
            // **［2026-08-08 改定］判定は期間ごとである**（計画 FR-21・裁定 planning#292）。
            // 従前は「最終観測時刻が非 null か」だったため、**初回観測より前の期間**（初回観測が 8/20 で
            // 7 月分の月報を作る場合）や**観測が途中で止まった期間**が「正当な 0」として報告されていた。
            //
            // 台帳は推定が起きたときにしか行を書かない——行数 0 は「観測が届いていない（＝この統制が
            // 働いていない）」と「観測して 0 件だった（正常）」を区別できない。前者を 0 件と描けば
            // 「強制買戻しは起きていない」と読める。
            //
            // **`periodCovered` を欠く応答（旧版 Risk）は未供給に倒す**——項目の欠落を「覆っている」と読まない。
            if (body.PeriodCovered is not true)
            {
                logger.LogWarning(
                    "報告期間がブローカ建玉の観測に覆われていません（{From}〜{To}・観測日 {Days} 日）。"
                        + "**推定台帳が空でも 0 件とは表示しません**（FR-21）。",
                    fromInclusive, toInclusive, body.ObservedTradingDays?.Count ?? 0);
                return null;
            }

            // ここから先は**正当な 0** を返し得る（期間は観測に覆われており、推定が無かったという事実である）。
            return [.. (body.Inferences ?? []).Select(ToEvent)];
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning(
                "強制買戻しの推定の照会がタイムアウトしました（{From}〜{To}）。**未供給として扱います**。",
                fromInclusive, toInclusive);
            return null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex,
                "強制買戻しの推定の照会で例外が発生しました（{From}〜{To}）。**未供給として扱います**。",
                fromInclusive, toInclusive);
            return null;
        }
    }

    // 権威源の BuyInInferenceRecord から報告書が使う BuyInInferred へ写す。
    //
    // **台帳は根拠（CoveringFills）を持たない**——推定の根拠は発行済みイベントと監査台帳（FR-11）に残っており、
    // 報告書の「発生有無・発生回数」には要らない。ここで空列を置くのは**根拠が無いという主張ではなく、
    // 本経路が根拠を運ばないという事実**である（明細が要るなら監査台帳を辿る）。
    private static BuyInInferred ToEvent(BuyInInferenceRecordDto r) => new(
        r.Id, r.Symbol, r.Market, r.LedgerShortQuantity, r.BrokerShortQuantity, r.InFlightCloseQuantity,
        r.UnexplainedQuantity, r.NewlyInferredQuantity, CoveringFills: [],
        r.BanUntil ?? r.InferredOn, r.ObservedAt, r.InferredAt);

    // 応答の受け皿（camelCase・列挙は数値で往復する）。
    private sealed record BuyInInferenceQueryDto(
        // false / 欠落＝報告期間が観測に覆われていない（FR-21・2026-08-08 改定）。
        // **`bool?` で受ける**——非 nullable にすると旧版応答の欠落が `false` と区別できず、
        // 「覆われていない」と「そもそも判定していない版」を同じに扱ってしまう
        //（どちらも未供給へ倒すが、ログで区別できるほうが運用で効く）。
        bool? PeriodCovered,
        // 診断用（どの日が欠けているかを運用者が特定できるようにする）。**判定には使わない。**
        IReadOnlyList<DateOnly>? ObservedTradingDays,
        IReadOnlyList<BuyInInferenceRecordDto>? Inferences);

    private sealed record BuyInInferenceRecordDto(
        Guid Id,
        string Symbol,
        Market Market,
        int LedgerShortQuantity,
        int BrokerShortQuantity,
        int InFlightCloseQuantity,
        int UnexplainedQuantity,
        int NewlyInferredQuantity,
        DateOnly? BanUntil,
        DateOnly InferredOn,
        DateTimeOffset ObservedAt,
        DateTimeOffset InferredAt);
}

using System.Net.Http.Json;
using ReportService.Features.Reports;
using ReportService.Domain;
using AiStockTrading.Shared.Contracts.Trading;
using Microsoft.Extensions.Logging;

namespace ReportService.Infrastructure.ExternalServices;

// FR-06, FR-16, #563, IADR-0268: 日報 §3「ポジション一覧」の建玉を権威源（リスク管理 #12 の取引台帳の射影）から
// s2s 同期照会する（GET /risk-controls/open-positions・OwnerOrService・IADR-0051）。
//
// 🔴 **供給不達は `null`（未供給）へ倒す。空列（建玉なし）と混ぜない**——
// 同居する `HttpPeriodFillSource`（不達＝空列）とは**向きが逆**であり、`HttpBuyInInferenceRecordSource` と同じ向き。
// 「建玉ゼロ」は重い事実であり、照会できなかったことと同じに書けば「今は何も持っていない」と読める。
// **隣に逆向きの前例があるため、後から「揃える」方向の整理で壊されやすい。揃えてはならない。**
public sealed class HttpOpenPositionSource(HttpClient httpClient, ILogger<HttpOpenPositionSource> logger)
    : IOpenPositionSource
{
    public async Task<IReadOnlyList<ReportPosition>?> GetOpenPositionsAsync(
        CancellationToken cancellationToken = default)
    {
        try
        {
            using var response = await httpClient
                .GetAsync("/risk-controls/open-positions", cancellationToken)
                .ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                logger.LogWarning(
                    "建玉の照会に失敗しました（{Status}）。**未供給として扱います**（「建玉なし」とは書きません）。",
                    (int)response.StatusCode);
                return null;
            }

            var rows = await response.Content
                .ReadFromJsonAsync<List<OpenPositionDto>>(cancellationToken)
                .ConfigureAwait(false);

            if (rows is null)
            {
                logger.LogWarning("建玉の応答が不正（null）でした。**未供給として扱います**。");
                return null;
            }

            // 銘柄が空の行は落とす（描画できないうえ、突き合わせの手掛かりにもならない）。
            return [.. rows.Where(r => !string.IsNullOrWhiteSpace(r.Symbol)).Select(ToPosition)];
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning("建玉の照会がタイムアウトしました。**未供給として扱います**。");
            return null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "建玉の照会で例外が発生しました。**未供給として扱います**。");
            return null;
        }
    }

    // 権威源の OpenPositionView から報告書の行へ写す。
    // **現在値・評価損益・借株料累計・保有日数は本経路が運ばない**——`null`（未供給）のまま返し、
    // 現在値の解決だけを ReportDraftService（市場データ源を持つ層）が後段で埋める。
    private static ReportPosition ToPosition(OpenPositionDto r) => new(
        r.Market, r.Symbol, r.Side, r.Quantity, r.EntryPrice, r.StopLossPrice,
        CurrentPrice: null, UnrealizedPnl: null, BorrowFeeTotal: null, HoldingDays: null);

    // 権威源の OpenPositionView と同形（camelCase・列挙は数値で往復する）。
    private sealed record OpenPositionDto(
        string Symbol,
        Market Market,
        TradeSide Side,
        int Quantity,
        decimal EntryPrice,
        decimal StopLossPrice);
}

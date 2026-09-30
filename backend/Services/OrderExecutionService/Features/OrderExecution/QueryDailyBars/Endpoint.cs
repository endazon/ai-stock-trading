using AiStockTrading.Shared.Contracts.Trading;
using AiStockTrading.TestSupport.PlatformShim.Foundation.Extensions;

namespace OrderExecutionService.Features.OrderExecution.QueryDailyBars;

// FR-04, FR-15, ADR-0048 決定 2, #1118, IADR-0467 決定 2: 日足の照会の口（読み取り専用。前復権）。
// 呼び手は取引判断（判断へ渡す出来高。既定は無効で、有効化するまで呼ばれない）であり、サービスの資格で呼ぶ。利用者も読める（OwnerOrService）。
// 応答は DailyBarsView を web 既定（camelCase・列挙は数値・日付は yyyy-MM-dd）で返す。受け手（取引判断の HttpDailyBarsSource）は
// この本文を読む契約テストを持つ（IADR-0420）。
public static class DailyBarsEndpoint
{
    /// <summary>1 回の照会で求められる期間の上限（暦日。両端を含む）。判断は 45 暦日＋α を求める。</summary>
    public const int MaxRangeDays = 400;

    /// <summary>銘柄コードの長さの上限（それを超える値は照会しない）。</summary>
    public const int MaxSymbolLength = 32;

    public static WebApplication MapDailyBarsEndpoint(this WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);

        app.MapGet("/order-execution/daily-bars", async (
                string? symbol,
                int? market,
                DateOnly? from,
                DateOnly? to,
                DailyBarsQueryService service,
                CancellationToken cancellationToken) =>
            {
                // 値域外は照会しない（OpenD の取得枠・頻度を使わない）。市場は数値（Market の値）で受ける。
                if (string.IsNullOrWhiteSpace(symbol) || symbol.Length > MaxSymbolLength
                    || market is not { } marketValue || !Enum.IsDefined((Market)marketValue)
                    || from is not { } fromDate || to is not { } toDate
                    || fromDate > toDate || toDate.DayNumber - fromDate.DayNumber + 1 > MaxRangeDays)
                {
                    return Results.BadRequest(new
                    {
                        error = $"symbol（{MaxSymbolLength} 文字まで）・market（市場の数値）・from / to（yyyy-MM-dd。from ≦ to・{MaxRangeDays} 日まで）が必要です。",
                    });
                }

                return Results.Ok(await service
                    .QueryAsync(symbol, (Market)marketValue, fromDate, toDate, cancellationToken)
                    .ConfigureAwait(false));
            })
            .RequireAuthorization(AiStockTradingAuthPolicies.OwnerOrService);

        return app;
    }
}

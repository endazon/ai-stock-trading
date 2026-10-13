using AiStockTrading.Shared.Contracts.Trading;
using AiStockTrading.TestSupport.PlatformShim.Foundation.Extensions;

namespace OrderExecutionService.Features.OrderExecution.QueryInFlightCloses;

// 🔴 FR-04, FR-10, #1338, IADR-0535 決定2: 処理中の決済の照会の口（読み取り専用）。
// 呼び手は取引判断（保有中の銘柄で LLM を呼ぶ前）であり、サービスの資格で呼ぶ。利用者も読める（OwnerOrService）。
// 応答は InFlightClosesView を web 既定（camelCase・列挙は数値）で返す。受け手（取引判断の HttpInFlightCloseProvider）は
// この本文を読む契約テストを持つ（IADR-0420）。
public static class InFlightClosesEndpoint
{
    /// <summary>銘柄コードの長さの上限（それを超える値は照会しない）。</summary>
    public const int MaxSymbolLength = 32;

    public static WebApplication MapInFlightClosesEndpoint(this WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);

        app.MapGet("/order-execution/in-flight-closes", async (
                string? symbol,
                int? market,
                int? closeSide,
                InFlightClosesQueryService service,
                CancellationToken cancellationToken) =>
            {
                // 値域外は照会しない（ブローカーの枠を使わない）。市場・決済の方向は数値（Market・TradeSide の値）で受ける。
                if (string.IsNullOrWhiteSpace(symbol) || symbol.Length > MaxSymbolLength
                    || market is not { } marketValue || !Enum.IsDefined((Market)marketValue)
                    || closeSide is not { } sideValue || !Enum.IsDefined((TradeSide)sideValue))
                {
                    return Results.BadRequest(new { error = "symbol（銘柄コード）・market（市場の数値）・closeSide（決済の方向の数値）が必要です。" });
                }

                return Results.Ok(await service
                    .QueryAsync(symbol, (Market)marketValue, (TradeSide)sideValue, cancellationToken)
                    .ConfigureAwait(false));
            })
            .RequireAuthorization(AiStockTradingAuthPolicies.OwnerOrService);

        return app;
    }
}

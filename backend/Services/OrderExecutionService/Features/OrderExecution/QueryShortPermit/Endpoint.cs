using AiStockTrading.Shared.Contracts.Trading;
using AiStockTrading.TestSupport.PlatformShim.Foundation.Extensions;

namespace OrderExecutionService.Features.OrderExecution.QueryShortPermit;

// FR-10, UC-06, ADR-0016 決定3（2026-08-06 改訂）, #967, IADR-0425 決定1: 借株可否の照会の口（読み取り専用）。
// 呼び手はリスク管理の審査（新規の売り建て）であり、サービスの資格（trading-service）で呼ぶ。利用者も読める（OwnerOrService）。
// 応答は ShortPermitView を web 既定（camelCase・列挙は数値）で返す。受け手（リスク管理の HttpShortSellBorrowSource）は
// この本文を読む契約テストを持つ（IADR-0420）。
public static class ShortPermitEndpoint
{
    public static WebApplication MapShortPermitEndpoint(this WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);

        app.MapGet("/order-execution/short-permit", async (
                string? symbol,
                int? market,
                ShortPermitQueryService service,
                CancellationToken cancellationToken) =>
            {
                // 値域外は照会しない（ブローカーの枠を使わない）。市場は数値（Market の値）で受ける。
                if (string.IsNullOrWhiteSpace(symbol) || market is not { } marketValue || !Enum.IsDefined((Market)marketValue))
                {
                    return Results.BadRequest(new { error = "symbol（銘柄コード）と market（市場の数値）が必要です。" });
                }

                return Results.Ok(await service.QueryAsync(symbol, (Market)marketValue, cancellationToken).ConfigureAwait(false));
            })
            .RequireAuthorization(AiStockTradingAuthPolicies.OwnerOrService);

        return app;
    }
}

using Microsoft.AspNetCore.Builder;
using AiStockTrading.TestSupport.PlatformShim.Foundation.Middleware;

namespace AiStockTrading.TestSupport.PlatformShim.Foundation.Extensions;

// IADR-0011（platform の最小移植・由来: Foundation/Extensions/CommonServiceExtensions.cs）:
// 相関ID・認証・認可のミドルウェアを共通の順序で束ねる。各サービスの Program.cs で呼び出す。
// NFR-06, IADR-0496, #1192: 先頭に例外処理（ProblemDetails。ヘッダー・スタックを返さない）を置く。
public static class CommonServiceExtensions
{
    public static WebApplication UseAiStockTradingMiddleware(this WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);
        app.UseAiStockTradingExceptionHandler();
        app.UseMiddleware<CorrelationIdMiddleware>();
        app.UseAuthentication();
        app.UseAuthorization();
        return app;
    }
}

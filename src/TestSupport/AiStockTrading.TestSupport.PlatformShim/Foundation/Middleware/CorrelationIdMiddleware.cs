using Microsoft.AspNetCore.Http;

namespace AiStockTrading.TestSupport.PlatformShim.Foundation.Middleware;

// IADR-0011（platform の最小移植・由来: Foundation/Middleware/CorrelationIdMiddleware.cs）:
// X-Correlation-ID を受領ヘッダから引き継ぎ（無ければ生成）、レスポンスへ反映しつつログへ相関付けする。
// 分散トレース・監査（FR-11）で 1 リクエストを横断的に追跡できるようにする。
public class CorrelationIdMiddleware(RequestDelegate next)
{
    private const string Header = "X-Correlation-ID";

    public async Task InvokeAsync(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (!context.Request.Headers.TryGetValue(Header, out var correlationId)
            || string.IsNullOrWhiteSpace(correlationId))
        {
            correlationId = Guid.NewGuid().ToString();
        }

        context.Response.Headers[Header] = correlationId.ToString();
        using (Serilog.Context.LogContext.PushProperty("CorrelationId", correlationId.ToString()))
        {
            await next(context);
        }
    }
}

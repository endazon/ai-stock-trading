using AiStockTrading.MarketMonitor.Application.Ports;
using AiStockTrading.MarketMonitor.Domain;
using AiStockTrading.TestSupport.PlatformShim.Foundation.Extensions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;

namespace AiStockTrading.MarketMonitor.Worker.Foundation.Endpoints;

// FR-03, FR-13, ADR-0007: 監視設定（監視銘柄・変動閾値・クールダウン）の照会・変更。利用者のみ（OwnerOnly）。
// 生成AI・自動処理はこのロールを持たないため変更できない。
internal static class MonitorSettingsEndpoints
{
    public static IEndpointRouteBuilder MapMonitorSettingsEndpoints(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/monitor")
            .RequireAuthorization(AiStockTradingAuthPolicies.OwnerOnly)
            .WithTags("MarketMonitor")
            // 検証失敗は 400、設定の楽観排他競合（IADR-0012）は 409 に写像（既定の 500 を回避）。
            .AddEndpointFilter(async (ctx, next) =>
            {
                try
                {
                    return await next(ctx);
                }
                catch (ArgumentException e)
                {
                    return Results.BadRequest(new { error = e.Message });
                }
                catch (DbUpdateConcurrencyException)
                {
                    return Results.Conflict(new { error = "設定が他の更新と競合しました。最新を取得して再試行してください。" });
                }
            });

        g.MapGet("/settings", (IMonitoredSymbolStore store) => Results.Ok(store.GetSettings()));

        g.MapPut("/settings", (MonitorSettingsUpdateRequest req, IMonitoredSymbolStore store) =>
        {
            store.Save(req.ToSettings());
            return Results.Ok(store.GetSettings());
        });

        return app;
    }
}

// 監視設定変更の要求。MonitoredSymbols は逆直列化可能な具象 List で受ける。
internal sealed record MonitorSettingsUpdateRequest(
    decimal MovementThresholdRatio,
    TimeSpan Cooldown,
    List<MonitoredSymbol> MonitoredSymbols)
{
    public MarketMonitorSettings ToSettings()
    {
        // FR-03: 不正値を弾く（閾値は正、クールダウンは非負）。
        if (MovementThresholdRatio <= 0m)
        {
            throw new ArgumentException("変動閾値は正の値である必要があります。", nameof(MovementThresholdRatio));
        }

        if (Cooldown < TimeSpan.Zero)
        {
            throw new ArgumentException("クールダウンは非負である必要があります。", nameof(Cooldown));
        }

        return new MarketMonitorSettings
        {
            MovementThresholdRatio = MovementThresholdRatio,
            Cooldown = Cooldown,
            MonitoredSymbols = MonitoredSymbols,
        };
    }
}

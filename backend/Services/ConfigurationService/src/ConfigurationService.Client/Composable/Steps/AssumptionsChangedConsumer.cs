using AiStockTrading.Configuration.Client.Ports;
using AiStockTrading.Shared.Contracts.Events;
using MassTransit;
using Microsoft.Extensions.Logging;

namespace AiStockTrading.Configuration.Client.Composable.Steps;

// FR-17, UC-06, IADR-0063 決定 1/4: 利用者が前提条件を変更したら（AssumptionsChanged）キャッシュを無効化し、次の参照で
// 新しい版を取り直す。イベント本文の値は使わない（本文から値を復元すると、取りこぼしや順序逆転で誤った版を保持しうる。
// 版の追随はあくまで GET /assumptions の再取得で行う）。
//
// 消費側サービスの Program で `x.AddConsumer<AssumptionsChangedConsumer>()` して用いる（購読はキャッシュ無効化のみで
// 副作用を持たない）。監査台帳への記録は監査サービス（#17）が同じイベントを購読して行う。
public sealed class AssumptionsChangedConsumer(
    IAssumptionsCacheInvalidator invalidator,
    ILogger<AssumptionsChangedConsumer> logger)
    : IConsumer<AssumptionsChanged>
{
    public Task Consume(ConsumeContext<AssumptionsChanged> context)
    {
        ArgumentNullException.ThrowIfNull(context);

        logger.LogInformation("全体前提条件が v{Version} へ変更されました。キャッシュを無効化します。", context.Message.Version);
        invalidator.Invalidate();
        return Task.CompletedTask;
    }
}

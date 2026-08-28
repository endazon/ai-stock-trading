using CostControlService.Infrastructure.ExternalServices;
using AiStockTrading.Shared.Contracts.Events;
using Microsoft.Extensions.Logging;

namespace CostControlService.Infrastructure.Steps;

// FR-17, UC-06, IADR-0063 決定 1/4: 利用者が前提条件を変更したら（AssumptionsChanged）キャッシュを無効化し、次の参照で
// 新しい版を取り直す。イベント本文の値は使わない（本文から値を復元すると、取りこぼしや順序逆転で誤った版を保持しうる。
// 版の追随はあくまで GET /assumptions の再取得で行う）。
//
// ADR-0013, IADR-0129, #354: MassTransit の IConsumer<AssumptionsChanged> から Wolverine のハンドラへ移行した。
// 消費側サービスは Program で `opts.UseAiStockTradingRabbitMq(ServiceName, ..., <本アセンブリ>)` と書いて
// ハンドラの発見範囲へ含める（MassTransit の `x.AddConsumer<T>()` に相当する）。
//
// NFR, #526, IADR-0264 決定 1/決定 4: 旧 ConfigurationService.Client から移した。**移した先は費用統制サービスだけ**である
// ——AssumptionsChanged を購読しているのは本サービスのみで（実測）、取引判断サービスへも複製すると Wolverine の
// アセンブリ走査が新しい購読を発見し、移送で振る舞いが変わる（IADR-0259 決定 7 違反）。**移送時に中身は変えていない。**
// 購読はキャッシュ無効化のみで副作用を持たない。監査台帳への記録は監査サービス（#17）が同じイベントを購読して行う。
//
// **public であること自体が要件である**: Wolverine は public なハンドラ型しか発見しない（実測。internal だと
// 「ハンドラも購読先も見つからない」という実行時例外になる）。
public sealed class AssumptionsChangedHandler(
    IAssumptionsCacheInvalidator invalidator,
    ILogger<AssumptionsChangedHandler> logger)
{
    public void Handle(AssumptionsChanged message)
    {
        ArgumentNullException.ThrowIfNull(message);

        logger.LogInformation("全体前提条件が v{Version} へ変更されました。キャッシュを無効化します。", message.Version);
        invalidator.Invalidate();
    }
}

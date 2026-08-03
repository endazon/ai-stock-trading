using AiStockTrading.TradeDecision.Application.Ports;
using AiStockTrading.Shared.Contracts.Events;
using MassTransit;
using Microsoft.Extensions.Logging;

namespace AiStockTrading.TradeDecision.Infrastructure.Composable.Adapters;

// UC-01, FR-09, FR-11, IADR-0096, #210: 日報未確定（policy-null）による見送りを通知する実装（opt-in）。
// DailyPolicyUnconfirmed を publish し、NotificationService（利用者通知）・AuditService（監査）へ連携する。
//
// 営業日単位の重複抑止（IADR-0096 決定4）: 定時サイクルは watchlist を巡回し全銘柄で policy-null に当たるため、
// 無抑止だと 1 巡回で N 件・巡回ごとに再発する。当日（UtcNow の日付）初回のみ publish し、以降は当日の再通知を抑止、
// 翌営業日で再通知する。抑止状態は in-memory（singleton・スレッドセーフ）で保持する。Worker は意図的にステートレス
// （DB なし）であり、リマインダ 1 件のための永続化層は過剰。再起動時に当日リマインダが最大 1 回重複し得るが無害。
//
// singleton として登録し、scoped な取引判断（TradeDecisionService）から呼ばれても状態を跨いで共有する。発行は singleton の
// IBus を用いる（scoped な IPublishEndpoint は singleton から解決できないため）。
internal sealed class PublishingDailyPolicyUnconfirmedNotifier(
    IBus bus,
    IClock clock,
    ILogger<PublishingDailyPolicyUnconfirmedNotifier> logger) : IDailyPolicyUnconfirmedNotifier
{
    private readonly object _gate = new();
    private DateOnly? _lastNotifiedBusinessDay;

    public async Task NotifyAsync(CancellationToken cancellationToken = default)
    {
        var occurredAt = clock.UtcNow;
        // BusinessDay/dedup 鍵は UTC 暦日（JST 営業日ではない）。ReportService と同一の慣行に揃える（IADR-0096 決定4・
        // イベント定義コメント参照）。トリガーは実運用上ほぼ市場営業時間内に発火するため JST 境界のずれは実害が小さい。
        var businessDay = DateOnly.FromDateTime(occurredAt.UtcDateTime);

        // 当日既通知なら抑止（当日初回のみ true）。チェックと記録を一体に行い、巡回内の並行呼び出しでも 1 回に収束させる。
        lock (_gate)
        {
            if (_lastNotifiedBusinessDay == businessDay)
                return;
            _lastNotifiedBusinessDay = businessDay;
        }

        try
        {
            await bus.Publish(new DailyPolicyUnconfirmed(businessDay, occurredAt), cancellationToken).ConfigureAwait(false);
            logger.LogInformation("日報未確定を通知しました（確定を促す）: 営業日 {BusinessDay}", businessDay);
        }
        catch
        {
            // 発行失敗時は当日の「通知済み」記録を戻し、次の見送りで再試行できるようにする（恒久的に握り潰さない）。
            lock (_gate)
            {
                if (_lastNotifiedBusinessDay == businessDay)
                    _lastNotifiedBusinessDay = null;
            }
            throw;
        }
    }
}

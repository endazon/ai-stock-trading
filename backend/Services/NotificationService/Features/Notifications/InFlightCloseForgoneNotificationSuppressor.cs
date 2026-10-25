using AiStockTrading.Shared.Contracts.Events;
using AiStockTrading.Shared.Contracts.Trading;

namespace NotificationService.Features.Notifications;

// 🔴 FR-09, FR-04, #1338, IADR-0535 決定4: 「処理中の決済が建玉を覆うため決済を見送った」（OrderDispatchForgone・理由 InFlightCloseCoversPosition）の
// **通知だけ**を、同じ建玉（銘柄・市場・決済の方向）・同じ処理中の決済（注文 ID の集合）につき 1 回へ畳む。是正前は、指値の決済が約定しない間、
// 判断が 5.5 分ごとに同じ決済を出し直し、見送りの通知が 1 セッションに 33 件出た（AAPL・2026-10-13）。監査は別のキューで全件を受け取る（変えない）。
//
// - 鍵に注文 ID を含める: 処理中の決済が替わる（約定・取消の後に別の決済が出る）と別の鍵になり、改めて 1 回通知する。
// - 注文 ID が無い（旧い送り手・ほかの理由）なら畳まない（従来どおり毎回通知する）。
// - 記憶は <see cref="RetainFor"/> で捨てる（日中の注文はセッションを越えない。鍵は少数だが無限に溜めない）。時刻は見送りの発生時刻（OccurredAt）で測る。
// - 送信に失敗した見送りは記憶から戻す（メッセージングの再試行で送り直せるように）。記憶はプロセス内だけ（再起動の直後は 1 回重なり得る）。
// - 前例は損切りライン到達の通知の抑止（StopLossNotificationSuppressor・IADR-0520 決定4）。ハンドラは並行に呼ばれ得るので排他する。
public sealed class InFlightCloseForgoneNotificationSuppressor
{
    /// <summary>#1338, IADR-0535: 同じ処理中の決済について通知済みと覚えておく長さ。</summary>
    public static readonly TimeSpan RetainFor = TimeSpan.FromHours(24);

    private readonly object _gate = new();
    private readonly Dictionary<ForgoneKey, DateTimeOffset> _notified = [];

    /// <summary>
    /// この見送りを通知してよいか。畳む対象（理由が InFlightCloseCoversPosition で注文 ID がある）でなければ常に true（記憶しない）。
    /// よければ通知したものとして記憶する（送信に失敗したら <see cref="Release"/> で戻す）。
    /// </summary>
    public bool TryAcquire(OrderDispatchForgone forgone)
    {
        ArgumentNullException.ThrowIfNull(forgone);
        if (ForgoneKey.Of(forgone) is not { } key)
            return true;

        lock (_gate)
        {
            foreach (var stale in _notified.Where(e => forgone.OccurredAt - e.Value >= RetainFor).Select(e => e.Key).ToList())
                _notified.Remove(stale);

            if (_notified.ContainsKey(key))
                return false;

            _notified[key] = forgone.OccurredAt;
            return true;
        }
    }

    /// <summary>送信に失敗した見送りの記憶を戻す（この見送りが記憶したものに限る）。</summary>
    public void Release(OrderDispatchForgone forgone)
    {
        ArgumentNullException.ThrowIfNull(forgone);
        if (ForgoneKey.Of(forgone) is not { } key)
            return;

        lock (_gate)
        {
            if (_notified.TryGetValue(key, out var at) && at == forgone.OccurredAt)
                _notified.Remove(key);
        }
    }

    private readonly record struct ForgoneKey(string Symbol, Market Market, TradeSide CloseSide, string OrderIds)
    {
        public static ForgoneKey? Of(OrderDispatchForgone f)
        {
            if (f.Reason != OrderDispatchForgoneReason.InFlightCloseCoversPosition
                || f.InFlightCloseOrderIds is not { Count: > 0 } ids)
            {
                return null;
            }

            // 並びに依らない集合として比べる（注文 ID はブローカーの文字列。大文字小文字を区別する）。
            var normalized = string.Join("\n", ids.Where(id => !string.IsNullOrEmpty(id)).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal));
            return normalized.Length == 0 ? null : new ForgoneKey(f.Intent.Symbol, f.Intent.Market, f.Intent.Side, normalized);
        }
    }
}

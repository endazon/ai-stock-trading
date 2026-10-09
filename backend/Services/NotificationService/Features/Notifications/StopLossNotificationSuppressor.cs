using AiStockTrading.Shared.Contracts.Events;
using AiStockTrading.Shared.Contracts.Trading;

namespace NotificationService.Features.Notifications;

// FR-09, FR-10, UC-02, #1280, IADR-0520 決定4: 損切りライン到達の**通知だけ**を、同じ到達（銘柄・市場・建玉方向・ライン）につき
// 抑止の間隔に 1 回へ絞る。市場監視は前回の発行より不利な価格の到達を出し直す（低いラインの S1 の行を遅らせないため。IADR-0520 決定2 追記）
// ので、価格が下げ続ける間は到達が巡回ごとに届く（PoC の NVDA 232.90 → 232.63）。発注執行・監査は別のキューで全部を受け取り、
// ここでの抑止は Discord への通知にしか効かない。
//
// - 間隔は市場監視の出し直しの間隔（StopLossArrivalGate.RepublishAfter）と同じ 3 分。運用者には同じ到達が残る間、3 分ごとに念押しが届く。
//   間隔は到達の検知時刻（DetectedAt）で測る（時計を持たない。遅れて届いた古い到達は負の差になり抑止される）。
// - ラインが変われば別の到達（鍵が変わる）。建玉が閉じたことは通知サービスからは見えない（契約に銘柄の無い約定しか届かない）ので、
//   閉じて同じラインで建て直した到達は 3 分以内なら通知されない（残余リスク。発注執行・監査には届く）。
//   価格が戻ったことも見えないので、ラインの内側へ戻って 3 分以内に再び割った到達も通知されない（同じ残余）。
// - 送信に失敗した到達は記憶から戻す（メッセージングの再試行で送り直せるように）。
// - 記憶はプロセス内だけ（再起動の直後は 1 回重なり得る）。ハンドラは並行に呼ばれ得るので排他する。
public sealed class StopLossNotificationSuppressor
{
    /// <summary>#1280, IADR-0520: 同じ到達を通知し直すまでの間隔（市場監視の出し直しの間隔と同じ）。</summary>
    public static readonly TimeSpan RenotifyAfter = TimeSpan.FromMinutes(3);

    private readonly object _gate = new();
    private readonly Dictionary<ArrivalKey, DateTimeOffset> _notified = [];

    /// <summary>
    /// この到達を通知してよいか。よければ通知したものとして記憶する（送信に失敗したら <see cref="Release"/> で戻す）。
    /// </summary>
    public bool TryAcquire(StopLossTriggered arrival)
    {
        ArgumentNullException.ThrowIfNull(arrival);
        var key = ArrivalKey.Of(arrival);
        lock (_gate)
        {
            if (_notified.TryGetValue(key, out var last) && arrival.DetectedAt - last < RenotifyAfter)
                return false;

            // 間隔を過ぎた記憶は捨てる（鍵は銘柄・ラインごとに少数だが、無限に溜めない）。
            foreach (var stale in _notified.Where(e => arrival.DetectedAt - e.Value >= RenotifyAfter).Select(e => e.Key).ToList())
                _notified.Remove(stale);

            _notified[key] = arrival.DetectedAt;
            return true;
        }
    }

    /// <summary>送信に失敗した到達の記憶を戻す（この到達が記憶したものに限る）。</summary>
    public void Release(StopLossTriggered arrival)
    {
        ArgumentNullException.ThrowIfNull(arrival);
        var key = ArrivalKey.Of(arrival);
        lock (_gate)
        {
            if (_notified.TryGetValue(key, out var last) && last == arrival.DetectedAt)
                _notified.Remove(key);
        }
    }

    private readonly record struct ArrivalKey(string Symbol, Market Market, TradeSide Side, decimal StopLossPrice)
    {
        public static ArrivalKey Of(StopLossTriggered a) => new(a.Symbol, a.Market, a.PositionSide, a.StopLossPrice);
    }
}

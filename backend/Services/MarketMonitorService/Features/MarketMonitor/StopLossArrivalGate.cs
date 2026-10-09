using AiStockTrading.Shared.Contracts.Events;
using AiStockTrading.Shared.Contracts.Trading;
using MarketMonitorService.Domain;

namespace MarketMonitorService.Features.MarketMonitor;

// FR-10, FR-03, UC-02, #1280, IADR-0520: 損切りライン到達（StopLossTriggered）を同じ到達につき 1 回だけ発行するための記憶。
// 決済が約定してリスク管理の台帳から建玉が消えるまでの間に始まった巡回は、同じ建玉を保有として読み直して同じ到達を
// 再評価する。そのまま発行すると通知（Discord）と監査台帳に「再到達」が残る（PoC 2026-10-08 で 2 件ずつ）。
//
// - 到達の同一性は（銘柄・市場・建玉方向・ライン）。ラインが変われば別の到達である（数量は鍵に入れない——部分約定で
//   数量だけが変わった巡回を新しい到達にしない）。
// - **記憶を解くのは「戻った」と言える巡回だけ**: 建玉が保有の照会から消えた、または価格を取ってラインの内側だった。
//   価格が取れなかった・閉場で評価しなかった巡回は戻った証拠ではないので記憶を残す（次の巡回で再発行しない）。
// - 🔴 **黙らせ続けない**: 同じ到達が <see cref="RepublishAfter"/> を過ぎても残っていれば出し直す（決済が進まない・
//   保護記録の行が到達の後に作られた等を、発注執行・通知へ再び届ける）。間隔は発注執行の到達の窓
//   （SoftwareStopExecutor.TriggerEpisodeGap＝5 分）より短くし、出し直しで決済の待ち時間がやり直しにならないようにする。
// - 発行に失敗した到達は記憶しない（次の巡回で発行し直す）。記憶はプロセス内だけ（再起動の直後は 1 回重なり得る）。
//
// MonitorPollingService（singleton）が 1 つ持ち、巡回は直列に回るので排他は持たない。
public sealed class StopLossArrivalGate
{
    /// <summary>
    /// #1280, IADR-0520: 同じ到達を出し直すまでの間隔。巡回間隔（既定 60 秒）＋決済の台帳への反映より長く、
    /// 発注執行の到達の窓（5 分）より短い。
    /// </summary>
    public static readonly TimeSpan RepublishAfter = TimeSpan.FromMinutes(3);

    private readonly Dictionary<ArrivalKey, DateTimeOffset> _published = [];

    /// <summary>この到達を発行すべきか（初めての到達か、前回の発行から <see cref="RepublishAfter"/> 以上経った）。</summary>
    public bool ShouldPublish(StopLossTriggered arrival)
    {
        ArgumentNullException.ThrowIfNull(arrival);
        return !_published.TryGetValue(ArrivalKey.Of(arrival), out var last)
            || arrival.DetectedAt - last >= RepublishAfter;
    }

    /// <summary>発行できた到達を記憶する（発行に失敗した到達は呼ばない）。</summary>
    public void MarkPublished(StopLossTriggered arrival)
    {
        ArgumentNullException.ThrowIfNull(arrival);
        _published[ArrivalKey.Of(arrival)] = arrival.DetectedAt;
    }

    /// <summary>
    /// 1 巡回の評価を終えたら呼ぶ。この巡回でも到達していた到達と、戻ったかどうか分からない（価格が取れなかった・
    /// 閉場で評価しなかった）建玉の到達だけを残し、それ以外（建玉が消えた・ラインの内側へ戻った）を忘れる。
    /// </summary>
    public void Settle(MonitorRoundResult round)
    {
        ArgumentNullException.ThrowIfNull(round);
        if (_published.Count == 0)
            return;

        var keep = new HashSet<ArrivalKey>(round.StopLosses.Select(ArrivalKey.Of));
        foreach (var unresolved in round.StopLossEvaluations.Where(e => e.Price is null).Concat(round.ClosedMarketPositions))
            keep.Add(ArrivalKey.Of(unresolved));

        foreach (var key in _published.Keys.Where(k => !keep.Contains(k)).ToList())
            _published.Remove(key);
    }

    private readonly record struct ArrivalKey(string Symbol, Market Market, TradeSide Side, decimal StopLossPrice)
    {
        public static ArrivalKey Of(StopLossTriggered a) => new(a.Symbol, a.Market, a.PositionSide, a.StopLossPrice);

        public static ArrivalKey Of(StopLossEvaluation e) => new(e.Symbol, e.Market, e.Side, e.StopLossPrice);
    }
}

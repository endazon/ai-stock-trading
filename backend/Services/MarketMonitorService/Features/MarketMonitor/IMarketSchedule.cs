using AiStockTrading.Shared.Contracts.Trading;

namespace MarketMonitorService.Features.MarketMonitor;

// FR-03, #909, IADR-0380 決定1: 市場の開場判定。**市場ごとに**判定する（04_workflows/02: 市場閉場中は監視停止）。
//
// 🔴 **市場を引数に取るのは「全市場を一括で開場と読む」ことをできなくするためである。**
// 引数の無い旧ポート（`IsOpen(DateTimeOffset)`）は UTC 曜日だけの近似実装を許し、**米東 16:00 の引けから
// 5 時間後も巡回が回り続けた**（#909 の実測）。閉場中の終値は動かないので、終値がラインを割った日は
// 閉場中ずっと到達が成立し続け、出した成行は翌寄りまで約定しない。
//
// 実装は市場ローカル時刻・休場日・半日取引日を見る（Infrastructure/ExternalServices/MarketHoursSchedule）。
// ポートを残すのは、市場カレンダーの供給元（規則計算 → 外部カレンダー API 等）を差し替える継ぎ目のためである（#21）。
public interface IMarketSchedule
{
    /// <summary>その瞬間にその市場が場中か。</summary>
    bool IsOpen(Market market, DateTimeOffset instant);

    /// <summary>
    /// その瞬間より**後**に来る最初の開場時刻。保護の空白が「いつまで続くか」を声に出すために使う。
    /// 見通せないときは <c>null</c>（呼び出し側は時刻を伏せて報告する）。
    /// </summary>
    DateTimeOffset? NextOpen(Market market, DateTimeOffset instant);
}

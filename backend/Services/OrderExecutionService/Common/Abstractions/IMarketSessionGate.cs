using AiStockTrading.Shared.Contracts.Trading;
using AiStockTrading.Shared.Kernel.Trading;

namespace OrderExecutionService.Common.Abstractions;

// 🔴 FR-10, NFR, #1444（親 #1423 M-1）, IADR-0574 決定1: 「今その市場は場中か」を発注執行の巡回が問う口。
// 保護逆指値ガード（逆指値の張り直し・成行の手仕舞い）と S1 の成行の決済は、**閉場中は新しい注文を送らない**。
// 判断の実体は共有カーネルの MarketHours（市場監視・取引判断・閉場前の統制と同じ答え。IADR-0380 決定5）であり、本口は注入点だけを持つ。
public interface IMarketSessionGate
{
    /// <summary>その瞬間にその市場が場中か（週末・休場日でなく、かつ取引時間内）。</summary>
    bool IsOpen(Market market, DateTimeOffset instant);

    /// <summary>その瞬間より後の最初の開場時刻（ログに「いつ張り直すか」を出すため）。見つからなければ null。</summary>
    DateTimeOffset? NextOpen(Market market, DateTimeOffset instant);
}

// 本番の実体（規則計算の休場日・半日取引日。臨時休場は足さない——発注執行に構成の口は無い。IADR-0574 残余 2）。
public sealed class MarketHoursSessionGate : IMarketSessionGate
{
    public static readonly MarketHoursSessionGate Instance = new();

    public bool IsOpen(Market market, DateTimeOffset instant) => MarketHours.IsOpen(market, instant);

    public DateTimeOffset? NextOpen(Market market, DateTimeOffset instant) => MarketHours.NextOpen(market, instant);
}

// 判定しない構成（単体の試験の既定。本番では使わない——Program.cs は MarketHoursSessionGate を渡し、T-10-3001 が固定する）。
// 省略時に常に場中と読むのは、ゲートを持つ前の挙動（時間帯を見ない）をそのまま残すためである。
public sealed class AlwaysOpenSessionGate : IMarketSessionGate
{
    public static readonly AlwaysOpenSessionGate Instance = new();

    public bool IsOpen(Market market, DateTimeOffset instant) => true;

    public DateTimeOffset? NextOpen(Market market, DateTimeOffset instant) => instant;
}

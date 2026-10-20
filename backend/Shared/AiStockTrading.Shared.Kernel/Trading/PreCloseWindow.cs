namespace AiStockTrading.Shared.Kernel.Trading;

// FR-10, FR-19, #1396, IADR-0561 決定 1: 「閉場の N 分前以降か」の判定（純関数）。閉場前の新規建ての締め切り（リスク管理の審査）と
// 閉場前の手仕舞い（取引判断）が同じ式を使う。閉場時刻は MarketHours.SessionCloseOn（市場ローカルの日付の最後の閉場。休場日は null）。
//
// 区間は **[閉場 − N 分, その市場ローカルの日の終わり)** である（「以降」＝閉場の後も同じ日のうちは入る）。閉場時刻は市場ローカルの日付ごとに
// 引き直すため、翌取引日の寄り付き前は入らない。休場日（閉場が null）は入らない —— 休場日の新規建ては場中の判定（MarketHours.IsOpen）の側が扱う。
public static class PreCloseWindow
{
    /// <summary><paramref name="now"/> が閉場 <paramref name="close"/> の <paramref name="minutes"/> 分前以降か。閉場が無い（null）なら false。</summary>
    public static bool IsAtOrAfter(DateTimeOffset now, DateTimeOffset? close, int minutes) =>
        close is { } c && now >= c.AddMinutes(-minutes);

    /// <summary>閉場までの残り時間（閉場の後は 0）。閉場が無い（null）なら null。</summary>
    public static TimeSpan? Remaining(DateTimeOffset now, DateTimeOffset? close) =>
        close is { } c ? (c > now ? c - now : TimeSpan.Zero) : null;
}

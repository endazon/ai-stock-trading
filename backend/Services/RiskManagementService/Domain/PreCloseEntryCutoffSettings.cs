using AiStockTrading.Shared.Contracts.Trading;
using AiStockTrading.Shared.Kernel.Trading;

namespace RiskManagementService.Domain;

// 🔴 FR-10, FR-19, #1396, IADR-0561 決定 2: 閉場前の新規建ての締め切り（市場ごとの分数）。利用者裁定 2026-10-20（planning#808）。
// 閉場の N 分前以降の新規建て（買い増し・売り増しを含む）を審査が PreCloseEntryCutoff で拒否する。手仕舞い（Close）は止めない（ADR-0009）。
//
// 🔴 **統制を外す値は持たない**（0・負・未設定の市場を「締め切りなし」にしない）。構成に無い市場は既定（60 分）で効く。
// 範囲は 1 〜 その市場の通常の場中の長さ（米国 390 分・東証 330 分）。範囲外は起動を止める（黙って丸めない）。
public sealed record PreCloseEntryCutoffSettings(IReadOnlyDictionary<Market, int> Minutes)
{
    /// <summary>その市場の締め切り（閉場の何分前から新規建てを拒否するか）。構成に無い市場は既定（<see cref="TradingDefaults.PreCloseEntryCutoffMinutes"/>）。</summary>
    public int MinutesFor(Market market) =>
        Minutes.TryGetValue(market, out var minutes) ? minutes : TradingDefaults.PreCloseEntryCutoffMinutes;

    /// <summary>範囲（1 〜 通常の場中の長さ）の外の値があれば例外（起動を止める）。</summary>
    public PreCloseEntryCutoffSettings Validate()
    {
        foreach (var (market, minutes) in Minutes)
        {
            var max = MarketSessions.RegularSessionMinutes(market);
            if (minutes < 1 || minutes > max)
            {
                throw new InvalidOperationException(
                    $"PreCloseEntryCutoff:Minutes:{market} は 1〜{max} 分の整数で指定してください（指定値 {minutes}）。"
                    + "閉場前の新規建ての締め切りを外す値は持ちません（IADR-0561 決定 2）。");
            }
        }

        return this;
    }

    // FR-11, #1407, IADR-0564: 変更履歴の前後値に中身を残す（既定の ToString は辞書の型名しか出さない）。
    public override string ToString() => $"PreCloseEntryCutoffSettings {{ Minutes = {ControlFormatting.Minutes(Minutes)} }}";
}

/// <summary>
/// FR-10, FR-19, #1396, IADR-0561 決定 2: 審査へ渡す締め切りの供給（判定の瞬間・その市場の当日の閉場・分数）。判定コアは時刻を読まない
/// （ステートレス・純関数に保つ）。閉場は <c>MarketHours.SessionCloseOn</c>（休場日は null＝締め切りに入らない）。
/// </summary>
public sealed record PreCloseEntryCutoffSupply(DateTimeOffset Now, DateTimeOffset? SessionClose, int Minutes)
{
    /// <summary>閉場の <see cref="Minutes"/> 分前以降か（<see cref="PreCloseWindow.IsAtOrAfter"/>）。</summary>
    public bool IsWithinCutoff => PreCloseWindow.IsAtOrAfter(Now, SessionClose, Minutes);

    /// <summary>
    /// 🔴 #1433, IADR-0572 決定1: 効いている締め切り（<paramref name="cutoff"/>。<c>SessionAndThemeControlResolution.EffectivePreCloseEntryCutoff</c> の結果）から、
    /// その市場の <paramref name="now"/> の供給を組む。閉場は <c>MarketHours.SessionCloseOn</c>（休場日は null）。**審査（OrderScreeningService）と
    /// 新規建ての可否の口（EntryBlockersService）が同じこの関数を呼ぶ**（組み立てを 2 か所に書かない）。
    /// </summary>
    public static PreCloseEntryCutoffSupply For(PreCloseEntryCutoffSettings cutoff, Market market, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(cutoff);
        return new(now, MarketHours.SessionCloseOn(market, now), cutoff.MinutesFor(market));
    }
}

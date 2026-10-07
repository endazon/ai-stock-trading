using AiStockTrading.Shared.Contracts.Trading;

namespace AiStockTrading.Shared.Kernel.Trading;

/// <summary>
/// FR-10, FR-15, #1176, IADR-0495 決定3, #1209, IADR-0506: 1 つの<b>判断由来の決済</b>を取引日で表したもの。
/// </summary>
/// <param name="CloseSide">決済の売買方向。売りの決済はロング建玉を、買いの決済はショート建玉を閉じた。</param>
/// <param name="ApprovedOn">決済を承認した取引日（その市場の現地取引日）。</param>
/// <param name="FilledOn">決済が約定した取引日の列（約定なし・部分約定を含む）。</param>
public sealed record DecisionExitOnTradingDays(TradeSide CloseSide, DateOnly ApprovedOn, IReadOnlyList<DateOnly> FilledOn);

/// <summary>当日の判断由来の決済の有無（建玉の方向ごと）。</summary>
/// <param name="LongSide">ロング建玉（売りで決済した建玉）を当日に判断で決済したか。</param>
/// <param name="ShortSide">ショート建玉（買いで決済した建玉）を当日に判断で決済したか。</param>
public readonly record struct DecisionExitSides(bool LongSide, bool ShortSide);

/// <summary>
/// FR-10, FR-15, #1176, IADR-0495 決定3, #1209, IADR-0506: <b>判断由来の決済の後は、同じ取引日のうち同じ方向の新規建てをしない</b>の述語（純関数）。
/// <para>
/// 本番の審査・新規建ての可否の口（リスク管理の <c>DecisionExitProjection</c>。時刻を市場の現地取引日へ写してから本型へ委ねる）と、
/// Stage 0 の再生（バックテストの <c>RecordedDecisionReplayStrategy</c>。判断日と約定日をそのまま渡す）が<b>同じ規則</b>を通るために共有カーネルへ置く。
/// 2 か所に置くと片方だけが変わり、Stage 0 で合格した系と本番の系がずれる（計画 ADR-0054 決定3 の前提が崩れる）。
/// </para>
/// <para>
/// 数える規則: 決済の<b>承認または約定</b>の取引日が当日なら数える（約定を待たない）。決済の由来（判断由来か）・市場の絞り込みは呼び出し側が行う。
/// </para>
/// </summary>
public static class DecisionExitReentry
{
    /// <summary>当日（<paramref name="today"/>）の判断由来の決済を建玉の方向ごとに射影する。</summary>
    public static DecisionExitSides Project(IEnumerable<DecisionExitOnTradingDays> exits, DateOnly today)
    {
        ArgumentNullException.ThrowIfNull(exits);

        var longSide = false;
        var shortSide = false;
        foreach (var exit in exits)
        {
            var onToday = exit.ApprovedOn == today || exit.FilledOn.Contains(today);
            if (!onToday)
                continue;

            // 売りの決済はロング建玉を、買いの決済はショート建玉を閉じた。
            if (exit.CloseSide == TradeSide.Sell)
                longSide = true;
            else
                shortSide = true;
        }

        return new DecisionExitSides(longSide, shortSide);
    }

    /// <summary>
    /// 新規建て（<paramref name="entrySide"/>）と<b>同じ方向の建玉</b>を当日に判断で決済したか（真なら新規建てを止める）。
    /// 買いの新規建て＝ロングを建てる → ロングの決済を見る。売りの新規建て＝ショートを建てる → ショートの決済を見る。反対方向は止めない。
    /// </summary>
    public static bool BlocksEntry(bool longSide, bool shortSide, TradeSide entrySide) =>
        entrySide == TradeSide.Buy ? longSide : shortSide;
}

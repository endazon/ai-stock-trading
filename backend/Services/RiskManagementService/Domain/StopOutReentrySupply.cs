using AiStockTrading.Shared.Contracts.Trading;

namespace RiskManagementService.Domain;

/// <summary>
/// FR-10, #935, IADR-0394 決定6: ある銘柄を<b>その取引日のうちに損切りで手仕舞ったか</b>の 3 値。
/// <para>
/// 🔴 <b><see cref="None"/> と <see cref="Unknown"/> を混ぜない。</b> 由来が記録されていない決済が当日にあるとき、
/// それが損切りだったかは分からない。「分からない」を「損切りしていない」として通すと、統制が掛かっているように
/// 見えて実際には何も止めていない状態になる（#865 / #877 と同じ向き）。
/// </para>
/// </summary>
public enum StopOutStatus
{
    /// <summary>当日の損切りは無いと<b>確かめられた</b>（当日の決済が無い、または由来がすべて損切り以外）。</summary>
    None = 0,

    /// <summary>当日に損切り（S1 の発動・S0 の約定）で手仕舞った。</summary>
    StoppedOut = 1,

    /// <summary>当日に<b>由来が記録されていない</b>決済があり、損切りだったかを確かめられない。</summary>
    Unknown = 2,
}

/// <summary>
/// FR-10, #935, IADR-0394: 判定コア（<see cref="RiskEvaluator"/>）へ渡す、1 銘柄・当日の損切りの供給。
/// 建玉の方向ごとに持つ——ロングの損切り（売りの決済）が止めるのは<b>買いの新規建て</b>だけであり、
/// 反対方向（売りの新規建て＝ショート）は止めない（裁定が「同じ方向」と限定した）。
/// <para>
/// 組み立ては <c>StopOutProjection</c>（純関数）、供給は <c>OrderScreeningService</c> が行う
/// （強制買戻しの 30 日禁止〔<see cref="BuyInBanSupply"/>〕と同じ位置）。
/// </para>
/// </summary>
/// <param name="LongSide">ロング建玉（売りで決済した建玉）の当日の損切り状態。</param>
/// <param name="ShortSide">ショート建玉（買いで決済した建玉）の当日の損切り状態。</param>
public sealed record StopOutReentrySupply(StopOutStatus LongSide, StopOutStatus ShortSide)
{
    /// <summary>当日の損切りが無いと確かめられた（両方向とも <see cref="StopOutStatus.None"/>）。</summary>
    public static StopOutReentrySupply NoneToday { get; } = new(StopOutStatus.None, StopOutStatus.None);

    /// <summary>
    /// 新規建て（<paramref name="entrySide"/>）と<b>同じ方向の建玉</b>の損切り状態。
    /// 買いの新規建て＝ロングを建てる → ロングの損切りを見る。売りの新規建て＝ショートを建てる → ショートの損切りを見る。
    /// </summary>
    public StopOutStatus ForEntry(TradeSide entrySide) =>
        entrySide == TradeSide.Buy ? LongSide : ShortSide;
}

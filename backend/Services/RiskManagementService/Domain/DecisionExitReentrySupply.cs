using AiStockTrading.Shared.Contracts.Trading;
using AiStockTrading.Shared.Kernel.Trading;

namespace RiskManagementService.Domain;

/// <summary>
/// FR-10, #1176, IADR-0495 決定3: 判定コア（<see cref="RiskEvaluator"/>）へ渡す、1 銘柄・当日の<b>判断由来の決済</b>（利確・判断の手仕舞い）の供給。
/// <see cref="StopOutReentrySupply"/> と同じ形で、建玉の方向ごとに持つ——ロングを判断で決済した（売りの決済）なら止めるのは
/// <b>買いの新規建て</b>だけであり、反対方向（売りの新規建て）は止めない（裁定が「同じ方向」と限定した）。
/// <para>
/// 🔴 <b>不明の値を持たない。</b>由来が記録されていない当日の決済は損切りの供給（<see cref="StopOutStatus.Unknown"/>）が
/// 既に同じ方向を止めており、同じ行を 2 つの理由で数えない。
/// </para>
/// <para>組み立ては <c>DecisionExitProjection</c>（純関数）、供給は <c>OrderScreeningService</c> と <c>EntryBlockersService</c>。</para>
/// </summary>
/// <param name="LongSide">ロング建玉（売りで決済した建玉）を当日に判断で決済したか。</param>
/// <param name="ShortSide">ショート建玉（買いで決済した建玉）を当日に判断で決済したか。</param>
public sealed record DecisionExitReentrySupply(bool LongSide, bool ShortSide)
{
    /// <summary>当日の判断由来の決済は無い（両方向とも false）。</summary>
    public static DecisionExitReentrySupply NoneToday { get; } = new(false, false);

    /// <summary>
    /// 新規建て（<paramref name="entrySide"/>）と<b>同じ方向の建玉</b>を当日に判断で決済したか。
    /// 買いの新規建て＝ロングを建てる → ロングの決済を見る。売りの新規建て＝ショートを建てる → ショートの決済を見る。
    /// </summary>
    public bool ForEntry(TradeSide entrySide) =>
        // #1209, IADR-0507: 方向の規則は共有カーネル（Stage 0 の再生と同じ述語）。
        DecisionExitReentry.BlocksEntry(LongSide, ShortSide, entrySide);
}

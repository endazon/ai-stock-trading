using AiStockTrading.Shared.Contracts.Trading;
using AiStockTrading.Shared.Kernel.Trading;

namespace ReportService.Domain;

// FR-07, UC-03〜05, ADR-0003, IADR-0071 決定2: 無応答時の既定動作の設定。
// 対話的確定で提示したドラフト方針に、翌営業日開場までに応答（承認）が無かった場合の挙動。
public enum NoResponseBehavior
{
    /// <summary>既定: 直近の確定済み方針を継続する（確定を経ない新方針は自動適用しない・ADR-0003）。運用の連続性を優先。</summary>
    ContinueLastConfirmed,

    /// <summary>停止: 新方針を適用せず取引を止める（保守運用向け・設定で選択）。</summary>
    Halt,
}

// FR-07, IADR-0071 決定2: 無応答判定の結果。
public enum NoResponseOutcome
{
    /// <summary>期限内・承認待ち（無応答の状況ではない＝何もしない）。</summary>
    AwaitingApproval,

    /// <summary>直近の確定済み方針を継続する。</summary>
    ContinueLastConfirmed,

    /// <summary>取引を停止する。</summary>
    Halt,
}

// FR-07, UC-03〜05, ADR-0003, IADR-0071 決定2: 無応答時の既定動作を決める純関数（決定的・副作用なし）。
// 「確定前の方針は取引に適用されない」（未確定ドラフトは方針として返らない）ことは ReportAppService.GetConfirmedDailyPolicy が
// 構造的に担保する。本ポリシーは、提示済みで無応答のまま期限（翌営業日開場）を過ぎたときに、継続／停止のいずれへ倒すかを定める。
// 🔴 #1423 M-6, ADR-0075, IADR-0583（2026-10-24）: 結線した。DailyReportNoResponseEvaluator が本関数で判定し、
// 方針の照会へ停止の印を載せ（取引判断が全体を見送る）、常駐が無応答を通知する。
public static class ReportNoResponsePolicy
{
    /// <summary>
    /// 無応答時の挙動を決める。
    /// <paramref name="hasPendingReview"/> は提示済み（承認待ち）で未確定のレビューがあるか。
    /// 期限内は応答待ち、期限超過（now &gt;= deadline）は設定に従う。既定は継続。
    /// </summary>
    public static NoResponseOutcome Decide(
        DateTimeOffset now, DateTimeOffset deadline, bool hasPendingReview, NoResponseBehavior behavior)
    {
        // 承認待ちのレビューが無ければ定常状態＝直近の確定済み方針を継続する（現行動作）。
        if (!hasPendingReview)
            return NoResponseOutcome.ContinueLastConfirmed;

        // 期限内は応答待ち（新方針の適用も停止もしない。取引は既存の確定済み方針で継続する）。
        if (now < deadline)
            return NoResponseOutcome.AwaitingApproval;

        // 期限（翌営業日開場）を過ぎても無応答: 設定に従う。既定は継続（安全側・運用の連続性）。
        return behavior == NoResponseBehavior.Halt
            ? NoResponseOutcome.Halt
            : NoResponseOutcome.ContinueLastConfirmed;
    }

    /// <summary>
    /// 🔴 FR-07, #1423 M-6, ADR-0075 決定1, IADR-0583 決定1: 日報 <paramref name="periodStart"/> の応答の期限（「翌営業日の開場」）。
    /// その日報の生成境界（<see cref="ReportScheduleOptions.DailyAt"/>・JST）より<b>後</b>の最初の<b>米国市場</b>の開場である
    /// （日本株は無効。ADR-0062）。休場日・半日取引日・夏時間は共有カーネルの規則計算に従う。走査の上限内に開場が無ければ null（期限なし）。
    /// </summary>
    public static DateTimeOffset? DeadlineOf(DateOnly periodStart, ReportScheduleOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var boundary = new DateTimeOffset(periodStart.ToDateTime(options.DailyAt), ReportSchedule.JstOffset);
        return MarketHours.NextOpen(Market.UnitedStates, boundary);
    }

    /// <summary>構成値（Reports:NoResponseBehavior）を解釈する。未設定・空・不正値は既定（継続）へ倒す（fail-safe）。</summary>
    public static NoResponseBehavior ParseBehavior(string? value) =>
        Enum.TryParse<NoResponseBehavior>(value, ignoreCase: true, out var behavior)
            ? behavior
            : NoResponseBehavior.ContinueLastConfirmed;
}

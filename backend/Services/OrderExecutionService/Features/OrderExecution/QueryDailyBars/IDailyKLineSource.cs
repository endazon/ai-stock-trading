namespace OrderExecutionService.Features.OrderExecution.QueryDailyBars;

// FR-04, FR-15, ADR-0048 決定 2・3, ADR-0023 決定 5, #1118, IADR-0467 決定 1: 判断へ渡す出来高（と #1122 の ATR(14)）のための、
// moomoo の米国株の日足 K 線を**前復権で 1 回だけ**取る読み取り専用ポート。
//
// 🔴 **復権区分は前復権に固定する**（引数に取らない）。#1117 の実測で前復権は出来高も分割で調整する（NVDA 10:1 の前後で forward/none が
// 10 → 1）。1 回の取得の中の足は同じ基準で揃うため、20 日平均比が分割をまたいで歪まない。無復権の足を混ぜる経路を型の上に置かない。
// 🔴 **1 呼び出しにつき OpenD へ日足の要求を 1 回だけ**撃つ（再試行・ページングのループを持たない）。続きの鍵が返れば不完全として失敗にする。
// 取得の直後に取得枠を照会し（枠は減らない照会）、使用数と残りを結果に載せる（計器で追うため）。
// 通信の失敗（接続・切断・打ち切り）は例外で返す。非成功（retType ≠ 0）は例外にせず失敗の結果で返す。
public interface IDailyKLineSource
{
    Task<DailyKLineFetch> FetchForwardAdjustedAsync(
        string symbol, DateOnly from, DateOnly to, CancellationToken cancellationToken = default);
}

// 日足の取得の結果。Succeeded が false なら Bars は空で FailureReason に理由（`ret-<retType>` / `has-more`）。
// QuotaUsed / QuotaRemaining は取得の直後に照会した取得枠（照会に失敗・欄が無ければ null）。
public sealed record DailyKLineFetch(
    bool Succeeded,
    IReadOnlyList<DailyBarView> Bars,
    string? FailureReason,
    int? QuotaUsed,
    int? QuotaRemaining);

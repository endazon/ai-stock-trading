using AiStockTrading.Report.Application.Ports;
using AiStockTrading.Shared.Contracts.Events;

namespace AiStockTrading.Report.Application.Adapters;

// FR-10, FR-06, UC-06, ADR-0016 決定15（2026-08-06 追記）, #419, IADR-0159 決定3:
// 強制買戻し（推定）の記録供給の既定＝**未供給（null）**。
//
// **空列（＝推定 0 件）へ倒してはならない。** 推定そのものはリスク管理サービスで動くが、**報告書サービスから
// 推定台帳を照会する経路が無い**（権威源への照会 API が未実装）。したがって報告書は「起きていない」ことを
// 知らない。決定15 は「推定経路が入るまで発生回数は供給されない。**供給が無い間は 0 件と表示してはならない**
// （『強制買戻しは起きていない』に見えるため）」と明記している。
//
// 自動縮小の既定（NoMarginReductionRecordSource が空列＝発動なしを返す）と**向きが違う**ことに注意する——
// あちらは発火元が存在せず「発動なし」が事実として正しいが、こちらは**推定経路が実在し発火し得る**。
// 事実として正しくない値を既定にしない。
public sealed class UnsuppliedBuyInInferenceRecordSource : IBuyInInferenceRecordSource
{
    public Task<IReadOnlyList<BuyInInferred>?> GetInferencesAsync(
        DateOnly fromInclusive,
        DateOnly toInclusive,
        CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<BuyInInferred>?>(null);
}

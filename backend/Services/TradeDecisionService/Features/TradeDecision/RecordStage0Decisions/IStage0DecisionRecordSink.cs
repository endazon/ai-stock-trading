using AiStockTrading.Shared.Contracts.Backtest;

namespace TradeDecisionService.Features.TradeDecision.RecordStage0Decisions;

// FR-15, ADR-0033 決定2/決定5, #632, IADR-0318: 記録集合の書き出しポート。
//
// 🔴 **途中までの記録も保存できる形にする**（ADR-0033 決定5「実行中に見積り額を超えたら停止して報告する」）。
// 超過で打ち切ったときに何も残らなければ、消費した費用に対して得るものが無い。
public interface IStage0DecisionRecordSink
{
    /// <summary>記録集合を書き出す。**失敗は例外にせず false**（呼び出し元が報告する）。</summary>
    Task<bool> SaveAsync(Stage0DecisionRecordSet recordSet, CancellationToken cancellationToken = default);
}

// 安全既定: 書き出さない（＝記録が残らない）。記録器はこの実装のとき実行しない
// —— 費用だけ消費して記録が残らない実行を作らないためである。
public sealed class NoStage0DecisionRecordSink : IStage0DecisionRecordSink
{
    public Task<bool> SaveAsync(Stage0DecisionRecordSet recordSet, CancellationToken cancellationToken = default) =>
        Task.FromResult(false);
}

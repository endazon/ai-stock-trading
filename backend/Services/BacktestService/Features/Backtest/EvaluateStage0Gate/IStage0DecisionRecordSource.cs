using AiStockTrading.Shared.Contracts.Backtest;

namespace BacktestService.Features.Backtest.EvaluateStage0Gate;

// FR-04, FR-15, ADR-0033 決定2, #632, IADR-0318: AI 判断の記録集合を供給するポート。
//
// 記録は取引判断サービス（LLM 客・プロンプト・費用計測がそこにある）が作り、本サービスは**読むだけ**である。
// 実体はファイル（`Backtest:Stage0:Recording:Path`）であり、**取得は非同期・失敗し得る I/O** なので、
// 純関数である `IBacktestStrategy` の外側に置く（IADR-0105 が `IHistoricalBarSource` を分けたのと同型）。
public interface IStage0DecisionRecordSource
{
    /// <summary>記録集合を読む。**記録が無い・読めない・解釈できないときは null**（呼び出し元は fail-closed へ倒す）。</summary>
    Task<Stage0DecisionRecordSet?> LoadAsync(CancellationToken cancellationToken = default);
}

// FR-15, ADR-0033, IADR-0318: 安全既定の実装。**常に「記録なし」を返す**（外部へ 1 リクエストも出さず、
// ファイルも読まない）。記録の供給を明示的に構成するまで、記録再生戦略は評価対象を持たない。
public sealed class NoStage0DecisionRecordSource : IStage0DecisionRecordSource
{
    public Task<Stage0DecisionRecordSet?> LoadAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult<Stage0DecisionRecordSet?>(null);
}

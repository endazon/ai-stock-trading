using BacktestService.Domain;
using AiStockTrading.Shared.Contracts.Trading;

namespace BacktestService.Application;

// FR-15, ADR-0004, #208, IADR-0105: 実過去データ源（外部 API）からの日足バー取得の抽象。
//
// 同期・純粋な IBarDataSource（評価側）とは別のポートにする。実データ取得は非同期・失敗し得る I/O であり、
// シミュレーションの外側で 1 回だけ行って結果をスナップショット（MaterializedBarDataSource）に固定する。
// これによりウォークフォワードの分割ごと・試行ごとの再取得で結果が揺れることが構造的に起きない（決定性の保全）。
public interface IHistoricalBarSource
{
    /// <summary>指定銘柄・期間 [from, to] の日足バーを取得する。取得できなかった銘柄は Gaps に残す。</summary>
    Task<HistoricalBarLoad> LoadBarsAsync(
        IReadOnlyList<(string Symbol, Market Market)> symbols,
        DateOnly from,
        DateOnly to,
        CancellationToken cancellationToken = default);
}

// FR-15, #208: 取得結果。欠測を無音破棄せず銘柄と理由で残す（BacktestRun.UnfilledOrderCount と同じ方針・#99 レビュー指摘）。
// 欠測のまま Stage 0 を合格させないための判断材料であり、バーが 0 本でも「取れなかった」ことが呼び出し元に残る。
public sealed record HistoricalBarLoad(IReadOnlyList<PriceBar> Bars, IReadOnlyList<HistoricalBarGap> Gaps);

// FR-15, #208: 取得できなかった銘柄と理由（非成功応答・データなし・解析不能・銘柄記法へ写像不能）。
public readonly record struct HistoricalBarGap(string Symbol, Market Market, string Reason);

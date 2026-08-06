using AiStockTrading.Backtest.Application;
using AiStockTrading.Shared.Contracts.Trading;
using Microsoft.Extensions.Logging;

namespace AiStockTrading.Backtest.Infrastructure.Composable.Adapters;

// FR-15, #208, IADR-0105: IHistoricalBarSource の安全既定＝外部へ一切接続せず常に空を返す。
// 実過去データ源は provider の明示指定（Backtest:BarData:Provider）でのみ有効化する（opt-in）。
// バーが 0 本なら Stage 0 は DSR・コスト2倍・ウォークフォワードで落ちるため、昇格は従来どおり拒否される（fail-safe）。
// ADR-0023 決定5, IADR-0156, IADR-0157, #382: 警告の意味は「差し替え漏れ（設定すれば Stooq が使える）」ではない。
// Stooq は取得不能（回避実装は禁止・ADR-0023 決定1）であり、**使えるのは決定5 で採用された moomoo だけ**である。
// ただし決定5 は「実装側で確認を要する 2 点」（取得枠の単位と回復周期／前復権と ADR-0016 決定14 の費用モデルの
// 整合）を本決定の前提としており、**確認が済むまで本番のバックテストへ流さない**。よって既定 none は今も有効な
// 状態であり、その間 Stage 0 の合格判定は発火しない。
// 抑止の単位は**インスタンス**であり、取得のたびのログ氾濫を防ぐ
// （NoOpMarketDataSource・IADR-0066 と同型）。ホスト（BacktestService.Worker/Program.cs）は
// IHistoricalBarSource を singleton で登録するため、実プロセスでは起動後 1 回に収まる
// （singleton であることは BacktestWorkerWiringTests が固定している。レート予算の観点でも singleton が要る）。
public sealed class NoOpHistoricalBarSource(ILogger<NoOpHistoricalBarSource> logger) : IHistoricalBarSource
{
    private int _warned;

    public Task<HistoricalBarLoad> LoadBarsAsync(
        IReadOnlyList<(string Symbol, Market Market)> symbols,
        DateOnly from,
        DateOnly to,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(symbols);

        if (Interlocked.Exchange(ref _warned, 1) == 0)
        {
            logger.LogWarning(
                "NoOpHistoricalBarSource を使用中: 実過去データ源が未接続のためバックテストのバーを取得できません。" +
                "Stage 0 は不合格となり Stage 1 への昇格は行われません（IADR-0105）。" +
                "米国株日足 OHLC の履歴源は ADR-0023 決定5 で moomoo に決まり実装もありますが" +
                "（Backtest:BarData:Provider=moomoo）、**取得枠の単位と回復周期／前復権と ADR-0016 決定14 の" +
                "費用モデルの整合という未確認の 2 点が済むまで本番のバックテストへ流さないこと**が同決定の前提です" +
                "（IADR-0157・docs/blocked-tasks.md A-3）。Stooq は取得不能のままです（ADR-0023 決定1・回避実装は行いません）。");
        }

        // 未取得も無音にしない（provider 未設定と「データ源にデータが無い」を呼び出し元が区別できる必要はないが、
        // 「0 本のバーで合格した」ように見えないよう欠測として残す）。
        var gaps = symbols
            .Select(s => new HistoricalBarGap(s.Symbol, s.Market, "実過去データ源が未接続（provider=none）"))
            .ToList();

        return Task.FromResult(new HistoricalBarLoad([], gaps));
    }
}

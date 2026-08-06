using AiStockTrading.Backtest.Application;
using AiStockTrading.Shared.Contracts.Trading;
using Microsoft.Extensions.Logging;

namespace AiStockTrading.Backtest.Infrastructure.Composable.Adapters;

// FR-15, #208, IADR-0105: IHistoricalBarSource の安全既定＝外部へ一切接続せず常に空を返す。
// 実過去データ源は provider の明示指定（Backtest:BarData:Provider）でのみ有効化する（opt-in）。
// バーが 0 本なら Stage 0 は DSR・コスト2倍・ウォークフォワードで落ちるため、昇格は従来どおり拒否される（fail-safe）。
// ADR-0023, IADR-0156, #382: 警告の意味は「差し替え漏れ（設定すれば使える）」ではなく **差し替え先が無い**
// である。実装済みの履歴源は Stooq のみでそれは取得不能（回避実装は禁止・ADR-0023 決定1）、代替源 moomoo は
// 実測されたが採用も実装も未了。よって本 no-op は恒久の状態であり、Stage 0 の合格判定は一度も発火し得ない。
// それでも警告は残す（「有効化したつもりで効いていない」構成不備は依然として起こり得るため）。
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
                "これは設定漏れではなく**差し替え先が無い**状態です: 実装済みの履歴源は Stooq のみで現在取得不能" +
                "（ADR-0023 決定1・回避実装は行いません）、代替源 moomoo は実測済みですが採用（計画側の裁定）も" +
                "実装も未了です（IADR-0156・docs/blocked-tasks.md）。");
        }

        // 未取得も無音にしない（provider 未設定と「データ源にデータが無い」を呼び出し元が区別できる必要はないが、
        // 「0 本のバーで合格した」ように見えないよう欠測として残す）。
        var gaps = symbols
            .Select(s => new HistoricalBarGap(s.Symbol, s.Market, "実過去データ源が未接続（provider=none）"))
            .ToList();

        return Task.FromResult(new HistoricalBarLoad([], gaps));
    }
}

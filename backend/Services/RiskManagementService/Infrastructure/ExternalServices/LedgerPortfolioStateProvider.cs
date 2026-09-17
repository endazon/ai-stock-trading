using RiskManagementService.Features.RiskManagement;
using RiskManagementService.Common.Abstractions;
using RiskManagementService.Domain;

namespace RiskManagementService.Infrastructure.ExternalServices;

// FR-10, FR-05, IADR-0018: 取引台帳からの純射影で PortfolioState を供給する IPortfolioStateProvider 実装。
// PlaceholderPortfolioStateProvider を置き換える。基準資金は TradingDefaults.InitialCapital（既存基準と同一）。
//
// #81, IADR-0066: currentPrices を注入すると含み損益・DD を時価で算出する。未注入（既定 null）では従来どおり
// 含み 0・DD 0 のまま＝現行挙動を保つ（Worker 側の MarketData:EnableMarkToMarket が既定 false のため既定は未注入）。
// #257, IADR-0108: 基準資金は注入できるようにする（既定＝TradingDefaults.InitialCapital＝現行等価）。
// SIMULATE 限定プロファイル（Risk:SimulatorProfile:Enabled）有効時のみ、ホストがシミュレータ残高相当を渡す。
//
// FR-10, #829, IADR-0346 決定3: 承認済みで生きている新規建て注文（IWorkingEntryOrderSource）を射影へ渡し、
// 日次発注累計・段階資金・保有建玉数へ算入させる。**必須依存である**（IADR-0163 決定2——省略可能にすると
// 配線を削ってもコンパイルが通り、未約定の算入だけが静かに外れて #829 の穴が戻る）。
public sealed class LedgerPortfolioStateProvider(
    IPortfolioLedgerStore ledger,
    IWorkingEntryOrderSource workingEntryOrders,
    IClock clock,
    ICurrentPriceSource? currentPrices = null,
    decimal? initialCapital = null)
    : IPortfolioStateProvider
{
    // IADR-0346 決定2: 注文源の走査の下限。当日の判定は Project が市場の現地取引日で行うため、下限は
    // 「どの市場の当日も取りこぼさない」幅であればよい（走査量の上限にすぎない）。
    private static readonly TimeSpan WorkingEntryLookback = TimeSpan.FromDays(2);

    private readonly decimal _initialCapital = initialCapital ?? TradingDefaults.InitialCapital;

    public PortfolioState GetCurrent()
    {
        var now = clock.UtcNow;
        var fills = ledger.GetFills();
        var working = workingEntryOrders.GetWorkingEntryOrders(now - WorkingEntryLookback);

        if (currentPrices is null)
            return PortfolioProjection.Project(fills, now, _initialCapital, workingEntries: working);

        var prices = currentPrices.GetCurrentPrices(PortfolioProjection.ProjectOpenPositions(fills));
        var state = PortfolioProjection.Project(fills, now, _initialCapital, prices, workingEntries: working);

        // 現在エクイティ＝当日開始基準（初期資金＋当日より前の実現）＋当日実現＋含み（Project の定義と同一）。
        // ピークは台帳から再計算し（IADR-0066）、DD だけを差し替える（Project をもう一度走らせない）。
        // 有効時は台帳を 3 回畳み込む（建玉射影・射影・ピーク）。1 回に畳むには Project がピークも返す必要があり、
        // 射影の責務に DD の入力を混ぜることになるため採らない。台帳はメモリ上の小さな列で O(n) のため許容する。
        var equity = state.Capital + state.DailyRealizedPnl + state.UnrealizedPnl;
        var highWaterMark = PortfolioValuation.EquityHighWaterMark(fills, _initialCapital, equity);

        return state with { DrawdownRatio = PortfolioValuation.DrawdownRatio(highWaterMark, equity) };
    }
}

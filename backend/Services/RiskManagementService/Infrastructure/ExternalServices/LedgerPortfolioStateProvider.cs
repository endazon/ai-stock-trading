using RiskManagementService.Features.RiskManagement;
using RiskManagementService.Common.Abstractions;
using RiskManagementService.Domain;

namespace RiskManagementService.Infrastructure.ExternalServices;

// FR-10, FR-05, IADR-0018: 取引台帳からの純射影で PortfolioState を供給する IPortfolioStateProvider 実装。
// PlaceholderPortfolioStateProvider を置き換える。
//
// 🔴 FR-10, #869, ADR-0041 決定2, IADR-0354: **本プロバイダは統制上限の基準資金（equity）を供給しない。**
// 供給元はブローカーの口座照会（ICapitalBaselineStore）である。ここで `initialCapital` と呼んでいるのは
// **ドローダウンのエクイティ系列の起点**（LedgerEquity の基点。IADR-0066）であって統制の分母ではない。
//
// #81, IADR-0066: currentPrices を注入すると含み損益・DD を時価で算出する。未注入（既定 null）では従来どおり
// 含み 0・DD 0 のまま＝現行挙動を保つ（Worker 側の MarketData:EnableMarkToMarket が既定 false のため既定は未注入）。
// #257, IADR-0108: **DD 系列の起点**は注入できるようにする（既定＝TradingDefaults.InitialCapital＝現行等価）。
// SIMULATE 限定プロファイル（Risk:SimulatorProfile:Enabled）有効時のみ、ホストがシミュレータ残高相当を渡す。
// #869: これは統制の基準資金ではないため、注入しても **equity 比の 4 上限**（1 注文 25% / 1 日 150% /
// 段階の総資金比 / 日次損失 2%）は 1 つも動かない。
// 🔴 **ただし DD は動く。** `DrawdownRatio = (peak − equity) / peak` は起点の平行移動に不変ではない——
// 損失 10,000 のとき起点 100,000 なら DD 0.10、起点 20,000 なら DD 0.50 であり、既定の
// `MaxDrawdownRatio = 0.10` に対して `MaxDrawdownReached` の成否が実際に入れ替わる。
// **#257 / IADR-0108 の注入はそのために在る**（SIMULATE の残高規模で DD を意味のある値にする）。
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

        // 現在エクイティ＝台帳由来のエクイティ（Project の LedgerEquity と同一の定義）。
        // 🔴 #869 / ADR-0041 決定2: **統制上限の基準資金ではない**（基準資金は ICapitalBaselineStore が供給する）。
        // ピークは台帳から再計算し（IADR-0066）、DD だけを差し替える（Project をもう一度走らせない）。
        // 有効時は台帳を 3 回畳み込む（建玉射影・射影・ピーク）。1 回に畳むには Project がピークも返す必要があり、
        // 射影の責務に DD の入力を混ぜることになるため採らない。台帳はメモリ上の小さな列で O(n) のため許容する。
        var equity = state.LedgerEquity;
        var highWaterMark = PortfolioValuation.EquityHighWaterMark(fills, _initialCapital, equity);

        return state with { DrawdownRatio = PortfolioValuation.DrawdownRatio(highWaterMark, equity) };
    }
}

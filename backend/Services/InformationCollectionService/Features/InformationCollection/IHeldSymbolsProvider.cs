using AiStockTrading.Shared.Contracts.Trading;

namespace InformationCollectionService.Features.InformationCollection;

// 🔴 FR-01, FR-02, #1363, IADR-0547 決定 1: 収集の対象へ足す**保有中の銘柄**の供給。取引判断は「監視銘柄 ∪ 保有中の銘柄」を判断する
// （#1286 / IADR-0521 決定 1）。収集が監視銘柄だけだと、監視銘柄の外の保有銘柄は**その銘柄のニュース・開示なしで出口を判断する**
// （PoC 2026-10-14: 「参考情報に GOOGL 固有のニュースはなく」の類が 32 件）。
//
// 形は取引判断の同名の口（TradeDecisionService の IHeldSymbolsProvider）に倣う。読む口も同じ（リスク管理の
// GET /risk-controls/open-positions・OwnerOrService）。サービス間は直接参照しないため、型と行の検証は本サービスに持つ。
public interface IHeldSymbolsProvider
{
    // 保有数量（符号付きの合計）が 0 でない (銘柄, 市場) の一覧。保有が無ければ空。
    // 🔴 照会できない・応答を解釈できないときは **null（不明）**。不明の巡回は監視銘柄だけから対象を決める
    // （IADR-0521 決定 1 と同じ縮退。保有の不明を理由に監視銘柄の収集まで止めない）。
    Task<IReadOnlyList<WatchedSymbol>?> GetHeldSymbolsAsync(CancellationToken cancellationToken = default);
}

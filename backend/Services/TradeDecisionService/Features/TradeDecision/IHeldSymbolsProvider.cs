namespace TradeDecisionService.Features.TradeDecision;

// 🔴 FR-02, FR-04, UC-01, ADR-0051, #1286, IADR-0521 決定 1: 定時サイクルの判断対象へ足す**保有中の銘柄**の供給。
// UC-01 基本フロー 3 は「日報の方針・保有ポジション・収集情報・過去判断」を文脈に判断するとしており、判断の対象を監視銘柄に
// 限っていない。従来の巡回は監視銘柄だけだったため、監視銘柄の外の保有銘柄は LLM の手仕舞い・利確の対象にならず、
// 出口が損切り（S1）だけになっていた（PoC 2026-10-09。利用者裁定「保有銘柄も判断対象に」）。
//
// 実装は保有照会（IHeldPositionProvider）と同じ口（リスク管理の open-positions）を読み、行の検証も同じ規則に従う。
public interface IHeldSymbolsProvider
{
    // 保有数量（符号付きの合計）が 0 でない (銘柄, 市場) の一覧。保有が無ければ空。
    // 🔴 照会できない・未結線・応答を解釈できないときは **null（不明）**。null のサイクルは監視銘柄だけを判断する
    // （従来の巡回と同じ。保有の不明を理由に監視銘柄の判断まで止めない）。
    Task<IReadOnlyList<WatchedSymbol>?> GetHeldSymbolsAsync(CancellationToken cancellationToken = default);
}

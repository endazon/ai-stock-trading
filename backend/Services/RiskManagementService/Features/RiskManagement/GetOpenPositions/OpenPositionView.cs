using AiStockTrading.Shared.Contracts.Trading;

namespace RiskManagementService.Features.RiskManagement.GetOpenPositions;

// FR-03, FR-10, IADR-0030: 市場監視（#10）へ公開する保有ポジション。市場監視の HeldPosition と同形
// （Symbol・Market・Side・Quantity・EntryPrice・StopLossPrice）で JSON 往復する。StopLossPrice は
// 保有中のエントリーのうち最も保護的な損切り価格（#936, IADR-0393）。記録を持たないエントリーは
// 既定損切り比率を平均取得単価へ適用した近似値で見積もる（OpenPositionsService が導出）。
// FR-04, FR-10, #1384, IADR-0557 決定 6: Entries は保有中のエントリー（ロット）を古い順に並べたもの（建てた判断・残数量・承認時刻・
// 建てた条件の記号と基準価格）。取引判断が保有中の判断のプロンプトへ渡す。市場監視・情報収集・報告書は読まない（追加の項目は無視される）。
// 🔴 受け手は「項目が無い」（本項目の追加前の送り手）を「ロットが無い」と読まない（記録の供給なし＝不明）。
public sealed record OpenPositionView(
    string Symbol,
    Market Market,
    TradeSide Side,
    int Quantity,
    decimal EntryPrice,
    decimal StopLossPrice,
    IReadOnlyList<OpenPositionEntryView>? Entries = null);

/// <summary>
/// FR-04, FR-10, #1384, IADR-0557 決定 6: 保有中のエントリー（ロット）1 件。<paramref name="DecisionId"/> は建てた判断（相関できない行は
/// <c>Guid.Empty</c>）、<paramref name="Quantity"/> は残数量、<paramref name="OrderedAt"/> は承認時刻（分からなければ約定時刻）。
/// <paramref name="EntryCondition"/>・<paramref name="EntryReferencePrice"/> は建てた条件の記号と基準価格（ローカル通貨。null＝記録なし）。
/// </summary>
public sealed record OpenPositionEntryView(
    Guid DecisionId,
    int Quantity,
    DateTimeOffset OrderedAt,
    string? EntryCondition,
    decimal? EntryReferencePrice);

using AiStockTrading.Shared.Contracts.Trading;

namespace AiStockTrading.Shared.Contracts.Events;

// 🔴 FR-10, FR-11, ADR-0049, #1136, IADR-0472 決定5: ソフトウェア逆指値（S1）の損切りラインを、損切り幅の下限まで**遡及して広げた**事実。
// 下限の導入（#1120・IADR-0465）より前に建てた建玉のラインは下限を割っていた（9/30 の NVDA 1.84%・AMZN 1.16% 等）。
// 発注執行の常駐ガードが巡回の先頭で、Active・未到達の S1 の行に「取得単価 × (1 − 下限)」（空売りは ＋）を当て、
// **広げる向きのときだけ**ラインを書き換えて本事実を 1 件出す（冪等。2 回目以降は書かず、出さない）。
//
// - EntryPrice: 取得単価（エントリーの発注記録の平均約定価格）。下限の基準。
// - PreviousStopLossPrice / StopLossPrice: 旧ライン / 新ライン（丸めない。S1 は数値で比べる）。
// - FloorPerShare / FloorSource: 下限（1 株あたり・ローカル通貨）と出所。発注執行は ATR を持たないため、今は常に Fallback2Pct。
//
// 購読: 監査台帳（記録）・リスク管理（取引台帳の承認行のラインを広げる向きにだけ追随させ、市場監視の到達を新ラインで出させる）。通知はしない。
public record SoftwareStopLineWidened(
    Guid EntryDecisionId,
    string Symbol,
    Market Market,
    TradeSide EntrySide,
    decimal EntryPrice,
    decimal PreviousStopLossPrice,
    decimal StopLossPrice,
    decimal FloorPerShare,
    StopWidthFloorSource FloorSource,
    DateTimeOffset OccurredAt);

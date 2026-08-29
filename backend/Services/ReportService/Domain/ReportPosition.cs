using AiStockTrading.Shared.Contracts.Trading;

namespace ReportService.Domain;

// FR-06, FR-16, 04_report-templates 日報 §3「ポジション一覧（当日終了時点）」の 1 建玉。
// #563, IADR-0268: 権威源はリスク管理の取引台帳の射影（GET /risk-controls/open-positions）である。
//
// 🔴 **nullable は「未供給」であり 0 ではない。**
//   - `CurrentPrice` / `UnrealizedPnl`: 現在値を引けなかった銘柄（評価損益 0 と書かない）。
//   - `BorrowFeeTotal`: 借株料の**建玉開始からの累計**の記録源が無い（期間の計上額は別物であり載せない）。
//   - `HoldingDays`: 射影は建玉の開始時刻を持たない（期間内の約定だけから数えると実際より短く出る）。
//
// `StopLossPrice` は供給元が権威データ（取引判断が決めた値）を優先し、無い建玉では既定比率から近似導出する。
// 近似か実値かは応答から区別できないため、レンダラの凡例でその旨を明記する。
public sealed record ReportPosition(
    Market Market,
    string Symbol,
    TradeSide Side,
    int Quantity,
    decimal AverageEntryPrice,
    decimal StopLossPrice,
    decimal? CurrentPrice,
    decimal? UnrealizedPnl,
    decimal? BorrowFeeTotal,
    int? HoldingDays);

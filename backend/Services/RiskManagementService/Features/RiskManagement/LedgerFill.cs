using AiStockTrading.Shared.Contracts.Trading;

namespace RiskManagementService.Features.RiskManagement;

// FR-10, FR-05, IADR-0018: 取引台帳の 1 約定（承認 Intent と OrderExecuted を DecisionId で相関して補完済み）。
// 射影（PortfolioProjection）の入力。銘柄・市場・約定方向・建玉効果・約定数量・約定単価・約定時刻を持つ。
// Quantity は約定数量（>0）、Price は約定単価（**銘柄の市場の通貨**＝ローカル通貨。IADR-0107 決定1）。
// StopLossPrice は承認 Intent 由来の損切り価格（IADR-0035・nullable＝レガシー/機械執行 Close は null）。
// FxRateToBase は承認 Intent 由来の基準通貨（USD）への換算レート（IADR-0107 決定2＝約定時レートの近似）。
// 既定 1＝基準通貨市場（米国株）。金額集計はこのレートで基準通貨へ揃える（#364・IADR-0152 決定1）。
// FR-06, FR-16, #563, IADR-0268: DecisionId は承認・約定を結ぶ相関キー（台帳は TradeFillRow に保持している）。
// 報告書の日報 §2「判断根拠（要約）」が、監査台帳の TradeDecisionMade を**この鍵で**引くために公開する。
// 既定 `default`（Guid.Empty）＝相関できない（レガシー行）。**推測で埋めない**——判断根拠は未供給になる。
public sealed record LedgerFill(
    string Symbol,
    Market Market,
    TradeSide Side,
    PositionEffect PositionEffect,
    int Quantity,
    decimal Price,
    DateTimeOffset ExecutedAt,
    decimal? StopLossPrice = null,
    decimal FxRateToBase = 1m,
    Guid DecisionId = default)
{
    /// <summary>基準通貨（USD）建ての約定単価。金額集計・実現損益・エクイティはこの単価で積む。**永続化しない計算値**である。</summary>
    public decimal PriceInBase => Price * FxRateToBase;
}

using AiStockTrading.Shared.Contracts.Trading;

namespace RiskManagementService.Features.RiskManagement;

// FR-10, FR-05, IADR-0018: 取引台帳の 1 約定（承認 Intent と OrderExecuted を DecisionId で相関して補完済み）。
// 射影（PortfolioProjection）の入力。銘柄・市場・約定方向・建玉効果・約定数量・約定単価・約定時刻を持つ。
// Quantity は約定数量（>0）、Price は約定単価（**銘柄の市場の通貨**＝ローカル通貨。IADR-0107 決定1）。
// StopLossPrice は承認 Intent 由来の損切り価格（IADR-0035・nullable＝レガシー/機械執行 Close は null）。
// FxRateToBase は承認 Intent 由来の基準通貨（USD）への換算レート（IADR-0107 決定2＝約定時レートの近似）。
// 既定 1＝基準通貨市場（米国株）。金額集計はこのレートで基準通貨へ揃える（#364・IADR-0152 決定1）。
// FR-06, FR-16, #563, IADR-0269: DecisionId は承認・約定を結ぶ相関キー（台帳は TradeFillRow に保持している）。
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
    Guid DecisionId = default,
    // FR-06, FR-15, FR-20, #569, IADR-0149 決定1, IADR-0271: **実際に発注したアダプタの発注先**。
    // 月報 §5 の三者比較（バックテスト / SIMULATE / 実弾）が段を分ける鍵である。
    // **既定 null＝発注先不明（本列の追加前に記録された行）。推定で埋めない**——
    // 不明の約定はどちらの段にも算入しない。
    BrokerProvider? Provider = null,
    // FR-06, FR-16, #611, IADR-0286 決定1: 承認時点の**認識時レート**（基準通貨〔USD〕1 単位あたりの表示通貨〔JPY〕額
    // ＝1 USD あたりの円）。報告書の為替差損益（認識時レートと期末レートの差）の根。FxRateToBase（ローカル通貨→USD）
    // とは軸が違い、米国株では後者が 1 で円の情報を持たない。
    // **既定 null＝未記録（列追加前の行・承認時に為替レート源が解決できなかった行）。推定で埋めない**——
    // 報告書は当該約定を含む期間の為替差損益を未供給とし、未記録の件数を明記する。
    decimal? FxRateBaseToDisplay = null)
{
    /// <summary>基準通貨（USD）建ての約定単価。金額集計・実現損益・エクイティはこの単価で積む。**永続化しない計算値**である。</summary>
    public decimal PriceInBase => Price * FxRateToBase;
}

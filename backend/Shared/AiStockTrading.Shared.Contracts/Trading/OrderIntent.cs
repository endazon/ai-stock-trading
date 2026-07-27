namespace AiStockTrading.Shared.Contracts.Trading;

// FR-04, FR-05: 取引判断サービスが生成する注文意図。リスク管理の検証を経て発注執行へ渡る。
// Price は**銘柄の市場の通貨（ローカル通貨）**の参照価格で、発注執行がブローカーへ送る注文価格の権威である
// （IADR-0106 決定1。moomoo アダプタは本値をそのまま注文価格に用いるため、円換算値を入れてはならない）。
// PositionEffect（建玉効果）はエントリー/手仕舞いを表し、既定は Open（新規建て）。エントリー専用の
// リスク統制の適用可否はこの値で判定する（IADR-0004）。既定を Open とすることで、効果未指定の注文は
// 制約を厳しく掛ける安全側に倒れる。
// FR-03/04/10, IADR-0035: StopLossPrice は取引判断が決めた損切り価格（ATR 連動・Price と同じローカル通貨）。#63 台帳へ
// 永続化され、市場監視の損切りライン検知（IADR-0030）に実値として供給される。後方互換のため nullable
//（既定 null＝機械執行の Close 等では該当なし）。
// FR-10, FR-17, #257, IADR-0106: FxRateToBase は「ローカル通貨 1 単位あたりの基準通貨（円）額」。取引判断が
// 意図生成時（＝約定時レートの近似・計画 05_trading-assumptions §3）に確定して同伴させ、統制・台帳は
// NotionalInBase で基準通貨の金額を判定する。既定 1＝基準通貨市場（日本株）およびレート未記録の既存データと等価。
public record OrderIntent(
    string Symbol,
    Market Market,
    TradeSide Side,
    ProductType ProductType,
    TradeMode Mode,
    int Quantity,
    decimal Price,
    PositionEffect PositionEffect = PositionEffect.Open,
    decimal? StopLossPrice = null,
    decimal FxRateToBase = 1m)
{
    /// <summary>ローカル通貨建ての概算約定金額（執行・スリッページ評価用）。</summary>
    public decimal Notional => Quantity * Price;

    /// <summary>基準通貨（円）建ての概算約定金額。リスク統制の金額判定はこちらを用いる（IADR-0106）。</summary>
    public decimal NotionalInBase => Quantity * Price * FxRateToBase;
}

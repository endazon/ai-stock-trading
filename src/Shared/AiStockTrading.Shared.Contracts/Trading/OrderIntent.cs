namespace AiStockTrading.Shared.Contracts.Trading;

// FR-04, FR-05: 取引判断サービスが生成する注文意図。リスク管理の検証を経て発注執行へ渡る。
// Price は基準通貨（円換算）の参照価格で、概算約定金額（Quantity × Price）の算出に使う。
// PositionEffect（建玉効果）はエントリー/手仕舞いを表し、既定は Open（新規建て）。エントリー専用の
// リスク統制の適用可否はこの値で判定する（IADR-0004）。既定を Open とすることで、効果未指定の注文は
// 制約を厳しく掛ける安全側に倒れる。
public record OrderIntent(
    string Symbol,
    Market Market,
    TradeSide Side,
    ProductType ProductType,
    TradeMode Mode,
    int Quantity,
    decimal Price,
    PositionEffect PositionEffect = PositionEffect.Open)
{
    public decimal Notional => Quantity * Price;
}

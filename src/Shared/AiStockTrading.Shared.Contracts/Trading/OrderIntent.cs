namespace AiStockTrading.Shared.Contracts.Trading;

// FR-04, FR-05: 取引判断サービスが生成する注文意図。リスク管理の検証を経て発注執行へ渡る。
// Price は基準通貨（円換算）の参照価格で、概算約定金額（Quantity × Price）の算出に使う。
public record OrderIntent(
    string Symbol,
    Market Market,
    TradeSide Side,
    ProductType ProductType,
    TradeMode Mode,
    int Quantity,
    decimal Price)
{
    public decimal Notional => Quantity * Price;
}

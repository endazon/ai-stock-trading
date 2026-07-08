namespace AiStockTrading.Shared.Contracts.Trading;

// FR-19, ADR-0007: 取引商品は現物基本＋信用を設定で有効化できる両対応
public enum ProductType
{
    Cash,
    Margin,
}

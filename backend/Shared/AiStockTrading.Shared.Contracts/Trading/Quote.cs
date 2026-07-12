namespace AiStockTrading.Shared.Contracts.Trading;

// FR-01, FR-03: 情報源から取得する現在値
public record Quote(
    string Symbol,
    Market Market,
    decimal Price,
    DateTimeOffset AsOf);

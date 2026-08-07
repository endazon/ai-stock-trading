namespace AiStockTrading.Shared.Contracts.Trading;

// FR-10, FR-11, UC-06, ADR-0016 決定4（2026-08-06 改訂）, #419, IADR-0159:
// 強制買戻しの事後推定において「**自らの決済指示**」として突合した約定 1 件。
//
// 決定4 の改訂は「自らの決済指示に対応しない建玉の消失を強制買戻しとみなす」と定めた。**推定である以上、
// 何と突合した結果そう判断したのかを後から人が検証できなければならない**（同改訂・#419 の設計上の注意）。
// 本型は監査ログ（FR-11）へ残す根拠であり、判定そのものには用いない（判定は数量の突合で行う）。
public sealed record BuyInCoveringFill(
    TradeSide Side,
    int Quantity,
    decimal Price,
    DateTimeOffset ExecutedAt);

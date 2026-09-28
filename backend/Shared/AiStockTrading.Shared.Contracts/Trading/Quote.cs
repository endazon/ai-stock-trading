namespace AiStockTrading.Shared.Contracts.Trading;

// FR-01, FR-03: 情報源から取得する現在値
// FR-02, FR-04, ADR-0044 決定1, #1035, IADR-0451: 同じ応答の日中文脈（前日終値・当日始値・日中高値・安値）を省略可能な値として運ぶ。
// 🔴 **null＝不明**（情報源が持たない・場前で 0 が返った等）。0 を値として入れない（前日比が -100% になる）。
// 既存の 4 引数の構築はそのまま通る（追加項目の既定は null）。
public record Quote(
    string Symbol,
    Market Market,
    decimal Price,
    DateTimeOffset AsOf,
    decimal? PreviousClose = null,
    decimal? Open = null,
    decimal? High = null,
    decimal? Low = null);

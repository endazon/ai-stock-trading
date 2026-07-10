using AiStockTrading.InformationCollection.Domain;

namespace AiStockTrading.InformationCollection.Application.State;

// FR-01: 情報源が返す生の取得アイテム（未正規化・未サニタイズ）。収集サービスが許可リスト選別→正規化→サニタイズする。
public sealed record RawInformationItem(
    InformationKind Kind,
    string Source,
    string? Symbol,
    string Title,
    string Content,
    DateTimeOffset PublishedAt,
    string? Url = null);

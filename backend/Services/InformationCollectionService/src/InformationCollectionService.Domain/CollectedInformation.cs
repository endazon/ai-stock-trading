namespace AiStockTrading.InformationCollection.Domain;

// FR-01: 収集情報の種別（市況・ニュース・開示・マクロ指標）。
public enum InformationKind
{
    Quote,
    News,
    Disclosure,
    MacroIndicator,
}

// FR-01, ADR-0004: 正規化済みの収集情報。KB 保存・取引判断文脈の共通形。Content は PromptSafetySanitizer で
// データとして分離済み（命令ではなくデータ）である前提（ニュース入力の防御）。
public sealed record CollectedInformation(
    InformationKind Kind,
    string Source,
    string? Symbol,
    string Title,
    string Content,
    DateTimeOffset PublishedAt,
    string? Url);

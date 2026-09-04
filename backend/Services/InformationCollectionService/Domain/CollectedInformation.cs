namespace InformationCollectionService.Domain;

// FR-01: 収集情報の種別（市況・ニュース・開示・マクロ指標・需給・収集状態）。
public enum InformationKind
{
    Quote,
    News,
    Disclosure,
    MacroIndicator,

    // FR-01, ADR-0016 決定12, #687: 需給データ（銘柄単位）。FINRA 空売り出来高など、市況・マクロ指標
    // いずれにも当たらない「特定銘柄の需給を示す数値」を表す。
    SupplyDemand,

    // FR-01, ADR-0020 決定2: 収集そのものの状態（欠測の明示）。**欠測を無言で空データとして渡さない**ため、
    // 収集サービスが自ら書き起こす種別である（外部から取得したテキストではない）。
    SourceStatus,
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

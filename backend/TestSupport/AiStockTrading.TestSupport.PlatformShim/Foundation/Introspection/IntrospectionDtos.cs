namespace AiStockTrading.TestSupport.PlatformShim.Foundation.Introspection;

// ADR-0001, FR-15（platform）, #22 受け入れ基準③: サービスの自己申告（実効構成）。
// 構成情報 API（platform BFF）はこれを集約し、宣言（pipeline.json）と突合してドリフトを検出する。
// メッシュ内部限定エンドポイント（GET /internal/introspection）が返す。

// サービス 1 つ分の実効構成。service は pipeline.json の service キー（例: "trade-decision-service"）。
public sealed record ServiceIntrospectionDto(
    string Service,
    IReadOnlyList<StepIntrospectionDto> Steps,
    IReadOnlyList<PortSelectionDto> Ports,
    string? ConfigVersion);

// 購読/発行する段の実効値。宣言（pipeline.json）から導出する（登録規則と同じ源泉）。
public sealed record StepIntrospectionDto(
    string Name,
    string Input,
    IReadOnlyList<string> Outputs,
    bool Enabled);

// 選択中のポート実装（例: broker / moomoo・llm-completion / http:...）。
public sealed record PortSelectionDto(
    string Port,
    string Implementation,
    string? Target = null);

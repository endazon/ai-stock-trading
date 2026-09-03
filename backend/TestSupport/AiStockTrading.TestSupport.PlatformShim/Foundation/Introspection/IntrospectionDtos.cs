namespace AiStockTrading.TestSupport.PlatformShim.Foundation.Introspection;

// ADR-0001, FR-15（platform）, #22 受け入れ基準③: サービスの自己申告（実効構成）。
// 構成情報 API（platform BFF）はこれを集約し、宣言（pipeline.json）と突合してドリフトを検出する。
// メッシュ内部限定エンドポイント（GET /internal/introspection）が返す。

// サービス 1 つ分の実効構成。service は pipeline.json の service キー（例: "trade-decision-service"）。
// FR-01, ADR-0031（計画）決定2〜4, IADR-0292: Metrics は Ports と同じ任意申告の枠（既定空）。
// 数値の見積り等、ポート選択には当たらない自己申告値（例: Finnhub 日次要求見積り）をここへ載せる。
public sealed record ServiceIntrospectionDto(
    string Service,
    IReadOnlyList<StepIntrospectionDto> Steps,
    IReadOnlyList<PortSelectionDto> Ports,
    string? ConfigVersion,
    IReadOnlyList<MetricSelectionDto>? Metrics = null);

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

// FR-01, ADR-0031（計画）決定2〜4, IADR-0292: ポート選択に当たらない数値等の自己申告値
// （例: Finnhub 日次要求見積り "finnhub-daily-request-estimate" / "142"）。
public sealed record MetricSelectionDto(
    string Name,
    string Value);

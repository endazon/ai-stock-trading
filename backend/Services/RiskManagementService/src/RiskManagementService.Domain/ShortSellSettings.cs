namespace RiskManagementService.Domain;

// FR-10, FR-19, UC-06, ADR-0016: 空売り専用統制値の集約（設定ストア由来）。
//
// #332, IADR-0132 決定2: **有効・無効は本型で持たない。** 取引ガードの商品種別
// （<c>TradingGuardSettings.EnabledProductTypes</c> が <c>ProductType.ShortSell</c> を含むか）が
// 単一情報源である（計画 FR-19「3 値をそれぞれ独立に有効・無効を設定でき、既定は現物のみ有効」）。
// #329 第 2 段階は `ProductType` が 2 値だったため専用フラグ `Enabled` を持っていたが、
// 有効・無効が 2 箇所に分かれると設定が食い違う（IADR-0131 §結果の申し送り）。
//
// 統制値は無効時も保持する。段階解禁（Stage 3・自己資金 $5,000 以上。決定8）で有効化した瞬間から
// 計画どおりの上限が効くようにするためである。
public record ShortSellSettings
{
    /// <summary>空売り専用の統制値（ADR-0016 決定2,3,4,7,9）。</summary>
    public required ShortSellingLimits Limits { get; init; }
}

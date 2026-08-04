namespace AiStockTrading.RiskManagement.Domain;

// FR-10, FR-19, UC-06, ADR-0016: 空売りの有効・無効と専用統制値の集約（設定ストア由来）。
// 既定は**無効**（計画 §5「商品種別の既定はいずれも現物のみ有効」・ADR-0016 決定1/8）。
// 実弾での解禁は Stage 3 かつ自己資金 $5,000 以上（決定8）であり、早くて半年以上先である。
//
// #329 第 2 段階, IADR-0131: 有効・無効を <c>ProductType.ShortSell</c>（3 値化）ではなく本フラグで持つのは、
// 商品種別の 3 値化が #332 の担当であり、本 issue で enum を先に割ると計画適合検査の既知逸脱
// （ProductType.Values・#332 担当）と衝突するためである。#332 が 3 値化した時点で、本フラグは
// <c>Guard.EnabledProductTypes</c> への統合を検討する（IADR-0131 §結果）。
public record ShortSellSettings
{
    /// <summary>空売りが有効か。既定 false（現物のみ）。無効時の新規売り建ては <c>ShortSellDisabled</c> で拒否する。</summary>
    public required bool Enabled { get; init; }

    /// <summary>空売り専用の統制値（ADR-0016 決定2,3,4,7,9）。</summary>
    public required ShortSellingLimits Limits { get; init; }
}

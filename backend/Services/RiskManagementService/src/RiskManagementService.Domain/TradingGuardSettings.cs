using AiStockTrading.Shared.Contracts.Trading;

namespace AiStockTrading.RiskManagement.Domain;

// FR-19, ADR-0007: 取引ガードのソフト設定。変更は利用者のみが行える（生成AI・自動処理は変更不可）
public record TradingGuardSettings
{
    /// <summary>
    /// FR-19, ADR-0016 決定1, #332: 有効な商品種別（現物 / 信用買い / 空売り の 3 値を独立に制御する）。
    /// 既定は「現物のみ有効」。**空売りの有効・無効もここが単一情報源である**（IADR-0132 決定2）。
    /// </summary>
    public required IReadOnlySet<ProductType> EnabledProductTypes { get; init; }

    public required IReadOnlySet<Market> EnabledMarkets { get; init; }

    /// <summary>取引禁止銘柄（利用者が理由・登録日を伴って登録する。ADR-0007 §決定）。</summary>
    public required IReadOnlyCollection<BannedSymbol> BannedSymbols { get; init; }

    /// <summary>
    /// 差金決済防止: 同一銘柄の同日再エントリー禁止。**適用は日本株の現物取引のみ**である
    /// （日本の差金決済規制〔金商法 161 条の 2〕向け。米国株は信用口座で運用するため
    /// Good Faith Violation が発生しない。FR-19 本文・05_trading-assumptions §5・#332・IADR-0132 決定5）。
    /// </summary>
    public bool PreventSameDayReentry { get; init; } = true;

    /// <summary>
    /// 相場操縦とみなされ得る発注パターン（約定意思のない発注・板演出・過剰な訂正/取消）の禁止（FR-19）。
    /// 既定で有効。実際の検知は <see cref="IManipulativeOrderPatternDetector"/> の注入実装が担う（後続スライス）。
    /// </summary>
    public bool ProhibitManipulativeOrderPatterns { get; init; } = true;
}

using AiStockTrading.Shared.Contracts.Trading;

namespace AiStockTrading.RiskManagement.Domain;

// FR-19, ADR-0007: 取引ガードのソフト設定。変更は利用者のみが行える（生成AI・自動処理は変更不可）
public record TradingGuardSettings
{
    public required IReadOnlySet<ProductType> EnabledProductTypes { get; init; }

    public required IReadOnlySet<Market> EnabledMarkets { get; init; }

    public required IReadOnlyCollection<BannedSymbol> BannedSymbols { get; init; }

    /// <summary>差金決済防止: 同一銘柄の同日再エントリー禁止（現物）。</summary>
    public bool PreventSameDayReentry { get; init; } = true;
}

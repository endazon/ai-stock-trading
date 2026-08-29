using AiStockTrading.Shared.Contracts.Trading;

namespace TradeDecisionService.Features.TradeDecision;

// FR-17, 05_trading-assumptions §4, IADR-0076: 採算評価に必要な費用見積りの供給口。
// 設定サービスの版付き前提条件（IADR-0063）＋概算費用関数（CostCalculator・IADR-0021）を Worker アダプタが包み、
// 市場・約定代金から往復費用と最小期待利益倍率を返す。前提条件が未解決・取得不能なら null（＝採算不能＝安全側 Hold）。
// 同期クリティカルパス（取引判断）から呼ばれるため非同期。実装側で fail-safe（例外を出さず null）に倒す。
public interface IProfitabilityAssumptionsProvider
{
    Task<TradeCostAssessment?> AssessAsync(Market market, decimal notional, CancellationToken cancellationToken = default);
}

// FR-17, IADR-0076: 採算評価の費用見積り。RoundTripCost は往復の概算取引費用（円）、MinimumProfitMultiple は最小期待利益倍率、
// AssumptionsVersion は適用した前提条件のバージョン（FR-17 の版記録・FR-11 監査ログ用）。
// #358, IADR-0173: CapitalGainsTaxRate を運ぶ。しきい値の基準が「往復費用＋**税**」になったが、
// 採算ゲートは TradeDecisionService.Domain にあり設定サービスへ依存できないため、税率は本 DTO で渡す。
public sealed record TradeCostAssessment(
    decimal RoundTripCost,
    decimal MinimumProfitMultiple,
    int AssumptionsVersion,
    decimal CapitalGainsTaxRate = 0m);

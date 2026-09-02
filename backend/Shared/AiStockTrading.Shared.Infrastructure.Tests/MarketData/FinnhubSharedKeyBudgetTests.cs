using AiStockTrading.Shared.Infrastructure.Composable.Adapters.MarketData;
using AwesomeAssertions;
using Xunit;

namespace AiStockTrading.Shared.Infrastructure.Tests.MarketData;

// FR-01, IADR-0275: 実クラスタでの実測（60 回/60 秒固定ウィンドウ）と、ローカル実行環境（values-local.yaml）で
// 情報収集（Collection:Source:Finnhub）と実市況（MarketData:Finnhub）が実際に同一 Finnhub 鍵を共有し得ることの
// 確認を踏まえ、自制レートの合計が実測上限を超えないことを固定する退行テスト。
//
// プロセス間の協調は行わない設計（IADR-0068 決定4）のため、超過は「合計を実測上限以下に保つ」構成上の
// 責務でしか防げない。IADR-0275 決定3 が指摘したとおり、市況の消費サービスは実装上 4 つ
// （MarketMonitorService/ReportService/RiskManagementService/TradeDecisionService）であり、
// IADR-0068 決定4 の「3 サービス」という前提は過小算定だった。
public class FinnhubSharedKeyBudgetTests
{
    // InformationCollectionService.Domain.CollectionSourceOptions.FinnhubOptions.RateLimitPerMinute の既定値
    // （Shared.Infrastructure から Services への参照は依存方向違反のため、値をここへ複写して固定する）。
    private const int CollectionDefaultRequestsPerMinute = 30;

    // MarketMonitorService / ReportService / RiskManagementService / TradeDecisionService の 4 サービス。
    private const int MarketDataConsumerServiceCount = 4;

    // IADR-0275 実測: Finnhub Free `/quote` は 60 回/60 秒の固定ウィンドウ（ローリングではない）。
    private const int MeasuredRealLimitPerWindow = 60;

    [Fact]
    public void 情報収集と市況4サービスの自制レート合計は実測上限を超えない()
    {
        var marketDataDefault = new FinnhubMarketDataOptions().RequestsPerMinute;

        var combined = CollectionDefaultRequestsPerMinute + (marketDataDefault * MarketDataConsumerServiceCount);

        combined.Should().BeLessThanOrEqualTo(MeasuredRealLimitPerWindow);
    }
}

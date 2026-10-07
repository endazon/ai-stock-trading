using System.Text.Json;
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
//
// FR-01, #1225, IADR-0512: 配備で効く値（Helm の描画）の合計は scripts/check-finnhub-key-budget.js が helm.yml で検査する。
// その検査器が読む母集合と既定値（scripts/finnhub-key-budget.json）のうち、市況の既定・消費サービスの数・上限をここでコードと
// 突き合わせる（複写が古くなっても緑のままにしない）。情報収集の既定は Shared → Services の参照が依存方向違反のため
// InformationCollectionService.Tests の FinnhubKeyBudgetDefaultsTests がコードと突き合わせ、ここでは JSON から読む。
public class FinnhubSharedKeyBudgetTests
{
    private const string MarketDataRateEnv = "MarketData__Finnhub__RequestsPerMinute";

    // MarketMonitorService / ReportService / RiskManagementService / TradeDecisionService の 4 サービス。
    private const int MarketDataConsumerServiceCount = 4;

    // IADR-0275 実測: Finnhub Free `/quote` は 60 回/60 秒の固定ウィンドウ（ローリングではない）。
    private const int MeasuredRealLimitPerWindow = 60;

    private static string RepoRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "backend", "backend.slnx"))) return dir.FullName;
        }
        throw new InvalidOperationException("リポジトリの根（backend/backend.slnx）が見つからない。");
    }

    private static JsonElement Budget()
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(RepoRoot(), "scripts", "finnhub-key-budget.json")));
        return doc.RootElement.Clone();
    }

    private static IReadOnlyList<JsonElement> Consumers() => [.. Budget().GetProperty("consumers").EnumerateArray()];

    [Fact]
    public void 情報収集と市況4サービスの自制レート合計は実測上限を超えない()
    {
        var marketDataDefault = new FinnhubMarketDataOptions().RequestsPerMinute;
        var collectionDefault = Consumers()
            .Single(c => c.GetProperty("deployment").GetString() == "information-collection-service")
            .GetProperty("codeDefault").GetInt32();

        var combined = collectionDefault + (marketDataDefault * MarketDataConsumerServiceCount);

        combined.Should().BeLessThanOrEqualTo(MeasuredRealLimitPerWindow);
    }

    [Fact]
    public void 予算の検査器が読む市況の既定値と消費サービスの数と上限はコードと一致する()
    {
        var marketData = Consumers().Where(c => c.GetProperty("rateEnv").GetString() == MarketDataRateEnv).ToList();

        marketData.Should().HaveCount(MarketDataConsumerServiceCount);
        marketData.Select(c => c.GetProperty("codeDefault").GetInt32())
            .Should().AllBeEquivalentTo(new FinnhubMarketDataOptions().RequestsPerMinute);
        Budget().GetProperty("limitPerMinute").GetInt32().Should().Be(MeasuredRealLimitPerWindow);
    }
}

using AwesomeAssertions;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace AiStockTrading.InformationCollection.Api.Tests;

// SC-01, FR-13, UC-06, #423, IADR-0165 決定1:
// **情報収集の巡回間隔（`Collection:PollIntervalSeconds`）を実行時に変更する経路が存在しないこと**を
// 構造で固定する。MarketMonitorService 側の同名テストと対をなす。
//
// 2026-08-07 の利用者裁定（質問票 第 13 回 Q11・案 A）により、SC-01 §2 は廃止され
// **収集間隔は起動時構成**と定まった。**構成そのものは残す**（裁定は「画面から変更しない」であって
// 「構成を持たない」ではない）。ここで否定するのは**実行時に変更する経路**だけである。
//
// 本サービスは HTTP の設定 API を持たない（ヘルスチェックのみ）。**その事実を明示的に固定する**——
// 「今は無い」を人の記憶に委ねると、次の PR で足された瞬間に誰も止められない。
public class CollectionIntervalNotConfigurableTests(InformationCollectionWorkerWebApplicationFactory factory)
    : IClassFixture<InformationCollectionWorkerWebApplicationFactory>
{
    private static readonly string[] IntervalWords = ["interval", "poll", "cadence", "frequency", "schedule"];

    [Fact]
    public void ルート表に収集間隔を扱うエンドポイントが存在しない()
    {
        var patterns = RoutePatterns();

        patterns.Should().NotBeEmpty("ルートを 1 本も取れないなら本検査は空振りしている");
        foreach (var pattern in patterns)
        {
            foreach (var word in IntervalWords)
            {
                pattern.Contains(word, StringComparison.OrdinalIgnoreCase).Should().BeFalse(
                    "収集間隔は起動時構成であり実行時に変更できてはならない（裁定 質問票 第 13 回 Q11）。"
                        + $" 違反ルート: {pattern}");
            }
        }
    }

    // 対照（肯定形）: 検査器が空振りしていないこと。ヘルスチェックは実在する。
    [Fact]
    public void ヘルスチェックの経路そのものは実在する()
    {
        RoutePatterns().Should().Contain(p => p.Contains("health", StringComparison.OrdinalIgnoreCase));
    }

    private IReadOnlyList<string> RoutePatterns() =>
        factory.Services.GetRequiredService<EndpointDataSource>().Endpoints
            .OfType<RouteEndpoint>()
            .Select(e => e.RoutePattern.RawText ?? string.Empty)
            .ToList();
}

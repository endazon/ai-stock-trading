using System.Net;
using System.Net.Http.Json;
using MarketMonitorService.Domain;
using AwesomeAssertions;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MarketMonitorService.Tests;

// SC-01, FR-13, UC-06, #423, IADR-0164 決定1:
// **収集間隔（巡回間隔）を画面・API から変更する経路が存在しないことを構造で固定する。**
//
// 2026-08-07 の利用者裁定（質問票 第 13 回 Q11・案 A）は、SC-01 §2 を廃止したうえで
// **「収集間隔は画面から変更しない。起動時構成とする」**と定めた。理由は次のとおりである。
//
//   - 収集間隔は**戦略ではなく費用・負荷のパラメータ**であり、月報レビュー時に構成で変える頻度で足りる。
//   - 画面から変えるには、永続ストア・エンドポイントに加えて
//     **稼働中の `BackgroundService` が値の変更を読み直す機構**が要る。これは巡回中のタイミングと
//     絡む挙動変更であり、その重さに見合う運用上の必要が示されていない。
//
// **「実装していない」と「実装してはならない」は別である。** 前者のままにすると、次に画面や API を
// 触る者が「未実装の項目」として素直に実装してしまう——裁定に反する変更が「機能追加」の顔をして
// 入り込む。本テスト群はその経路を赤くする。
//
// **なお起動時構成（`Monitor:PollIntervalSeconds`）は残す。** 裁定は「画面から変更しない」であって
// 「構成を持たない」ではない。ここで否定するのは**実行時に変更する経路**だけである。
public class CollectionIntervalNotConfigurableTests(MonitorWorkerWebApplicationFactory factory)
    : IClassFixture<MonitorWorkerWebApplicationFactory>
{
    // 「間隔を変える経路」を示唆する語。ルート表・型のプロパティ名の双方に用いる。
    private static readonly string[] IntervalWords = ["interval", "poll", "cadence", "frequency", "schedule"];

    private HttpClient OwnerClient()
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add(TestAuthHandler.RolesHeader, "trading-owner");
        return client;
    }

    // ------------------------------------------------------------------
    // 構造1: ルート表に間隔を扱うエンドポイントが 1 本も無い
    // ------------------------------------------------------------------

    // **実 `Program.cs` の配線から `EndpointDataSource` を取り出して検査する。**
    // 「テストが想像したルート一覧」ではなく、実際に公開されているルートそのものを見る。
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

    // 対照（肯定形）: 検査器が空振りしていないこと。**間隔以外の監視設定の経路は実在する。**
    // これが無いと「ルートを 1 本も見ていないから緑」という壊れ方に気づけない。
    [Fact]
    public void 監視設定の経路そのものは実在する()
    {
        var patterns = RoutePatterns();

        patterns.Should().Contain(p => p.Contains("monitor/settings", StringComparison.Ordinal));
        patterns.Should().Contain(p => p.Contains("movement-threshold", StringComparison.Ordinal));
        patterns.Should().Contain(p => p.Contains("cooldown", StringComparison.Ordinal));
    }

    // ------------------------------------------------------------------
    // 構造2: 監視設定の型が間隔を持たない
    // ------------------------------------------------------------------

    // ルート名だけの検査は、別の語（例: `/monitor/settings/tick`）で回避され得る。
    // **型の側からも塞ぐ**——設定として往来する型に間隔のプロパティが無ければ、
    // エンドポイントを足しても保存する先が無い。
    [Fact]
    public void 監視設定のドメイン型が収集間隔のプロパティを持たない()
    {
        var properties = typeof(MarketMonitorSettings).GetProperties().Select(p => p.Name).ToList();

        properties.Should().NotBeEmpty();
        foreach (var word in IntervalWords)
        {
            properties.Should().NotContain(
                n => n.Contains(word, StringComparison.OrdinalIgnoreCase),
                $"監視設定は収集間隔を保持しない（起動時構成である）。語: {word}");
        }
    }

    // ------------------------------------------------------------------
    // 挙動: 想定される「素直な」パスは 404 である
    // ------------------------------------------------------------------

    // 実装者が最初に試すであろうパスを名指しで否定する。構造検査（上の 3 件）が本命だが、
    // 「実際に叩いて 404」であることは読み手に対して最も分かりやすい。
    [Theory]
    [InlineData("/monitor/settings/poll-interval")]
    [InlineData("/monitor/settings/interval")]
    [InlineData("/monitor/settings/collection-interval")]
    public async Task 収集間隔の変更エンドポイントは存在しない(string path)
    {
        var res = await OwnerClient().PutAsJsonAsync(path, new { pollIntervalSeconds = 5, reason = "検証" });

        res.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    // 全置換 PUT に間隔を混ぜても**黙って受理されない**（未知プロパティが設定に化けない）。
    // 受理されること自体は JSON の既定挙動として許すが、**保存された設定に間隔が現れてはならない**。
    [Fact]
    public async Task 全置換に間隔を混ぜても設定へ現れない()
    {
        var client = OwnerClient();

        await client.PutAsJsonAsync("/monitor/settings", new
        {
            MovementThresholdRatio = 0.03m,
            Cooldown = TimeSpan.FromMinutes(15),
            MonitoredSymbols = Array.Empty<MonitoredSymbol>(),
            Reason = "間隔の混入検証",
            PollIntervalSeconds = 1,
        });

        var body = await client.GetStringAsync("/monitor/settings");

        foreach (var word in IntervalWords)
        {
            body.Contains(word, StringComparison.OrdinalIgnoreCase).Should().BeFalse(
                $"監視設定の応答に間隔は現れない。語: {word}");
        }
    }

    private IReadOnlyList<string> RoutePatterns() =>
        factory.Services.GetRequiredService<EndpointDataSource>().Endpoints
            .OfType<RouteEndpoint>()
            .Select(e => e.RoutePattern.RawText ?? string.Empty)
            .ToList();
}

using System.Net;
using FluentAssertions;
using Xunit;

namespace AiStockTrading.InformationCollection.Worker.Tests;

// FR-01: 情報収集サービスホストが起動し（既定の安全な no-op 情報源で外部接続せず）、ヘルスが応答することを検証する。
public class HealthEndpointTests(InformationCollectionWorkerWebApplicationFactory factory)
    : IClassFixture<InformationCollectionWorkerWebApplicationFactory>
{
    [Fact]
    public async Task ヘルスチェック_live_は応答する()
    {
        var res = await factory.CreateClient().GetAsync("/health/live");
        res.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    // #121: 本番スケジューラ（K8s CronJob）の run-once トリガ。1 巡回を起動し 200 を返す
    //（既定の no-op 情報源では収集ゼロだが、エンドポイントの疎通＝CronJob からの起動を保証する）。
    [Fact]
    public async Task RunOnce_エンドポイントは_200_を返す()
    {
        var res = await factory.CreateClient().PostAsync("/internal/collection/run-once", content: null);
        res.StatusCode.Should().Be(HttpStatusCode.OK);
    }
}

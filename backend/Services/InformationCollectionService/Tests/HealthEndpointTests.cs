using System.Net;
using AwesomeAssertions;
using AiStockTrading.TestSupport.PlatformShim.Foundation.Extensions;
using Xunit;

namespace InformationCollectionService.Tests;

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
    //
    // **【⚠️ 2026-08-07・#456 / IADR-0176 で期待が変わった】** 本テストは元々**無認証で 200**
    // を期待していた。それは当時の実装どおりだったが、**その「当時の実装」こそが #456 で塞いだ穴**である
    // —— **無認証の疎通を固定するテストは、穴を仕様として固定していた**。
    // 現在は `trading-service` ロールを付けて叩く（CronJob と同じ経路）。
    // **無認証で 401 になること**は `RunOnceAuthorizationTests` が受け持つ。
    [Fact]
    public async Task RunOnce_エンドポイントはサービストークンで_200_を返す()
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add(TestAuthHandler.RolesHeader, AiStockTradingAuthPolicies.ServiceRole);

        var res = await client.PostAsync("/internal/collection/run-once", content: null);
        res.StatusCode.Should().Be(HttpStatusCode.OK);
    }
}

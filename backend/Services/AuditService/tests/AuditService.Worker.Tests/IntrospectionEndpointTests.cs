using System.Net;
using System.Net.Http.Json;
using AiStockTrading.TestSupport.PlatformShim.Foundation.Introspection;
using FluentAssertions;
using Xunit;

namespace AiStockTrading.Audit.Worker.Tests;

// ADR-0001, FR-15, #22 受け入れ基準③: 実効構成の自己申告エンドポイントの結線を代表 1 サービスで検証する
// （全 10 Worker は同一の 2 行結線・自己申告の内容ロジックは PlatformShim.Tests で網羅）。
public class IntrospectionEndpointTests(AuditWorkerWebApplicationFactory factory)
    : IClassFixture<AuditWorkerWebApplicationFactory>
{
    [Fact]
    public async Task 自己申告エンドポイントは無認可で応答しサービス名を返す()
    {
        // メッシュ内部限定・無認可（親グループに認可を付けない）。トークン無しで 200。
        var res = await factory.CreateClient().GetAsync("/internal/introspection");
        res.StatusCode.Should().Be(HttpStatusCode.OK);

        var dto = await res.Content.ReadFromJsonAsync<ServiceIntrospectionDto>();
        dto.Should().NotBeNull();
        dto!.Service.Should().Be("audit-service");
        // 監査は pipeline.json の変換段ではない（横断オブザーバ）ため段は空。構成バージョン未注入で null。
        dto.Steps.Should().BeEmpty();
        dto.ConfigVersion.Should().BeNull();
    }
}

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
        // 本ファクトリは Pipeline:ConfigPath を設定しないため fail-safe（宣言未マウント）で段は空になる。
        // 「audit-service が pipeline.json に段を持たない（横断オブザーバ）」ことの検証は宣言パースの単体テスト
        // （PlatformShim.Tests の IntrospectionTests）側で担保する。ここは無認可応答と DTO 形状の結線を検証する。
        dto.Steps.Should().BeEmpty();
        dto.ConfigVersion.Should().BeNull();
    }
}

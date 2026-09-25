using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using CostControlService.Features.CostControl;
using AwesomeAssertions;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace CostControlService.Tests;

// 🔴 T-10-912, FR-01, NFR（費用）, #957, IADR-0408（2026-09-25 追記。リスク管理の T-10-805 の同型）: **本番の Program.cs が
// `GET /costs/state` に出す JSON は、応答型 `CostControlDecision` を web 既定＋文字列列挙で直列化したものと一字一句同じである**ことを
// 固定する。受け手（情報収集 T-10-911）はこの設定で直列化した本文を読ませており、その前提（＝送り手が JSON 設定を変えていない）は
// 受け手の側からは見えない。停止の判定に使う `isHalted`（計算プロパティ）が本文に載っていることも名指しで表明する。
public class ReadContractWireFormatTests
{
    private static readonly JsonSerializerOptions CostWire =
        new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };

    [Fact]
    public async Task 費用統制の状態の本文は応答型を_web_既定と文字列列挙で直列化したものと同じ()
    {
        await using var factory = new CostControlWorkerWebApplicationFactory();
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add(TestAuthHandler.RolesHeader, "trading-service");

        var res = await client.GetAsync("/costs/state");
        res.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = JsonNode.Parse(await res.Content.ReadAsStringAsync())!.AsObject();

        using var scope = factory.Services.CreateScope();
        var expected = await scope.ServiceProvider.GetRequiredService<CostControlAppService>().GetLlmStateAsync();

        JsonNode.DeepEquals(body, JsonSerializer.SerializeToNode(expected, CostWire))
            .Should().BeTrue($"costs/state の本文が web 既定＋文字列列挙と異なる: {body.ToJsonString()}");
        body.Select(p => p.Key).Should().BeEquivalentTo(["state", "intervalMultiplier", "isHalted"]);
        body["state"]!.GetValueKind().Should().Be(JsonValueKind.String);
        body["isHalted"]!.GetValueKind().Should().BeOneOf(JsonValueKind.True, JsonValueKind.False);
    }
}

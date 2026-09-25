using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using AiStockTrading.Shared.Contracts.Events;
using AiStockTrading.Shared.Contracts.Trading;
using AuditService.Domain;
using AuditService.Features.AuditEvents;
using AwesomeAssertions;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace AuditService.Tests;

// 🔴 T-10-922, FR-06, FR-11, #957, IADR-0408（2026-09-25 追記。リスク管理の T-10-805 の同型）: **本番の Program.cs が
// `GET /audit/events/by-type`（報告書の借株料・為替の情報源・LLM 使用量・判断根拠が読む口）に出す JSON は、応答型 `AuditEntry` の
// 一覧を web 既定で直列化したものと一字一句同じである**ことを固定する。受け手（報告書 T-10-917〜920）はこの設定で直列化した本文を
// 読ませている。受け手が種別の突き合わせに使う `eventType` と本文の `detail`（文字列）が載っていることも名指しで表明する。
public class ReadContractWireFormatTests
{
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    [Fact]
    public async Task 種別期間照会の本文は応答型を_web_既定で直列化したものと同じ()
    {
        await using var factory = new AuditWorkerWebApplicationFactory();
        var t0 = new DateTimeOffset(2026, 8, 3, 10, 0, 0, TimeSpan.Zero);
        using (var scope = factory.Services.CreateScope())
        {
            scope.ServiceProvider.GetRequiredService<IAuditEventStore>().Append(AuditEntryFactory.From(
                new BorrowFeeAccrued("AAPL", Market.UnitedStates, new DateOnly(2026, 8, 3), 0.06m, 10_000m, 1.64m, t0),
                Guid.NewGuid(), t0.AddSeconds(1)));
        }

        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add(TestAuthHandler.RolesHeader, "trading-service");
        var res = await client.GetAsync("/audit/events/by-type"
            + $"?from={Uri.EscapeDataString(t0.AddDays(-1).ToString("o"))}&to={Uri.EscapeDataString(t0.AddDays(1).ToString("o"))}"
            + $"&types={nameof(BorrowFeeAccrued)}");
        res.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = JsonNode.Parse(await res.Content.ReadAsStringAsync())!.AsArray();

        using var s = factory.Services.CreateScope();
        var expected = s.ServiceProvider.GetRequiredService<IAuditEventStore>()
            .GetByTypesInPeriod([nameof(BorrowFeeAccrued)], t0.AddDays(-1), t0.AddDays(1));
        expected.Should().ContainSingle("空の配列どうしの一致は何も証明しない");

        JsonNode.DeepEquals(body, JsonSerializer.SerializeToNode(expected, Web))
            .Should().BeTrue($"by-type の本文が web 既定と異なる: {body.ToJsonString()}");
        body.Single()!["eventType"]!.GetValue<string>().Should().Be(nameof(BorrowFeeAccrued));
        body.Single()!["detail"]!.GetValueKind().Should().Be(JsonValueKind.String);
    }
}

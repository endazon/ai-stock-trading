using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using AwesomeAssertions;
using Microsoft.Extensions.DependencyInjection;
using ReportService.Domain;
using ReportService.Features.Reports;
using Xunit;

namespace ReportService.Tests;

// 🔴 T-10-921, FR-06, FR-07, FR-14, #957, IADR-0408（2026-09-25 追記。リスク管理の T-10-805 の同型）: **本番の Program.cs が
// 他サービスの読む口（判断が読む `GET /reports/daily-policy`・通知が読む `GET /reports/{periodKey}/review`）に出す JSON は、
// 応答型を web 既定＋文字列列挙で直列化したものと一字一句同じである**ことを固定する。受け手の契約テスト（判断 T-10-910・
// 通知 T-10-913）はこの設定で直列化した本文を読ませており、その前提は受け手の側からは見えない。
public class ReadContractWireFormatTests
{
    private const string Key = "daily-2026-07-10";

    private static readonly JsonSerializerOptions ReportWire =
        new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };

    [Fact]
    public async Task 判断と通知が読む口の本文は応答型を_web_既定と文字列列挙で直列化したものと同じ()
    {
        await using var factory = new ReportWorkerWebApplicationFactory();
        using (var scope = factory.Services.CreateScope())
        {
            var store = scope.ServiceProvider.GetRequiredService<IReportStore>();
            store.UpsertDraft(new TradingReport
            {
                PeriodKey = Key,
                Kind = ReportKind.Daily,
                PeriodStart = new DateOnly(2026, 7, 10),
                AssumptionsVersion = 1,
                PolicySummary = "翌営業日は押し目買い",
                Body = "# 日報\n",
                UnsuppliedInputs = [ReportInput.OpenPositions],
            }, 0);
            store.Confirm(Key, 1, DateTimeOffset.UtcNow).Should().NotBeNull();
        }

        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add(TestAuthHandler.RolesHeader, "trading-owner,trading-service");
        var policy = await BodyAsync(client, "/reports/daily-policy");
        var review = await BodyAsync(client, $"/reports/{Key}/review");

        using var s = factory.Services.CreateScope();
        var svc = s.ServiceProvider.GetRequiredService<ReportAppService>();
        var expectedPolicy = svc.GetConfirmedDailyPolicy();
        var expectedReview = svc.GetReviewView(Key);
        // 空どうし・null どうしの一致は何も証明しない。
        expectedPolicy!.Summary.Should().Be("翌営業日は押し目買い");
        expectedReview!.UnsuppliedInputs.Should().ContainSingle();

        JsonNode.DeepEquals(policy, JsonSerializer.SerializeToNode(expectedPolicy, ReportWire))
            .Should().BeTrue($"daily-policy の本文が異なる: {policy?.ToJsonString()}");
        JsonNode.DeepEquals(review, JsonSerializer.SerializeToNode(expectedReview, ReportWire))
            .Should().BeTrue($"review の本文が異なる: {review?.ToJsonString()}");
        review!["state"]!.GetValueKind().Should().Be(JsonValueKind.String);
    }

    private static async Task<JsonNode?> BodyAsync(HttpClient client, string path)
    {
        var res = await client.GetAsync(path);
        res.StatusCode.Should().Be(HttpStatusCode.OK, path);
        return JsonNode.Parse(await res.Content.ReadAsStringAsync());
    }
}

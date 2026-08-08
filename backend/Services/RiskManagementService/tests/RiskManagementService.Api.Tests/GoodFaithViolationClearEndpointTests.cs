using System.Net;
using System.Net.Http.Json;
using AiStockTrading.RiskManagement.Application.Ports;
using AiStockTrading.RiskManagement.Application.State;
using AiStockTrading.Shared.Contracts.Events;
using AiStockTrading.Shared.Contracts.Trading;
using AwesomeAssertions;
using Microsoft.Extensions.DependencyInjection;
using Wolverine.Tracking;
using Xunit;

namespace AiStockTrading.RiskManagement.Api.Tests;

// FR-19, FR-10, FR-11, UC-06, #464, ADR-0028 決定2, IADR-0182:
// GFV 解除の**成功経路**（200 応答とイベント発行）を固定する。
//
// 認可・検証の否定形は `RiskControlEndpointsTests` が持つ。**本ファイルは「解除が実際に成立し、
// 監査へ発行される」ことを見る** —— 発行側を検査しないと、`AuditEntryFactory` のテストが緑でも
// **Risk が一度も発行していない**状態を検知できない（`StageTransitioned` は発行側も固定されており、
// そちらと対称にする）。
//
// 各テストは専用ファクトリ（隔離 InMemory DB）を使う。違反記録を仕込むため、
// 共有フィクスチャだと他のテストへ漏れる。
public class GoodFaithViolationClearEndpointTests
{
    private const string Owner = "trading-owner";

    private static HttpClient OwnerClient(RiskWorkerWebApplicationFactory factory)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add(TestAuthHandler.RolesHeader, Owner);
        return client;
    }

    // 本番では発注審査が計上する。ここでは DI 経由で直接仕込む（受け口は同じストア）。
    private static void SeedViolations(RiskWorkerWebApplicationFactory factory, params string[] orderIds)
    {
        using var scope = factory.Services.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<IGoodFaithViolationStore>();
        foreach (var id in orderIds)
        {
            store.Append(new GoodFaithViolationRecord(
                Guid.NewGuid(), id, Guid.NewGuid(), "AAPL", Market.UnitedStates,
                PurchaseAmountInBase: 1000m, SettledCashInBase: 0m,
                OccurredOn: new DateOnly(2026, 8, 8),
                ExecutedAt: DateTimeOffset.UtcNow, RecordedAt: DateTimeOffset.UtcNow));
        }
    }

    [Fact]
    public async Task 解除は200で解除した記録の一覧と残件数を返す()
    {
        using var factory = new RiskWorkerWebApplicationFactory();
        SeedViolations(factory, "ord-1", "ord-2");
        var client = OwnerClient(factory);

        var res = await client.PostAsJsonAsync("/risk-controls/good-faith-violations/clear",
            new { reason = "決済済み資金の判定を修正した" });

        res.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await res.Content.ReadFromJsonAsync<ClearResponse>();
        body.Should().NotBeNull();
        body!.ClearedOrderIds.Should().BeEquivalentTo(["ord-1", "ord-2"]);
        // **0 とは限らない**が、ここでは競合が無いため 0（「解除したのに止まったまま」の説明用）。
        body.RemainingCount.Should().Be(0);
    }

    // FR-11, ADR-0028 決定2: **誰が・いつ・どの記録に対して**を中央監査集約へ発行する。
    // **この経路だけが監査への供給を成立させる** —— ここを落としても `AuditEntryFactory` のテストは緑のまま。
    [Fact]
    public async Task 解除受理時に_GoodFaithViolationsCleared_をバス発行する()
    {
        using var factory = new RiskWorkerWebApplicationFactory();
        SeedViolations(factory, "ord-1");
        var client = OwnerClient(factory);

        HttpResponseMessage res = null!;
        var session = await factory.Services.ExecuteAndWaitAsync(async () =>
        {
            res = await client.PostAsJsonAsync("/risk-controls/good-faith-violations/clear",
                new { reason = "決済済み資金の判定を修正した" });
        });
        res.StatusCode.Should().Be(HttpStatusCode.OK);

        var published = session.Sent.MessagesOf<GoodFaithViolationsCleared>().Should().ContainSingle().Subject;
        published.ClearedOrderIds.Should().BeEquivalentTo(["ord-1"]);
        published.Reason.Should().Contain("決済済み資金");
        published.ClearedBy.Should().NotBeNullOrWhiteSpace("決定2 の「誰が」");
    }

    // **否定形**: 受理されない解除（対象なし）では発行しない。
    // 発行してしまうと、**何も起きていない操作が監査上の事実になる**。
    [Fact]
    public async Task 受理されない解除では_GoodFaithViolationsCleared_を発行しない()
    {
        using var factory = new RiskWorkerWebApplicationFactory();
        var client = OwnerClient(factory);

        HttpResponseMessage res = null!;
        var session = await factory.Services.ExecuteAndWaitAsync(async () =>
        {
            res = await client.PostAsJsonAsync("/risk-controls/good-faith-violations/clear",
                new { reason = "原因を是正した" });
        });
        res.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);

        session.Sent.MessagesOf<GoodFaithViolationsCleared>().Should().BeEmpty();
    }

    // 🔴 決定1: 解除後も**違反記録そのものは残る**（HTTP 経路でも成立することを固定する）。
    [Fact]
    public async Task 解除後も違反記録は台帳に残る()
    {
        using var factory = new RiskWorkerWebApplicationFactory();
        SeedViolations(factory, "ord-1", "ord-2");
        var client = OwnerClient(factory);

        var res = await client.PostAsJsonAsync("/risk-controls/good-faith-violations/clear",
            new { reason = "原因を是正した" });
        res.StatusCode.Should().Be(HttpStatusCode.OK);

        using var scope = factory.Services.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<IGoodFaithViolationStore>();
        store.GetRecordedBetween(new DateOnly(2026, 8, 8), new DateOnly(2026, 8, 8))
            .Should().HaveCount(2, "ADR-0028 決定1: 違反記録は失効させない");
        store.GetTally().Count.Should().Be(0, "数えないことと消すことは別である");
    }

    private sealed record ClearResponse(
        List<string> ClearedOrderIds, DateTimeOffset? ClearedAt, int RemainingCount);
}

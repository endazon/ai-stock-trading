using System.Net;
using System.Net.Http.Json;
using AiStockTrading.Shared.Contracts.Events;
using AiStockTrading.Shared.Kernel.Trading;
using AwesomeAssertions;
using Microsoft.Extensions.DependencyInjection;
using Wolverine.Tracking;
using Xunit;

namespace ConfigurationService.Tests;

// FR-17, UC-06: 前提条件エンドポイントの認可・更新・履歴・楽観排他（409）・変更イベント発行を検証する。
// IADR-0063 決定 2: 読み取り（GET /assumptions）のみ OwnerOrService、更新・履歴は OwnerOnly 据え置き（最小権限）。
// 単一行の前提条件は可変状態のため、テストごとに独立した Factory（＝独立 InMemory DB）を用いる。
public class AssumptionsEndpointsTests
{
    private const string OwnerRole = "trading-owner";
    private const string ServiceRole = "trading-service";

    private static HttpClient OwnerClient(ConfigurationWorkerWebApplicationFactory factory) =>
        ClientWithRole(factory, OwnerRole);

    private static HttpClient ServiceClient(ConfigurationWorkerWebApplicationFactory factory) =>
        ClientWithRole(factory, ServiceRole);

    private static HttpClient ClientWithRole(ConfigurationWorkerWebApplicationFactory factory, string role)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add(TestAuthHandler.RolesHeader, role);
        return client;
    }

    private static object UpdateBody(TradingAssumptions assumptions, int expectedVersion, string reason) =>
        new { Assumptions = assumptions, ExpectedVersion = expectedVersion, Reason = reason };

    [Fact]
    public async Task 未認証は_401_ロール無しは_403()
    {
        await using var factory = new ConfigurationWorkerWebApplicationFactory();

        (await factory.CreateClient().GetAsync("/assumptions")).StatusCode
            .Should().Be(HttpStatusCode.Unauthorized);

        var noRole = factory.CreateClient();
        noRole.DefaultRequestHeaders.Add(TestAuthHandler.RolesHeader, "other");
        (await noRole.GetAsync("/assumptions")).StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task 現在値は既定を_Version1_で返す()
    {
        await using var factory = new ConfigurationWorkerWebApplicationFactory();

        var res = await OwnerClient(factory).GetAsync("/assumptions");
        res.StatusCode.Should().Be(HttpStatusCode.OK);

        var current = await res.Content.ReadFromJsonAsync<VersionedDto>();
        current!.Version.Should().Be(1);
        current.Assumptions.CapitalGainsTaxRate.Should().Be(0.20315m);
    }

    [Fact]
    public async Task 更新で_Version_が上がり_AssumptionsChanged_が発行され履歴に残る()
    {
        await using var factory = new ConfigurationWorkerWebApplicationFactory();
        var client = OwnerClient(factory);

        var modified = TradingAssumptionsDefaults.Create() with { FxSpreadRatio = 0.003m };
        HttpResponseMessage put = null!;
        // ADR-0013, IADR-0129, #354: MassTransit の ITestHarness に代えて Wolverine.Tracking で発行を捕捉する
        // （PUT の処理中に外へ出たメッセージが収束するまで待つ）。
        var session = await factory.Services.ExecuteAndWaitAsync(async () =>
        {
            put = await client.PutAsJsonAsync("/assumptions", UpdateBody(modified, 1, "為替スプレッド登録"));
        });
        put.StatusCode.Should().Be(HttpStatusCode.OK);

        var updated = await put.Content.ReadFromJsonAsync<VersionedDto>();
        updated!.Version.Should().Be(2);
        updated.Assumptions.FxSpreadRatio.Should().Be(0.003m);

        // 変更通知イベントが発行される（IADR-0129 決定 2: 宛先はメッセージ型ごとの共有 fanout exchange。
        // exchange 名は Wolverine 既定＝メッセージ型の完全名であり、購読側サービスもここに bind する）。
        session.Sent.MessagesOf<AssumptionsChanged>().Should().NotBeEmpty();
        session.Sent.Envelopes().Should().Contain(e =>
            e.Message is AssumptionsChanged
            && e.Destination!.ToString()
                == "rabbitmq://exchange/AiStockTrading.Shared.Contracts.Events.AssumptionsChanged");

        // 履歴に残る。
        var history = await client.GetFromJsonAsync<List<ChangeDto>>("/assumptions/history");
        history!.Should().ContainSingle(h => h.Reason == "為替スプレッド登録" && h.Version == 2);
    }

    [Fact]
    public async Task 版番号が不一致の更新は_409()
    {
        await using var factory = new ConfigurationWorkerWebApplicationFactory();
        var client = OwnerClient(factory);

        var put = await client.PutAsJsonAsync("/assumptions",
            UpdateBody(TradingAssumptionsDefaults.Create(), expectedVersion: 99, reason: "遅延更新"));

        put.StatusCode.Should().Be(HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task 理由が空の更新は_400()
    {
        await using var factory = new ConfigurationWorkerWebApplicationFactory();
        var client = OwnerClient(factory);

        var put = await client.PutAsJsonAsync("/assumptions",
            UpdateBody(TradingAssumptionsDefaults.Create(), expectedVersion: 1, reason: ""));

        put.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    // IADR-0063 決定 2: 消費側（費用統制 #139・損益計算・AI 判断）はサービストークンで現在値と Version を読める。
    [Fact]
    public async Task サービストークンは現在値を読める()
    {
        await using var factory = new ConfigurationWorkerWebApplicationFactory();

        var res = await ServiceClient(factory).GetAsync("/assumptions");
        res.StatusCode.Should().Be(HttpStatusCode.OK);

        var current = await res.Content.ReadFromJsonAsync<VersionedDto>();
        current!.Version.Should().Be(1);
        current.Assumptions.CostLimits.Total.Should().Be(20_000m);
    }

    // FR-17 / IADR-0063 決定 2: 変更は利用者のみ。履歴（誰がなぜ変えたか）もサービスへは開放しない（最小権限）。
    [Fact]
    public async Task サービストークンは更新と履歴を拒否される()
    {
        await using var factory = new ConfigurationWorkerWebApplicationFactory();
        var client = ServiceClient(factory);

        var put = await client.PutAsJsonAsync("/assumptions",
            UpdateBody(TradingAssumptionsDefaults.Create() with { FxSpreadRatio = 0.9m }, 1, "サービスによる変更"));
        put.StatusCode.Should().Be(HttpStatusCode.Forbidden);

        (await client.GetAsync("/assumptions/history")).StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    private sealed record VersionedDto(TradingAssumptions Assumptions, int Version);

    private sealed record ChangeDto(string Actor, string Reason, int Version);
}

using System.Net;
using System.Net.Http.Json;
using RiskManagementService.Features.RiskManagement;
using AiStockTrading.Shared.Contracts.Trading;
using AwesomeAssertions;
using Xunit;

namespace RiskManagementService.Tests;

// FR-10, FR-12, FR-11, UC-06, SC-02, SC-03, ADR-0040 決定1・決定3, #819, IADR-0342 決定2:
// `PUT /risk-controls/settings/stop-loss-method` の検証。
//
// **本テスト群が守るのは「画面を経由しなくても止まる」ことである**（発注先の変更と同じ）。
//   - 変更は利用者のみ（生成 AI・サービス間呼び出しは変更できない）
//   - 実弾（moomoo REAL）では S0 以外を選べない／S0 以外が有効なまま実弾へ切り替えられない
//
// 注意: 本 fixture は 1 つの DB を共有するため、手法・発注先を変えるテストは**最後に既定へ戻す**。
public class StopLossMethodEndpointTests(RiskWorkerWebApplicationFactory factory)
    : IClassFixture<RiskWorkerWebApplicationFactory>
{
    private const string Path = "/risk-controls/settings/stop-loss-method";
    private const string ProviderPath = "/risk-controls/settings/broker-provider";

    private HttpClient OwnerClient()
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add(TestAuthHandler.RolesHeader, "trading-owner");
        return client;
    }

    private static async Task RestoreAsync(HttpClient client)
    {
        (await client.PutAsJsonAsync(Path, new { method = 0, reason = "テストの片付け" }))
            .StatusCode.Should().Be(HttpStatusCode.OK);
        (await client.PutAsJsonAsync(ProviderPath, new { provider = (int)BrokerProvider.InternalPaper, reason = "テストの片付け" }))
            .StatusCode.Should().Be(HttpStatusCode.OK);
    }

    // ---- 認可（ADR-0040 決定3: 生成 AI は上書きできない） ----

    [Fact]
    public async Task 未認証の変更は401()
    {
        var res = await factory.CreateClient().PutAsJsonAsync(Path, new { method = 2, reason = "検証" });

        res.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task サービスロールの変更は403()
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add(TestAuthHandler.RolesHeader, "trading-service");

        var res = await client.PutAsJsonAsync(Path, new { method = 2, reason = "検証" });

        res.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    // ---- 肯定形: SIMULATE で S2 を選べ、設定・統制状態・履歴に現れる ----

    [Fact]
    public async Task SIMULATEではS2を選べ設定と統制状態と履歴に現れる()
    {
        var client = OwnerClient();
        (await client.PutAsJsonAsync(ProviderPath, new { provider = (int)BrokerProvider.MoomooSimulate, reason = "PoC" }))
            .StatusCode.Should().Be(HttpStatusCode.OK);

        var res = await client.PutAsJsonAsync(Path, new { method = 2, reason = "ペーパーでは逆指値が置けないため建玉を残す" });

        res.StatusCode.Should().Be(HttpStatusCode.OK);
        (await client.GetFromJsonAsync<SettingsDto>("/risk-controls/settings"))!
            .StopLossMethod.Should().Be(StopLossExecutionMethod.NoProtectiveStop);
        (await client.GetFromJsonAsync<StatusDto>("/risk-controls/status"))!
            .StopLossMethod.Should().Be(StopLossExecutionMethod.NoProtectiveStop, "SC-03 は選択中の手法を参照する");
        var history = await client.GetFromJsonAsync<List<HistoryDto>>("/risk-controls/settings/history");
        history.Should().Contain(h =>
            h.ChangeType == SettingsChangeType.StopLossMethodChanged
            && h.Reason == "ペーパーでは逆指値が置けないため建玉を残す"
            && h.Before == nameof(StopLossExecutionMethod.BrokerStopOrder)
            && h.After == nameof(StopLossExecutionMethod.NoProtectiveStop));

        await RestoreAsync(client);
    }

    // ---- 否定形: 実弾では S0 以外を選べない ----

    [Fact]
    public async Task 実弾ではS0以外への変更は400で設定も履歴も変わらない()
    {
        var client = OwnerClient();
        (await client.PutAsJsonAsync(ProviderPath, new
        {
            provider = (int)BrokerProvider.MoomooReal,
            reason = "実弾へ移行する",
            acknowledgedLiveTrading = true,
            acknowledgement = "REAL",
        })).StatusCode.Should().Be(HttpStatusCode.OK);
        var historyBefore = (await client.GetFromJsonAsync<List<HistoryDto>>("/risk-controls/settings/history"))!.Count;

        var res = await client.PutAsJsonAsync(Path, new { method = 2, reason = "実弾でも試したい" });

        res.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await res.Content.ReadAsStringAsync()).Should().Contain("moomoo REAL");
        (await client.GetFromJsonAsync<SettingsDto>("/risk-controls/settings"))!
            .StopLossMethod.Should().Be(StopLossExecutionMethod.BrokerStopOrder);
        (await client.GetFromJsonAsync<List<HistoryDto>>("/risk-controls/settings/history"))!
            .Count.Should().Be(historyBefore, "拒否された要求を履歴に積まない");

        await RestoreAsync(client);
    }

    [Fact]
    public async Task S0以外が有効なまま実弾へは確認操作が揃っていても切り替えられない()
    {
        var client = OwnerClient();
        (await client.PutAsJsonAsync(ProviderPath, new { provider = (int)BrokerProvider.MoomooSimulate, reason = "PoC" }))
            .StatusCode.Should().Be(HttpStatusCode.OK);
        (await client.PutAsJsonAsync(Path, new { method = 2, reason = "建玉を残す" }))
            .StatusCode.Should().Be(HttpStatusCode.OK);

        var res = await client.PutAsJsonAsync(ProviderPath, new
        {
            provider = (int)BrokerProvider.MoomooReal,
            reason = "実弾へ移行する",
            acknowledgedLiveTrading = true,
            acknowledgement = "REAL",
        });

        res.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await client.GetFromJsonAsync<SettingsDto>("/risk-controls/settings"))!
            .BrokerProvider.Should().Be(BrokerProvider.MoomooSimulate);

        await RestoreAsync(client);
    }

    // ---- 入力の検証 ----

    [Fact]
    public async Task 手法の省略は400()
    {
        var res = await OwnerClient().PutAsJsonAsync(Path, new { reason = "method を書き忘れた" });

        res.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task 理由が空の変更は400(string reason)
    {
        var res = await OwnerClient().PutAsJsonAsync(Path, new { method = 2, reason });

        res.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task 未知の手法は400()
    {
        var res = await OwnerClient().PutAsJsonAsync(Path, new { method = 9, reason = "検証" });

        res.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    private sealed record SettingsDto(BrokerProvider BrokerProvider, StopLossExecutionMethod StopLossMethod);

    private sealed record StatusDto(StopLossExecutionMethod StopLossMethod);

    private sealed record HistoryDto(
        string Actor, SettingsChangeType ChangeType, string Reason, string? Before, string? After);
}

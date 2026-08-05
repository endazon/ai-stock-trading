using System.Net;
using System.Net.Http.Json;
using AiStockTrading.RiskManagement.Domain;
using AiStockTrading.Shared.Contracts.Trading;
using AwesomeAssertions;
using Xunit;

namespace AiStockTrading.RiskManagement.Api.Tests;

// FR-20, FR-13, UC-06, SC-02, INDEX 決定 46, #334, IADR-0141:
// 発注先の変更エンドポイント `PUT /risk-controls/settings/broker-provider` の検証。
//
// **本テスト群が守るのは「画面を経由しなくても止まる」ことである。** 計画（FR-20 (1)）の
// 「既定の『OK』ボタン 1 押しで切り替えられてはならない」を画面だけに置くと、API を直接叩けば統制は消える。
//
// 注意: 本 fixture は 1 つの DB を共有するため、実弾へ切り替えるテストは**最後に内蔵 paper へ戻す**
// （後続テストへ実弾状態を持ち越さない。既存の pause テストと同じ片付けの流儀）。
public class BrokerProviderEndpointTests(RiskWorkerWebApplicationFactory factory)
    : IClassFixture<RiskWorkerWebApplicationFactory>
{
    private const string Owner = "trading-owner";

    private HttpClient OwnerClient()
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add(TestAuthHandler.RolesHeader, Owner);
        return client;
    }

    [Fact]
    public async Task 未認証の発注先変更は401()
    {
        var client = factory.CreateClient();

        var res = await client.PutAsJsonAsync(
            "/risk-controls/settings/broker-provider",
            new { provider = 2, reason = "検証開始" });

        res.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task 利用者ロールを持たない発注先変更は403()
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add(TestAuthHandler.RolesHeader, "viewer");

        var res = await client.PutAsJsonAsync(
            "/risk-controls/settings/broker-provider",
            new { provider = 2, reason = "検証開始" });

        res.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    // 否定形（本 issue の中核）: 確認操作を欠いた実弾切替を API 直叩きで通せない。
    [Fact]
    public async Task 確認操作を欠いた実弾への切替は400で拒否され設定が変わらない()
    {
        var client = OwnerClient();
        var before = await client.GetFromJsonAsync<SettingsDto>("/risk-controls/settings");

        var res = await client.PutAsJsonAsync(
            "/risk-controls/settings/broker-provider",
            new { provider = (int)BrokerProvider.MoomooReal, reason = "実弾へ移行する" });

        res.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var after = await client.GetFromJsonAsync<SettingsDto>("/risk-controls/settings");
        after!.BrokerProvider.Should().Be(before!.BrokerProvider);
        after.BrokerProvider.Should().NotBe(BrokerProvider.MoomooReal);
    }

    // 否定形: 同意フラグだけ・文字入力だけの片方では通らない。
    [Theory]
    [InlineData(true, null)]
    [InlineData(false, "REAL")]
    [InlineData(true, "real")]
    public async Task 実弾への切替は同意と文字入力の両方が揃うまで400である(bool acknowledged, string? phrase)
    {
        var client = OwnerClient();

        var res = await client.PutAsJsonAsync(
            "/risk-controls/settings/broker-provider",
            new
            {
                provider = (int)BrokerProvider.MoomooReal,
                reason = "実弾へ移行する",
                acknowledgedLiveTrading = acknowledged,
                acknowledgement = phrase,
            });

        res.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    // 否定形: 変更理由が空欄では保存できない（FR-13）。
    [Fact]
    public async Task 理由が空の発注先変更は400である()
    {
        var client = OwnerClient();

        var res = await client.PutAsJsonAsync(
            "/risk-controls/settings/broker-provider",
            new { provider = (int)BrokerProvider.MoomooSimulate, reason = "   " });

        res.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    // 否定形: provider の省略は 400。非 nullable enum で受けると既定値 0（内蔵 paper）へ暗黙束縛され、
    // 「送っていない値へ黙って切り替わる」経路になる。
    [Fact]
    public async Task 発注先の省略は400である()
    {
        var client = OwnerClient();

        var res = await client.PutAsJsonAsync(
            "/risk-controls/settings/broker-provider",
            new { reason = "理由はあるが provider を書き忘れた" });

        res.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    // 安全な方向（moomoo SIMULATE）への切替は理由だけで通り、履歴に残る。
    [Fact]
    public async Task SIMULATEへの切替は理由だけで受理され履歴に残る()
    {
        var client = OwnerClient();

        var res = await client.PutAsJsonAsync(
            "/risk-controls/settings/broker-provider",
            new { provider = (int)BrokerProvider.MoomooSimulate, reason = "Stage 1 の検証を開始する" });

        res.StatusCode.Should().Be(HttpStatusCode.OK);

        var settings = await client.GetFromJsonAsync<SettingsDto>("/risk-controls/settings");
        settings!.BrokerProvider.Should().Be(BrokerProvider.MoomooSimulate);

        var history = await client.GetFromJsonAsync<List<SettingsChangeDto>>("/risk-controls/settings/history");
        history.Should().Contain(h =>
            h.ChangeType == Application.State.SettingsChangeType.BrokerProviderChanged
            && h.Reason == "Stage 1 の検証を開始する");

        // 片付け: 後続テストへ SIMULATE 状態を持ち越さない。
        await client.PutAsJsonAsync(
            "/risk-controls/settings/broker-provider",
            new { provider = (int)BrokerProvider.InternalPaper, reason = "テストの片付け" });
    }

    // 統制状態（SC-03 の参照表示）に発注先が載る。段階とは別のフィールドである（1 行に混ぜない）。
    [Fact]
    public async Task 統制状態は発注先を段階とは別のフィールドとして返す()
    {
        var client = OwnerClient();

        var status = await client.GetFromJsonAsync<StatusDto>("/risk-controls/status");

        status.Should().NotBeNull();
        Enum.IsDefined(status!.BrokerProvider).Should().BeTrue();
        // 1 注文上限の実額（実弾切替モーダル③の提示に用いる）も返る。
        status.MaxOrderAmount.Should().BeGreaterThan(0m);
    }

    // 応答 JSON の受け皿（プロパティ名は web 既定 = camelCase）。
    private sealed record SettingsDto(BrokerProvider BrokerProvider);

    private sealed record SettingsChangeDto(
        string Actor, Application.State.SettingsChangeType ChangeType, string Reason);

    private sealed record StatusDto(BrokerProvider BrokerProvider, decimal MaxOrderAmount);
}

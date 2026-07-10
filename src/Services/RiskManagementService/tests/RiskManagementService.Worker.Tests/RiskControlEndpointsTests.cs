using System.Net;
using System.Net.Http.Json;
using AiStockTrading.RiskManagement.Application.State;
using AiStockTrading.RiskManagement.Domain;
using AiStockTrading.RiskManagement.Worker.Foundation.Endpoints;
using FluentAssertions;
using Xunit;

namespace AiStockTrading.RiskManagement.Worker.Tests;

// FR-10, UC-06, ADR-0007: kill switch/設定エンドポイントの認可（OwnerOnly）と永続化・履歴を検証する。
public class RiskControlEndpointsTests(RiskWorkerWebApplicationFactory factory)
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
    public async Task 未認証の_kill_switch_取得は401()
    {
        // OwnerOnly: 未認証（X-Test-Roles ヘッダ無し）は 401。
        var client = factory.CreateClient();

        var res = await client.GetAsync("/risk-controls/kill-switch");

        res.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task 利用者ロールを持たない場合は403()
    {
        // 認証済みだが trading-owner を持たない → 403。
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add(TestAuthHandler.RolesHeader, "viewer");

        var res = await client.GetAsync("/risk-controls/kill-switch");

        res.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task 利用者は_kill_switch_を起動でき状態が永続化される()
    {
        var client = OwnerClient();

        var engage = await client.PostAsJsonAsync("/risk-controls/kill-switch/engage",
            new KillSwitchRequest("急変のため緊急停止"));
        engage.StatusCode.Should().Be(HttpStatusCode.OK);

        // 別リクエスト（別 DI スコープ）でも永続化された状態が読める。
        var state = await client.GetFromJsonAsync<KillSwitchStateDto>("/risk-controls/kill-switch");
        state!.Engaged.Should().BeTrue();
        state.Reason.Should().Be("急変のため緊急停止");
    }

    [Fact]
    public async Task 設定変更が履歴に記録される()
    {
        // FR-11, ADR-0007: 上限変更が変更履歴に残る。
        var client = OwnerClient();
        var limits = TradingDefaults.CreateRiskLimits() with { MaxOpenPositions = 5 };

        var put = await client.PutAsJsonAsync("/risk-controls/settings/limits",
            new LimitsUpdateRequest(limits, "検証結果に基づき保有数上限を緩和"));
        put.StatusCode.Should().Be(HttpStatusCode.OK);

        var history = await client.GetFromJsonAsync<List<SettingsChangeDto>>("/risk-controls/settings/history");
        history.Should().NotBeNull();
        history!.Should().Contain(e => e.ChangeType == SettingsChangeType.Limits && e.Actor == "test-owner");
    }

    [Fact]
    public async Task 理由が空の_kill_switch_操作は400()
    {
        // 検証失敗（ADR-0007: 理由必須）は既定の 500 ではなく 400 に写像する。
        var client = OwnerClient();

        var res = await client.PostAsJsonAsync("/risk-controls/kill-switch/engage",
            new KillSwitchRequest(""));

        res.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task 未認証の_sizing_context_取得は401()
    {
        // FR-04/10, IADR-0029: サイジング文脈も OwnerOnly。未認証は 401。
        var client = factory.CreateClient();

        var res = await client.GetAsync("/risk-controls/sizing-context");

        res.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task 利用者は_sizing_context_を取得できる()
    {
        // FR-04/10, IADR-0029: 設定＋ポートフォリオ状態から導出したサイジング文脈を返す。
        var client = OwnerClient();

        var view = await client.GetFromJsonAsync<SizingContextDto>("/risk-controls/sizing-context");

        view.Should().NotBeNull();
        // 段階/日次残枠は上限から使用分を引いた非負値（既定状態では上限＝残枠）。
        view!.StageCapitalRemaining.Should().BeGreaterThanOrEqualTo(0m);
        view.DailyOrderRemaining.Should().BeGreaterThanOrEqualTo(0m);
    }

    [Fact]
    public async Task ヘルスチェック_live_は認証不要で応答する()
    {
        var client = factory.CreateClient();

        var res = await client.GetAsync("/health/live");

        res.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    // 応答 JSON の受け皿（プロパティ名は web 既定=camelCase）。
    private sealed record KillSwitchStateDto(bool Engaged, string? Actor, string? Reason);

    private sealed record SettingsChangeDto(string Actor, SettingsChangeType ChangeType, string Reason);

    private sealed record SizingContextDto(decimal Capital, decimal StageCapitalRemaining, decimal DailyOrderRemaining);
}

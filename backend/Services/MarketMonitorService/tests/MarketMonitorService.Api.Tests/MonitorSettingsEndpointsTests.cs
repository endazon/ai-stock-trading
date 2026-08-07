using System.Net;
using System.Net.Http.Json;
using AiStockTrading.MarketMonitor.Domain;
using AiStockTrading.Shared.Contracts.Trading;
using AwesomeAssertions;
using Xunit;

namespace AiStockTrading.MarketMonitor.Api.Tests;

// FR-03, FR-13: 監視設定エンドポイントの認可（OwnerOnly）と永続化・反映を検証する。
public class MonitorSettingsEndpointsTests(MonitorWorkerWebApplicationFactory factory)
    : IClassFixture<MonitorWorkerWebApplicationFactory>
{
    private HttpClient OwnerClient()
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add(TestAuthHandler.RolesHeader, "trading-owner");
        return client;
    }

    [Fact]
    public async Task 未認証の設定取得は401()
    {
        var res = await factory.CreateClient().GetAsync("/monitor/settings");
        res.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task 利用者ロールを持たない場合は403()
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add(TestAuthHandler.RolesHeader, "viewer");

        var res = await client.GetAsync("/monitor/settings");

        res.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task 利用者は監視設定を更新でき永続化される()
    {
        var client = OwnerClient();
        var request = new
        {
            MovementThresholdRatio = 0.05m,
            Cooldown = TimeSpan.FromMinutes(10),
            MonitoredSymbols = new[] { new MonitoredSymbol("AAPL", Market.UnitedStates) },
            // FR-11, #423, IADR-0165 決定3: 全置換も**理由必須**である（部分更新と同じ規律）。
            Reason = "全置換の検証",
        };

        var put = await client.PutAsJsonAsync("/monitor/settings", request);
        put.StatusCode.Should().Be(HttpStatusCode.OK);

        var settings = await client.GetFromJsonAsync<SettingsDto>("/monitor/settings");
        settings!.MovementThresholdRatio.Should().Be(0.05m);
        settings.MonitoredSymbols.Should().ContainSingle(s => s.Symbol == "AAPL");
    }

    [Fact]
    public async Task 不正な閾値は400()
    {
        var client = OwnerClient();
        var request = new
        {
            MovementThresholdRatio = 0m, // 不正（正でない）
            Cooldown = TimeSpan.FromMinutes(10),
            MonitoredSymbols = Array.Empty<MonitoredSymbol>(),
            Reason = "値域外",
        };

        var res = await client.PutAsJsonAsync("/monitor/settings", request);

        res.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    // ---- #423, IADR-0165 決定3: 全置換も部分更新と同じ規律（値域・理由・履歴）である ----
    //
    // **本節は「画面から使わない経路」の否定形である。** 導入前、全置換 PUT は理由も履歴も持たず、
    // 値域も「正・非負」だけを見ていた。その状態では、SC-02 が 0.6（60%）を弾いても
    // **API を直接叩けば保存できた**——統制を画面の親切心に依存させることになる（IADR-0141 決定1）。

    // 値域の上限（0.50）は全置換でも効く。**部分更新だけを守っても意味が無い。**
    [Theory]
    [InlineData(0.51)]
    [InlineData(1.0)]
    public async Task 値域上限を超える閾値は全置換でも400(double ratio)
    {
        var res = await OwnerClient().PutAsJsonAsync("/monitor/settings", new
        {
            MovementThresholdRatio = (decimal)ratio,
            Cooldown = TimeSpan.FromMinutes(10),
            MonitoredSymbols = Array.Empty<MonitoredSymbol>(),
            Reason = "値域外",
        });

        res.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    // クールダウンの上限（24 時間）も全置換で効く。
    [Fact]
    public async Task 値域上限を超えるクールダウンは全置換でも400()
    {
        var res = await OwnerClient().PutAsJsonAsync("/monitor/settings", new
        {
            MovementThresholdRatio = 0.03m,
            Cooldown = TimeSpan.FromHours(25),
            MonitoredSymbols = Array.Empty<MonitoredSymbol>(),
            Reason = "値域外",
        });

        res.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    // FR-11: 理由なしでは保存できない（#423 の退行防止項目「変更理由なしでは保存できないこと」）。
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task 理由なしの全置換は400(string? reason)
    {
        var res = await OwnerClient().PutAsJsonAsync("/monitor/settings", new
        {
            MovementThresholdRatio = 0.04m,
            Cooldown = TimeSpan.FromMinutes(10),
            MonitoredSymbols = Array.Empty<MonitoredSymbol>(),
            Reason = reason,
        });

        res.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    // FR-11: 全置換で変えた値も**監査ログ（変更履歴）に残る**（#423 の退行防止項目）。
    [Fact]
    public async Task 全置換で変えた変動閾値とクールダウンは履歴に残る()
    {
        var client = OwnerClient();

        var res = await client.PutAsJsonAsync("/monitor/settings", new
        {
            MovementThresholdRatio = 0.11m,
            Cooldown = TimeSpan.FromMinutes(37),
            MonitoredSymbols = Array.Empty<MonitoredSymbol>(),
            Reason = "全置換で履歴に残ることの検証",
        });
        res.StatusCode.Should().Be(HttpStatusCode.OK);

        var history = await client.GetFromJsonAsync<List<HistoryDto>>("/monitor/settings/history");
        history.Should().Contain(e =>
            e.ChangeType == Application.State.MonitorSettingsChangeType.MovementThresholdChanged
            && e.Reason == "全置換で履歴に残ることの検証");
        history.Should().Contain(e =>
            e.ChangeType == Application.State.MonitorSettingsChangeType.CooldownChanged
            && e.Reason == "全置換で履歴に残ることの検証");
    }

    [Fact]
    public async Task ヘルスチェック_live_は認証不要で応答する()
    {
        var res = await factory.CreateClient().GetAsync("/health/live");
        res.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    private sealed record SettingsDto(decimal MovementThresholdRatio, List<MonitoredSymbol> MonitoredSymbols);

    private sealed record HistoryDto(
        string Actor, Application.State.MonitorSettingsChangeType ChangeType, string Reason);
}

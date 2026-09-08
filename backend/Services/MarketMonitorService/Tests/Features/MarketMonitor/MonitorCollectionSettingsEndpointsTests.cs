using System.Net;
using System.Net.Http.Json;
using MarketMonitorService.Features.MarketMonitor;
using MarketMonitorService.Domain;
using AwesomeAssertions;
using Xunit;

namespace MarketMonitorService.Tests;

// FR-03, FR-11, FR-13, UC-06, SC-01 §2, #340, IADR-0155:
// 収集パラメータ（変動閾値・クールダウン）の部分更新エンドポイントの認可・検証(400)・履歴を検証する。
//
// **画面（SC-01 §2）にも値域の表があるが、実効はここである**——画面だけの関門は API 直叩きで消える
// （IADR-0141 決定1 と同じ判断）。したがって値域外・理由欠如をサーバが 400 で弾くことをここで固定する。
//
// #707, IADR-0317: ステータスの断定は `ShouldHaveStatusAsync`（応答本文つき）を通す。素の断定は
// 400 の理由を隠し、**インフラ層の障害を「検証で弾かれた」と読み違えさせる**。
public class MonitorCollectionSettingsEndpointsTests(MonitorWorkerWebApplicationFactory factory)
    : IClassFixture<MonitorWorkerWebApplicationFactory>
{
    private HttpClient OwnerClient()
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add(TestAuthHandler.RolesHeader, "trading-owner");
        return client;
    }

    private static Task<HttpResponseMessage> PutThresholdAsync(HttpClient client, object body) =>
        client.PutAsJsonAsync("/monitor/settings/movement-threshold", body);

    private static Task<HttpResponseMessage> PutCooldownAsync(HttpClient client, object body) =>
        client.PutAsJsonAsync("/monitor/settings/cooldown", body);

    [Fact]
    public async Task 未認証の変動閾値変更は401()
    {
        var res = await PutThresholdAsync(
            factory.CreateClient(), new { movementThresholdRatio = 0.05m, reason = "検証" });

        await res.ShouldHaveStatusAsync(HttpStatusCode.Unauthorized);
    }

    // FR-13: 変更は利用者のみ。サービストークン（trading-service）にも開かない。
    [Fact]
    public async Task サービスロールの変動閾値変更は403()
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add(TestAuthHandler.RolesHeader, "trading-service");

        var res = await PutThresholdAsync(client, new { movementThresholdRatio = 0.05m, reason = "検証" });

        await res.ShouldHaveStatusAsync(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task 利用者は変動閾値を変更でき取得と履歴に反映される()
    {
        var client = OwnerClient();

        var res = await PutThresholdAsync(client, new { movementThresholdRatio = 0.07m, reason = "ボラティリティ上昇" });
        await res.ShouldHaveStatusAsync(HttpStatusCode.OK);

        var settings = await client.GetFromJsonAsync<SettingsDto>("/monitor/settings");
        settings!.MovementThresholdRatio.Should().Be(0.07m);

        var history = await client.GetFromJsonAsync<List<HistoryDto>>("/monitor/settings/history");
        history.Should().Contain(e =>
            e.ChangeType == MonitorSettingsChangeType.MovementThresholdChanged && e.Reason == "ボラティリティ上昇");
    }

    // FR-11: 監査のため理由必須。空欄は 400 で、設定も履歴も一切変えない。
    [Fact]
    public async Task 理由が空の変動閾値変更は400()
    {
        var res = await PutThresholdAsync(OwnerClient(), new { movementThresholdRatio = 0.05m, reason = "" });
        await res.ShouldHaveStatusAsync(HttpStatusCode.BadRequest);
    }

    // 値域外はサーバが弾く（画面の検証は即時提示にすぎない）。**黙って既定値へ丸めない。**
    [Theory]
    [InlineData(0)]
    [InlineData(-0.01)]
    [InlineData(0.51)]
    public async Task 値域外の変動閾値は400(double ratio)
    {
        var res = await PutThresholdAsync(
            OwnerClient(), new { movementThresholdRatio = (decimal)ratio, reason = "値域外" });

        await res.ShouldHaveStatusAsync(HttpStatusCode.BadRequest);
    }

    // 省略（null）は 400。非 nullable decimal で受けると既定値 0 へ暗黙束縛され、
    // 「送っていない値へ黙って切り替わる」経路になる（BrokerProviderUpdateRequest.Provider と同じ規律）。
    [Fact]
    public async Task 変動閾値を省略すると400()
    {
        var res = await PutThresholdAsync(OwnerClient(), new { reason = "省略" });
        await res.ShouldHaveStatusAsync(HttpStatusCode.BadRequest);
    }

    // SC-01 §2 の部分更新は監視銘柄を巻き込まない（全置換 PUT との決定的な違い）。
    [Fact]
    public async Task 変動閾値の変更は監視銘柄を巻き込まない()
    {
        var client = OwnerClient();
        var added = await client.PostAsJsonAsync("/monitor/watchlist",
            new { Symbol = "MSFT", Market = AiStockTrading.Shared.Contracts.Trading.Market.UnitedStates, Reason = "監視開始" });
        added.IsSuccessStatusCode.Should().BeTrue("応答本文: {0}", await added.Content.ReadAsStringAsync());

        await PutThresholdAsync(client, new { movementThresholdRatio = 0.09m, reason = "引き上げ" });

        var list = await client.GetFromJsonAsync<List<MonitoredSymbol>>("/monitor/watchlist");
        list.Should().Contain(s => s.Symbol == "MSFT");
    }

    [Fact]
    public async Task 利用者はクールダウンを変更できる()
    {
        var client = OwnerClient();

        var res = await PutCooldownAsync(client, new { cooldown = "00:45:00", reason = "再トリガー抑制を延ばす" });

        await res.ShouldHaveStatusAsync(HttpStatusCode.OK);
        var settings = await client.GetFromJsonAsync<SettingsDto>("/monitor/settings");
        settings!.Cooldown.Should().Be(TimeSpan.FromMinutes(45));
    }

    // #707, IADR-0317 陽性対照: **わざと汚した状態から始めても緑になる。**
    // クラス内の実行順序は保証されないため、「先行テストが残した値」を拾って落ちないことを明示的に固定する。
    // 汚し方は 3 つ（別の値へのクールダウン変更・変動閾値の変更・監視銘柄の追加）をまとめて掛ける。
    [Fact]
    public async Task 汚れた状態から始めてもクールダウンは変更できる()
    {
        var client = OwnerClient();
        await (await PutCooldownAsync(client, new { cooldown = "00:05:00", reason = "汚染" }))
            .ShouldHaveStatusAsync(HttpStatusCode.OK);
        await (await PutThresholdAsync(client, new { movementThresholdRatio = 0.21m, reason = "汚染" }))
            .ShouldHaveStatusAsync(HttpStatusCode.OK);
        await (await client.PostAsJsonAsync("/monitor/watchlist",
                new { Symbol = "TSLA", Market = AiStockTrading.Shared.Contracts.Trading.Market.UnitedStates, Reason = "汚染" }))
            .ShouldHaveStatusAsync(HttpStatusCode.OK);

        var res = await PutCooldownAsync(client, new { cooldown = "01:30:00", reason = "汚染後の変更" });

        await res.ShouldHaveStatusAsync(HttpStatusCode.OK);
        var settings = await client.GetFromJsonAsync<SettingsDto>("/monitor/settings");
        settings!.Cooldown.Should().Be(TimeSpan.FromMinutes(90));
    }

    [Fact]
    public async Task 値域外のクールダウンは400()
    {
        var res = await PutCooldownAsync(OwnerClient(), new { cooldown = "-00:01:00", reason = "負値" });

        await res.ShouldHaveStatusAsync(HttpStatusCode.BadRequest);
    }

    private sealed record SettingsDto(decimal MovementThresholdRatio, TimeSpan Cooldown);

    private sealed record HistoryDto(string Actor, MonitorSettingsChangeType ChangeType, string Reason);
}

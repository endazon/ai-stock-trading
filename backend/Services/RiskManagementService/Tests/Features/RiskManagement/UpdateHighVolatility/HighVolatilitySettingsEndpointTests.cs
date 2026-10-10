using System.Net;
using System.Net.Http.Json;
using System.Text.RegularExpressions;
using RiskManagementService.Domain;
using RiskManagementService.Features.RiskManagement;
using RiskManagementService.Features.RiskManagement.GetSizingContext;
using RiskManagementService.Infrastructure.Persistence;
using AiStockTrading.Shared.Contracts.Trading;
using AwesomeAssertions;
using Xunit;

namespace RiskManagementService.Tests;

// FR-10, FR-11, UC-06, ADR-0063 決定1・決定2, #1291, IADR-0527 決定2: 高ボラティリティ銘柄の統制値（区分の上限・利用者の明示指定）の
// 永続化（旧行・値域外の読み方）と `PUT /risk-controls/settings/high-volatility`（利用者のみ・理由必須・値域外は 400・履歴に残す）。
public class HighVolatilitySettingsEndpointTests(RiskWorkerWebApplicationFactory factory)
    : IClassFixture<RiskWorkerWebApplicationFactory>
{
    private const string Path = "/risk-controls/settings/high-volatility";

    private HttpClient OwnerClient()
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add(TestAuthHandler.RolesHeader, "trading-owner");
        return client;
    }

    private static async Task RestoreAsync(HttpClient client) =>
        (await client.PutAsJsonAsync(Path, new { maxOrderAmountRatio = 0.05m, designatedSymbols = Array.Empty<object>(), reason = "テストの片付け" }))
            .StatusCode.Should().Be(HttpStatusCode.OK);

    // ---- 永続化 ----

    // T-10-2559, ADR-0063 決定2: 本項目を持たない旧行は既定（5%・明示指定なし）で読む（不在を「区分の上限なし」にしない）。
    [Fact]
    public void T_10_2559_統制値を持たない旧行は既定の5パーセントで読む()
    {
        var legacy = Regex.Replace(
            RiskSettingsSerialization.Serialize(TradingDefaults.CreateSettings()), ",\"highVolatility\":\\{[^}]*\\}", string.Empty);
        legacy.Should().NotContain("highVolatility", "旧行の再現に失敗している");

        RiskSettingsSerialization.Deserialize(legacy).HighVolatility.Should().Be(TradingDefaults.CreateHighVolatilitySettings());
    }

    // T-10-2560, ADR-0063 決定1・決定2: 明示指定と上限は往復する。値域外の永続値は既定の 5% で読む（明示指定は残す）。
    [Theory]
    [InlineData(0.03, 0.03)]
    [InlineData(0.5, 0.05)]
    [InlineData(0.001, 0.05)]
    public void T_10_2560_統制値は往復し値域外の比率は既定で読む(double persisted, double expected)
    {
        var settings = TradingDefaults.CreateSettings() with
        {
            HighVolatility = new HighVolatilitySettings
            {
                MaxOrderAmountRatio = (decimal)persisted,
                DesignatedSymbols = [new HighVolatilitySymbol("TSLA", Market.UnitedStates)],
            },
        };

        var restored = RiskSettingsSerialization.Deserialize(RiskSettingsSerialization.Serialize(settings)).HighVolatility;

        restored.MaxOrderAmountRatio.Should().Be((decimal)expected);
        restored.DesignatedSymbols.Should().Equal(new HighVolatilitySymbol("TSLA", Market.UnitedStates));
    }

    // ---- 認可（生成 AI・サービス間呼び出しは変更できない。ADR-0063 決定1「指定できるのは利用者だけ」） ----

    [Fact]
    public async Task T_10_2561_サービスロールと未認証は明示指定を変更できない()
    {
        var service = factory.CreateClient();
        service.DefaultRequestHeaders.Add(TestAuthHandler.RolesHeader, "trading-service");
        var body = new { maxOrderAmountRatio = 0.05m, designatedSymbols = new[] { new { symbol = "TSLA", market = 1 } }, reason = "検証" };

        (await factory.CreateClient().PutAsJsonAsync(Path, body)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await service.PutAsJsonAsync(Path, body)).StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    // T-10-2562, FR-11, UC-06: 利用者の変更は設定・サイジング文脈・履歴（HighVolatilityChanged・前後値）に現れる。
    [Fact]
    public async Task T_10_2562_利用者の明示指定は設定とサイジング文脈と履歴に現れる()
    {
        var client = OwnerClient();

        var res = await client.PutAsJsonAsync(Path, new
        {
            maxOrderAmountRatio = 0.04m,
            designatedSymbols = new[] { new { symbol = " TSLA ", market = (int)Market.UnitedStates } },
            reason = "値動きの大きい銘柄を区分に入れる",
        });

        res.StatusCode.Should().Be(HttpStatusCode.OK);
        var sizing = (await client.GetFromJsonAsync<SizingContextView>("/risk-controls/sizing-context"))!;
        sizing.HighVolatility!.MaxOrderAmountRatio.Should().Be(0.04m);
        sizing.HighVolatility.DesignatedSymbols.Should().Equal(new HighVolatilitySymbol("TSLA", Market.UnitedStates));
        var history = await client.GetFromJsonAsync<List<HistoryDto>>("/risk-controls/settings/history");
        history.Should().Contain(h =>
            h.ChangeType == SettingsChangeType.HighVolatilityChanged
            && h.Reason == "値動きの大きい銘柄を区分に入れる"
            && h.After!.Contains("TSLA(UnitedStates)")
            && h.After.Contains("0.04"));

        await RestoreAsync(client);
    }

    // T-10-2563, ADR-0063 決定2: 値域外（最小の名目額の比率未満・25% 超）・理由なし・項目の省略は 400 で、設定も履歴も変わらない。
    [Theory]
    [InlineData("low")]
    [InlineData("high")]
    [InlineData("noReason")]
    [InlineData("noSymbols")]
    public async Task T_10_2563_値域外と理由なしと省略は400で設定も履歴も変わらない(string kind)
    {
        var client = OwnerClient();
        var historyBefore = (await client.GetFromJsonAsync<List<HistoryDto>>("/risk-controls/settings/history"))!.Count;
        object body = kind switch
        {
            "low" => new { maxOrderAmountRatio = 0.009m, designatedSymbols = Array.Empty<object>(), reason = "検証" },
            "high" => new { maxOrderAmountRatio = 0.26m, designatedSymbols = Array.Empty<object>(), reason = "検証" },
            "noReason" => new { maxOrderAmountRatio = 0.05m, designatedSymbols = Array.Empty<object>(), reason = " " },
            _ => new { maxOrderAmountRatio = 0.05m, reason = "検証" },
        };

        (await client.PutAsJsonAsync(Path, body)).StatusCode.Should().Be(HttpStatusCode.BadRequest);

        var sizing = (await client.GetFromJsonAsync<SizingContextView>("/risk-controls/sizing-context"))!;
        sizing.HighVolatility.Should().Be(TradingDefaults.CreateHighVolatilitySettings());
        (await client.GetFromJsonAsync<List<HistoryDto>>("/risk-controls/settings/history"))!.Count.Should().Be(historyBefore);
    }

    private sealed record HistoryDto(string Actor, SettingsChangeType ChangeType, string Reason, string? Before, string? After);
}

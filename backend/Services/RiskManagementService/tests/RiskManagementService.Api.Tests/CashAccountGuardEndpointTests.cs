using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using RiskManagementService.Application.Ports;
using AiStockTrading.Shared.Contracts.Trading;
using AwesomeAssertions;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace RiskManagementService.Api.Tests;

// FR-19, UC-06, SC-02, #375, ADR-0021 決定3/決定4-4, IADR-0153:
// 取引ガード更新エンドポイント（`PUT /risk-controls/settings/guard`）における口座種別の扱い。
//
// 本エンドポイントは**全置換**である。画面（SC-02）は口座種別を編集しないため、送られてくる本文には
// 当該キーが無い。非 nullable enum で受けると**送り漏らした瞬間に既定値 0（＝信用口座）へ黙って戻る**——
// 禁止銘柄を 1 件足しただけで口座種別の設定が消える。BrokerProviderUpdateRequest.Provider と同じ規律で塞ぐ。
public class CashAccountGuardEndpointTests
{
    private const string Owner = "trading-owner";

    private static HttpClient OwnerClient(RiskWorkerWebApplicationFactory factory)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add(TestAuthHandler.RolesHeader, Owner);
        return client;
    }

    // 応答は生 JSON として読む。ドメインの設定レコードは required init プロパティを持ち、
    // System.Text.Json では往復できない（テストの都合でドメイン型を緩めない）。
    private static async Task<JsonElement> GuardOfAsync(HttpClient client)
    {
        using var doc = JsonDocument.Parse(await client.GetStringAsync("/risk-controls/settings"));
        return doc.RootElement.GetProperty("guard").Clone();
    }

    // 口座種別を含まない（＝SC-02 が実際に送る形の）ガード更新本文。
    private static object GuardBody(string reason, params ProductType[] productTypes) => new
    {
        enabledProductTypes = productTypes.Select(p => (int)p).ToArray(),
        enabledMarkets = new[] { (int)Market.Japan, (int)Market.UnitedStates },
        bannedSymbols = Array.Empty<object>(),
        preventSameDayReentry = true,
        prohibitManipulativeOrderPatterns = true,
        reason,
    };

    // **否定形（本テストの主眼）**: 口座種別を送らない更新で、既に設定済みの口座種別が消えない。
    [Fact]
    public async Task 口座種別を送らないガード更新は現行の口座種別を保つ()
    {
        using var factory = new RiskWorkerWebApplicationFactory();
        var client = OwnerClient(factory);

        // まず現金口座を設定する（口座種別を明示した更新）。
        var setup = await client.PutAsJsonAsync("/risk-controls/settings/guard", new
        {
            enabledProductTypes = new[] { (int)ProductType.Cash },
            enabledMarkets = new[] { (int)Market.UnitedStates },
            bannedSymbols = Array.Empty<object>(),
            preventSameDayReentry = true,
            prohibitManipulativeOrderPatterns = true,
            configuredAccountType = (int)AccountType.Cash,
            reason = "米国口座を現金口座へ切り替えた",
        });
        setup.StatusCode.Should().Be(HttpStatusCode.OK);

        // 続いて画面と同じ形（口座種別を含まない本文）で別の項目だけを変える。
        var update = await client.PutAsJsonAsync(
            "/risk-controls/settings/guard", GuardBody("市場設定の見直し", ProductType.Cash));
        update.StatusCode.Should().Be(HttpStatusCode.OK);

        var guard = await GuardOfAsync(client);
        guard.GetProperty("configuredAccountType").GetInt32().Should().Be(
            (int)AccountType.Cash,
            "送っていない値へ黙って切り替わる経路を作らない（ADR-0021 決定3）");
    }

    [Fact]
    public async Task 明示された口座種別はガード更新で反映される()
    {
        using var factory = new RiskWorkerWebApplicationFactory();
        var client = OwnerClient(factory);

        var res = await client.PutAsJsonAsync("/risk-controls/settings/guard", new
        {
            enabledProductTypes = new[] { (int)ProductType.Cash },
            enabledMarkets = new[] { (int)Market.UnitedStates },
            bannedSymbols = Array.Empty<object>(),
            preventSameDayReentry = true,
            prohibitManipulativeOrderPatterns = true,
            configuredAccountType = (int)AccountType.Cash,
            reason = "米国口座を現金口座へ切り替えた",
        });

        res.StatusCode.Should().Be(HttpStatusCode.OK);
        var guard = await GuardOfAsync(client);
        guard.GetProperty("configuredAccountType").GetInt32().Should().Be((int)AccountType.Cash);
    }

    // FR-19, ADR-0021 決定4-4: **API を直に叩く経路でも**現金口座で信用系を有効化できない（多層防御）。
    // 画面だけの統制は API 直叩きで消えるため、サーバ側にも同じ関門を置く（IADR-0141 決定1 と同じ理由）。
    [Theory]
    [InlineData(ProductType.MarginLong)]
    [InlineData(ProductType.ShortSell)]
    public async Task 現金口座を観測している状態で信用系を有効化する要求は400で拒否される(ProductType unsupported)
    {
        using var factory = new RiskWorkerWebApplicationFactory();
        // 現金口座を照会できている状態を作る（本来は定期 probe が供給する）。
        using (var scope = factory.Services.CreateScope())
        {
            scope.ServiceProvider.GetRequiredService<IBrokerAccountObservationStore>()
                .Record(new BrokerAccountState(AccountType.Cash), DateTimeOffset.UtcNow);
        }

        var client = OwnerClient(factory);

        var res = await client.PutAsJsonAsync(
            "/risk-controls/settings/guard", GuardBody("信用系を有効化する", ProductType.Cash, unsupported));

        res.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        // 拒否された要求は設定を変えない（履歴にも残らない・IADR-0151 決定2 と同じ規律）。
        var guard = await GuardOfAsync(client);
        guard.GetProperty("enabledProductTypes").EnumerateArray().Select(e => e.GetInt32())
            .Should().NotContain((int)unsupported);
    }
}

using System.Net;
using System.Net.Http.Json;
using AiStockTrading.Shared.Kernel.Trading;
using AiStockTrading.TestSupport.ContractFixtures;
using AwesomeAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;

namespace ConfigurationService.Tests.Contracts;

// 🔴 SC-01, FR-17, FR-13, #1470, IADR-0585, IADR-0146: **全体前提条件の更新の 400 の本文も契約である。**
//
// 画面（SC-01）はサーバの理由（`details`・無ければ `error`）を出す（`frontend/src/lib/validationReasons.ts`）。本文の形が変わると
// 理由が黙って消え、一般文言だけが残る（#1465 の事故）。RiskManagementService・MarketMonitorService の契約テストと同じ作法で、
// 実端点（実 `Program.cs` の配線・群のフィルタの `ArgumentException` → 400 の写し）から本文を採り、frontend 側のフィクスチャと突き合わせる。
//
//   - 本文の形を変える → **本テストが赤**
//   - フィクスチャを再生成して緑に戻す → **フロントの `npm run typecheck` が赤**
//     （`frontend/src/testing/assumptionsContractFixtures.ts` が型 `BadRequestBody` へ代入している）
public class AssumptionsContractFixtureTests
{
    private static readonly ContractFixtureStore Fixtures = new("frontend/src/testing/contract-fixtures");

    private static HttpClient Owner(WebApplicationFactory<Program> factory)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add(TestAuthHandler.RolesHeader, "trading-owner");
        return client;
    }

    // 理由の空欄（利用者へ見せる印つきの入力検証。画面は先に止めるが、サーバが実効を持つ）。
    [Fact]
    public async Task 前提条件の更新の400応答がフロントの契約フィクスチャと一致する()
    {
        await using var factory = new ConfigurationWorkerWebApplicationFactory();

        var res = await Owner(factory).PutAsJsonAsync(
            "/assumptions",
            new { Assumptions = TradingAssumptionsDefaults.Create(), ExpectedVersion = 1, Reason = " " },
            TestContext.Current.CancellationToken);

        res.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var actual = ContractFixtureComparer.Normalize(await res.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        const string fileName = "assumptions.400.json";

        if (ContractFixtureStore.UpdateRequested)
        {
            Fixtures.Write(fileName, actual);
            return;
        }

        var matched = ContractFixtureComparer.Matches(Fixtures.Read(fileName), actual, out var difference);
        matched.Should().BeTrue(
            $"前提条件の 400 の応答がフロントの契約フィクスチャ `{fileName}` と一致しなければならない（#1470・IADR-0585）。"
            + $" {difference}"
            + " 応答を意図的に変えたのであれば `UPDATE_CONTRACT_FIXTURES=1 dotnet test` でフィクスチャを再生成し、"
            + "**フロント（validationReasons.ts・assumptionsContractFixtures.ts・画面）も追随させてから**コミットすること。");
    }
}

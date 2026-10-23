using System.Net;
using AiStockTrading.TestSupport.ContractFixtures;
using AwesomeAssertions;
using Xunit;

namespace AiStockTrading.Bff.Endpoints.Tests;

// SC-04, FR-09, FR-11, UC-06, #1413, IADR-0146, IADR-0321: **BFF ↔ フロントの契約テスト（OpenD 認証の状態）。**
//
// SC-04 の E2E（`frontend/e2e/sc04-opend-auth.spec.ts`）と単体テストのモックは、ここで採った**BFF の実応答**を土台にする。
// 手書きの literal で組むと、フロントが「こう返ってくるはずだ」と思っている形をフロント自身が検証する構造に戻る（#389 の根因）。
//
//   - BFF の `OpendAuthStateView` のプロパティ名・型を変える → **本テストが赤**
//   - フィクスチャを再生成して本テストを緑に戻す → **フロントの `npm run typecheck` が赤**
//     （`frontend/src/features/sc04-opend-auth/types/contract.test.ts` が型 `OpendAuthStateView` へ代入している）
//
// 採るのは「SMS 検証コード待ち」で、配備構成の 2 指標（デバイス信頼・egress の安定）も構成した状態である。
// 値が `null` の欄は契約に形が現れない（IADR-0146 残余リスク 2）ため、全欄に値が入る状態を選んだ。
// 「待機していない」「供給が無い」の形は、E2E が本フィクスチャを spread で上書きして描き分ける。
public class OpendAuthContractFixtureTests
{
    private static readonly ContractFixtureStore Fixtures = new("frontend/src/testing/contract-fixtures");

    private const string UpstreamWaitingForSms =
        """{"status":"waiting","prompt":"phone","captchaAvailable":false,"lastLoginAt":"2026-09-09T21:02:00Z","detail":null}""";

    [Fact]
    public async Task 状態応答がフロントの契約フィクスチャと一致する()
    {
        await using var host = await OpendAuthTestHost.StartAsync(deviceTrust: "true", egressStable: "true");
        host.Upstream.State = UpstreamWaitingForSms;

        var res = await host.SendAsync("GET", "/bff/opend-auth/state", roles: OpendAuthTestHost.OwnerRole);

        res.StatusCode.Should().Be(HttpStatusCode.OK);
        var actual = ContractFixtureComparer.Normalize(await res.Content.ReadAsStringAsync());

        if (ContractFixtureStore.UpdateRequested)
        {
            Fixtures.Write("opend-auth.state.json", actual);
            return;
        }

        var matched = ContractFixtureComparer.Matches(Fixtures.Read("opend-auth.state.json"), actual, out var difference);
        matched.Should().BeTrue(
            "BFF の応答がフロントの契約フィクスチャ `opend-auth.state.json` と一致しなければならない（IADR-0146）。"
            + $" {difference}"
            + " 応答を意図的に変えたのであれば `UPDATE_CONTRACT_FIXTURES=1 dotnet test` でフィクスチャを再生成し、"
            + "**フロント（sc04-opend-auth/types と同 contract.test.ts・画面）も追随させてから**コミットすること。");
    }
}

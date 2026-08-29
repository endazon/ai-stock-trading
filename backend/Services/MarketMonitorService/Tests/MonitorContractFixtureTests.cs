using System.Net;
using System.Net.Http.Json;
using AiStockTrading.Shared.Contracts.Trading;
using AiStockTrading.TestSupport.ContractFixtures;
using AwesomeAssertions;
using Xunit;

namespace MarketMonitorService.Tests;

// FR-03, FR-13, SC-01 §2, SC-02, #340, IADR-0146 / IADR-0155: **バックエンド↔フロントの契約テスト（監視設定）。**
//
// #389 は「フロントのテストがフロント自身の書いたモックを検証していたため、両側とも緑のまま本番だけが
// 壊れた」事故だった。SC-01 §2 は本 issue で **`/monitor/settings` を新たに読む**ため、同じ関門を張る。
// IADR-0146 は当時 RiskManagementService の 4 エンドポイントに限っており、他サービスへの横展開を
// フォローアップとしていた。ここがその横展開である。
//
//   - バックエンドのプロパティ名を変える → **本テストが落ちる**
//   - フィクスチャを再生成して本テストを緑に戻す → **フロントの `npm run typecheck` が落ちる**
//
// **ファクトリはテストごとに新設する**（`IClassFixture` で共有しない）。共有すると、閾値を変更する
// 履歴テストと変更前の値を採る設定テストの**実行順でフィクスチャの中身が変わる**（xUnit はクラス内の
// 実行順を保証しない）。フィクスチャが実行順に依存すると、無関係な PR で赤くなり再生成が常用される
// ＝機構が無効化される（IADR-0146 決定5 が避けたい状態そのもの）。
public class MonitorContractFixtureTests
{
    // IADR-0146: フィクスチャは frontend 側（`src/features/monitor/contract-fixtures/`）に置く。
    private static readonly ContractFixtureStore Fixtures = new("frontend/src/features/monitor/contract-fixtures");

    private static HttpClient OwnerClient(MonitorWorkerWebApplicationFactory factory)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add(TestAuthHandler.RolesHeader, "trading-owner");
        return client;
    }

    // FR-03, FR-13, SC-01 §2: GET /monitor/settings（変動閾値・クールダウン・監視銘柄）。
    // **`cooldown` は `TimeSpan` であり JSON では `"00:15:00"` の文字列で往来する**（数値ではない）。
    // フロントの型がこれを number と宣言していれば `tsc` が落ちる。
    [Fact]
    public async Task 監視設定応答がフロントの契約フィクスチャと一致する()
    {
        using var factory = new MonitorWorkerWebApplicationFactory();
        var client = OwnerClient(factory);

        // 監視銘柄を 1 件積んでから採る——空のままだと `MonitoredSymbol` の形が契約に現れない
        // （フィクスチャが表現しない形は守れない・IADR-0146 残余リスク2）。
        (await client.PostAsJsonAsync("/monitor/watchlist",
            new { Symbol = "CONTRACT", Market = Market.UnitedStates, Reason = "契約フィクスチャ生成" }))
            .IsSuccessStatusCode.Should().BeTrue();

        var res = await client.GetAsync("/monitor/settings");

        res.StatusCode.Should().Be(HttpStatusCode.OK);
        await AssertMatchesFixtureAsync("monitor.settings.json", res);
    }

    // FR-11, FR-13, SC-01 §2: GET /monitor/settings/history（監視設定の変更履歴）。
    // 変動閾値の変更（changeType=2・before/after つき）を実際に起こしてから採る。
    // SC-01 §2 は before/after を描画するため、null のままでは契約が現れない。
    [Fact]
    public async Task 監視設定変更履歴応答がフロントの契約フィクスチャと一致する()
    {
        using var factory = new MonitorWorkerWebApplicationFactory();
        var client = OwnerClient(factory);

        var put = await client.PutAsJsonAsync("/monitor/settings/movement-threshold",
            new { movementThresholdRatio = 0.04m, reason = "契約フィクスチャ生成のための変更" });
        put.StatusCode.Should().Be(HttpStatusCode.OK);

        var res = await client.GetAsync("/monitor/settings/history");

        res.StatusCode.Should().Be(HttpStatusCode.OK);
        await AssertMatchesFixtureAsync("monitor.settings-history.json", res);
    }

    private static async Task AssertMatchesFixtureAsync(string fileName, HttpResponseMessage response)
    {
        var actual = ContractFixtureComparer.Normalize(await response.Content.ReadAsStringAsync());

        if (ContractFixtureStore.UpdateRequested)
        {
            Fixtures.Write(fileName, actual);
            return;
        }

        var expected = Fixtures.Read(fileName);
        var matched = ContractFixtureComparer.Matches(expected, actual, out var difference);

        matched.Should().BeTrue(
            $"バックエンドの応答がフロントの契約フィクスチャ `{fileName}` と一致しなければならない（#340・IADR-0146）。"
            + $" {difference}"
            + " 応答を意図的に変えたのであれば `UPDATE_CONTRACT_FIXTURES=1 dotnet test` でフィクスチャを再生成し、"
            + "**フロント（monitor/contracts.ts / contractFixtures.ts / 画面）も追随させてから**コミットすること"
            + "（再生成だけでは `npm run typecheck` が赤いままになる）。");
    }
}

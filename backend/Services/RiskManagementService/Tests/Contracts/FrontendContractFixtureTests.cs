using System.Net;
using System.Net.Http.Json;
using RiskManagementService.Features.RiskManagement;
using RiskManagementService.Domain;
using AiStockTrading.Shared.Contracts.Trading;
using AiStockTrading.Shared.Kernel.Trading;
using AiStockTrading.TestSupport.ContractFixtures;
using AwesomeAssertions;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace RiskManagementService.Tests.Contracts;

// FR-10, FR-20, SC-02, SC-03, #389, IADR-0146: **バックエンド↔フロントの契約テスト。**
//
// 誰も見ていない境界は、いずれ必ず壊れる。#329（equity 比化）・#333（総資金比化）でバックエンドの
// プロパティ名が変わったとき、フロントの契約型は追随せず、**CI は両側とも緑のまま本番だけが壊れた**
// （フロントのテストがフロント自身の書いたモックを検証していたため）。
//
// 本テストは「**バックエンドが実際に返す JSON**」をフィクスチャとして固定する。フロント側は同じ
// フィクスチャを `import` して契約型へ代入するため（`frontend/src/features/risk/contractFixtures.ts`）、
//   - バックエンドのプロパティ名を変える → **本テストが落ちる**
//   - フィクスチャを再生成して本テストを緑に戻す → **フロントの `npm run typecheck` が落ちる**
// という二重の関門になる。片側だけ直しても緑にならないことが要点である（IADR-0146 決定2）。
//
// 応答は**実エンドポイント**（実 `Program.cs` の配線・実 `JsonSerializerOptions`）から採る。型を手で
// シリアライズし直すと「テストの中だけの JSON」を検証することになり、フロントが実際に読む形と乖離する。
public class FrontendContractFixtureTests
{
    private const string Owner = "trading-owner";

    // IADR-0146: フィクスチャは frontend 側（`src/features/risk/contract-fixtures/`）に置く。
    private static readonly ContractFixtureStore Fixtures = new("frontend/src/features/risk/contract-fixtures");

    private static HttpClient OwnerClient(RiskWorkerWebApplicationFactory factory)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add(TestAuthHandler.RolesHeader, Owner);
        return client;
    }

    // FR-10, FR-19, FR-20, SC-02: GET /risk-controls/settings（リスク上限・取引ガード・段階・発注先）。
    // #389 の 3 キー（maxOrderAmountRatio / maxDailyOrderAmountRatio / capitalCapRatio）はすべてこの応答に現れる。
    [Fact]
    public async Task 設定応答がフロントの契約フィクスチャと一致する()
    {
        using var factory = new RiskWorkerWebApplicationFactory();
        var client = OwnerClient(factory);

        var res = await client.GetAsync("/risk-controls/settings");

        res.StatusCode.Should().Be(HttpStatusCode.OK);
        await AssertMatchesFixtureAsync("risk-controls.settings.json", res);
    }

    // FR-10, SC-03: GET /risk-controls/status（統制状態）。
    // ここの maxOrderAmount / maxDailyOrderAmount は **equity から解決済みの実額**であり、
    // 設定側（比率）と同名だが別物である。フロントの型もこの区別のまま保つ（#389 で改名しない）。
    [Fact]
    public async Task 統制状態応答がフロントの契約フィクスチャと一致する()
    {
        using var factory = new RiskWorkerWebApplicationFactory();
        var client = OwnerClient(factory);

        var res = await client.GetAsync("/risk-controls/status");

        res.StatusCode.Should().Be(HttpStatusCode.OK);
        await AssertMatchesFixtureAsync("risk-controls.status.json", res);
    }

    // FR-20, SC-03: GET /risk-controls/stage-gate（段階ゲート現況）。
    // 遷移を 1 件実際に起こしてから採る——`history` が空のままだと StageTransition の形が契約に現れず、
    // その型のずれを検出できない（フィクスチャが表現しない形は守れない・IADR-0146 残余リスク2）。
    [Fact]
    public async Task 段階ゲート応答がフロントの契約フィクスチャと一致する()
    {
        using var factory = new RiskWorkerWebApplicationFactory();
        using (var scope = factory.Services.CreateScope())
        {
            scope.ServiceProvider.GetRequiredService<IStagePerformanceStore>()
                .Save(new StagePerformance { BacktestPassed = true });
        }

        var client = OwnerClient(factory);
        var transition = await client.PostAsJsonAsync("/risk-controls/stage-gate/transition",
            new { targetStage = (int)TradingStage.Stage1Simulate });
        transition.StatusCode.Should().Be(HttpStatusCode.OK);

        var res = await client.GetAsync("/risk-controls/stage-gate");

        res.StatusCode.Should().Be(HttpStatusCode.OK);
        await AssertMatchesFixtureAsync("risk-controls.stage-gate.json", res);
    }

    // FR-10, SC-03, ADR-0016 決定3/7/9/15, #340, IADR-0154: GET /risk-controls/short-selling（空売りの現況）。
    //
    // **本フィクスチャが固定するのは値ではなく「供給が無いという宣言」である。**
    // 既定構成（UnavailableMaintenanceMarginSnapshotSource）では維持率・借株料の累計・自動縮小の発動履歴・
    // **強制買戻しの発生回数**（#424・IADR-0162）のいずれも供給されず、応答は
    // `MetricAvailability.NotSupplied`（＝1）を返す。ここが `0`（Available）や `2`（NotApplicable）へ
    // 黙って変われば本テストが落ちる。
    //
    // **「供給可否はサーバ側が宣言する」ことそのものを本フィクスチャが担保する**（05_screens 2026-08-07）。
    // 応答から `*Availability` が消える・フロントが値の中身（0 かどうか）から推測する形へ戻る、のいずれも
    // 本テストとフロントの `npm run typecheck` の**両方**を通らないと成立しない。
    //
    // 供給元が入って `Available` を返すようになった場合も本テストは落ちる。**それが正しい**——
    // 供給の有無が変わったなら、画面（SC-03）と blocked-tasks の記述を同じ PR で追随させる必要がある。
    [Fact]
    public async Task 空売り現況応答がフロントの契約フィクスチャと一致する()
    {
        using var factory = new RiskWorkerWebApplicationFactory();

        // 空売り建玉を 1 件積んでから採る——`positions` が空のままだと `ShortSellingPositionView` の形が
        // 契約に現れず、その型のずれを検出できない（フィクスチャが表現しない形は守れない・IADR-0146 残余リスク2）。
        // 現在値の供給は既定で無いため、この建玉の評価額は `NotSupplied`（＝1）で現れる。
        using (var scope = factory.Services.CreateScope())
        {
            var ledger = scope.ServiceProvider.GetRequiredService<IPortfolioLedgerStore>();
            var decisionId = Guid.NewGuid();
            var at = new DateTimeOffset(2026, 8, 5, 0, 0, 0, TimeSpan.Zero);
            ledger.AppendApproval(
                decisionId,
                new OrderIntent("SHRT", Market.UnitedStates, TradeSide.Sell, ProductType.ShortSell,
                    BrokerProvider.MoomooSimulate, 10, 90m, PositionEffect.Open),
                at);
            ledger.AppendFill(decisionId, $"short-{decisionId:N}", 10, 90m, at);
        }

        var client = OwnerClient(factory);

        var res = await client.GetAsync("/risk-controls/short-selling");

        res.StatusCode.Should().Be(HttpStatusCode.OK);
        await AssertMatchesFixtureAsync("risk-controls.short-selling.json", res);
    }

    // FR-11, FR-20, SC-02, SC-03: GET /risk-controls/settings/history（設定変更履歴）。
    // 上限変更（changeType=1）と発注先変更（changeType=7・before/after つき）の 2 件を実際に起こしてから採る。
    // SC-03 の「発注先の変更履歴」は before/after を描画するため、null のままでは契約が現れない。
    [Fact]
    public async Task 設定変更履歴応答がフロントの契約フィクスチャと一致する()
    {
        using var factory = new RiskWorkerWebApplicationFactory();
        var client = OwnerClient(factory);

        var limits = await client.PutAsJsonAsync("/risk-controls/settings/limits", new
        {
            limits = TradingDefaults.CreateRiskLimits(),
            reason = "契約フィクスチャ生成のための既定値の再保存",
        });
        limits.StatusCode.Should().Be(HttpStatusCode.OK);

        var provider = await client.PutAsJsonAsync("/risk-controls/settings/broker-provider", new
        {
            provider = (int)BrokerProvider.MoomooSimulate,
            reason = "Stage 1 の検証のため moomoo SIMULATE へ切り替える",
        });
        provider.StatusCode.Should().Be(HttpStatusCode.OK);

        var res = await client.GetAsync("/risk-controls/settings/history");

        res.StatusCode.Should().Be(HttpStatusCode.OK);
        await AssertMatchesFixtureAsync("risk-controls.settings-history.json", res);
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
            $"バックエンドの応答がフロントの契約フィクスチャ `{fileName}` と一致しなければならない（#389・IADR-0146）。"
            + $" {difference}"
            + " 応答を意図的に変えたのであれば `UPDATE_CONTRACT_FIXTURES=1 dotnet test` でフィクスチャを再生成し、"
            + "**フロント（contracts.ts / contractFixtures.ts / 画面）も追随させてから**コミットすること"
            + "（再生成だけでは `npm run typecheck` が赤いままになる）。");
    }
}

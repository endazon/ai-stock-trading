using System.Net;
using System.Net.Http.Json;
using RiskManagementService.Application.State;
using RiskManagementService.Domain;
using AwesomeAssertions;
using Xunit;

namespace RiskManagementService.Api.Tests;

// FR-20, FR-13, UC-06, SC-02, #423, 06_daytrading-review §4.1 条件 3 / §4.3, IADR-0164 決定4〜決定6:
// `PUT /risk-controls/settings/stage1-minimum-trade-count` の検証。
//
// **本テスト群が守るのは 2 つの相反する要求である。**
//   1. 値域（1〜1000）は**サーバが実効させる**——画面だけの関門は API 直叩きで消える。
//   2. **100 件未満は拒否してはならない**——裁定は「警告は設定を妨げない。下げた事実が記録に残ることを
//      担保する」と定めた。1 を守るあまり 2 を破ると、利用者が下げられなくなり裁定に反する。
//
// 注意: 本 fixture は 1 つの DB を共有するため、件数を変えるテストは**最後に既定 100 へ戻す**
// （後続テストへ持ち越さない。BrokerProviderEndpointTests と同じ片付けの流儀）。
public class Stage1TradeCountSettingEndpointTests(RiskWorkerWebApplicationFactory factory)
    : IClassFixture<RiskWorkerWebApplicationFactory>
{
    private const string Path = "/risk-controls/settings/stage1-minimum-trade-count";

    private HttpClient OwnerClient()
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add(TestAuthHandler.RolesHeader, "trading-owner");
        return client;
    }

    private static Task<HttpResponseMessage> PutAsync(HttpClient client, object body) =>
        client.PutAsJsonAsync(Path, body);

    private async Task RestoreDefaultAsync(HttpClient client) =>
        (await PutAsync(client, new { minimumTradeCount = Stage1TradeCountBounds.Default, reason = "既定へ戻す" }))
            .StatusCode.Should().Be(HttpStatusCode.OK);

    // ------------------------------------------------------------------
    // 認可（FR-13: 変更は利用者のみ。生成AI・自動処理は変更できない）
    // ------------------------------------------------------------------

    [Fact]
    public async Task 未認証の変更は401()
    {
        var res = await PutAsync(factory.CreateClient(), new { minimumTradeCount = 50, reason = "検証" });

        res.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task サービスロールの変更は403()
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add(TestAuthHandler.RolesHeader, "trading-service");

        var res = await PutAsync(client, new { minimumTradeCount = 50, reason = "検証" });

        res.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    // ------------------------------------------------------------------
    // 値域の境界（0 不可 / 1 可 / 1000 可 / 1001 不可）
    // ------------------------------------------------------------------

    [Theory]
    [InlineData(1)]
    [InlineData(1000)]
    public async Task 値域の境界は受理され設定へ反映される(int value)
    {
        var client = OwnerClient();

        var res = await PutAsync(client, new { minimumTradeCount = value, reason = $"境界 {value} の検証" });

        res.StatusCode.Should().Be(HttpStatusCode.OK);
        (await CurrentCountAsync(client)).Should().Be(value);

        await RestoreDefaultAsync(client);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(1001)]
    [InlineData(10_000)]
    public async Task 値域外は400であり設定も履歴も変わらない(int value)
    {
        var client = OwnerClient();
        var before = await CurrentCountAsync(client);
        var historyBefore = await HistoryCountAsync(client);

        var res = await PutAsync(client, new { minimumTradeCount = value, reason = "値域外" });

        res.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await CurrentCountAsync(client)).Should().Be(before, "拒否された要求は設定を変えない");
        (await HistoryCountAsync(client)).Should().Be(
            historyBefore, "拒否された要求を履歴に積むと、起きていない変更が監査上の事実になる");
    }

    // 省略（null）は 400。非 nullable int で受けると本文省略時に既定値 0 へ暗黙束縛され、
    // 「送っていない値へ黙って切り替わる」経路になる。
    [Fact]
    public async Task 件数を省略すると400()
    {
        var res = await PutAsync(OwnerClient(), new { reason = "省略" });

        res.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    // ------------------------------------------------------------------
    // 理由必須と監査ログ（FR-11・#423 の退行防止項目）
    // ------------------------------------------------------------------

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task 理由が空の変更は400(string reason)
    {
        var client = OwnerClient();
        var before = await CurrentCountAsync(client);

        var res = await PutAsync(client, new { minimumTradeCount = 50, reason });

        res.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await CurrentCountAsync(client)).Should().Be(before);
    }

    [Fact]
    public async Task 理由を省略した変更は400()
    {
        var res = await PutAsync(OwnerClient(), new { minimumTradeCount = 50 });

        res.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    // 裁定「**下げた事実が記録に残ることを担保する**」。前後値つきで履歴に残ることを固定する。
    [Fact]
    public async Task 変更は理由と前後値つきで監査ログに残る()
    {
        var client = OwnerClient();
        await RestoreDefaultAsync(client);

        var res = await PutAsync(client, new { minimumTradeCount = 40, reason = "検証期間を短縮したい" });
        res.StatusCode.Should().Be(HttpStatusCode.OK);

        var history = await client.GetFromJsonAsync<List<HistoryDto>>("/risk-controls/settings/history");
        history.Should().Contain(e =>
            e.ChangeType == SettingsChangeType.Stage1MinimumTradeCountChanged
            && e.Reason == "検証期間を短縮したい"
            && e.Before == "100"
            && e.After == "40");

        await RestoreDefaultAsync(client);
    }

    // ------------------------------------------------------------------
    // 100 件未満は「警告するが妨げない」（最重要）
    // ------------------------------------------------------------------

    // **裁定の核心。** 統計的根拠を満たさない設定であっても保存できなければならない。
    // ここが 400 になる実装は「警告」を「拒否」へすり替えたことを意味する。
    [Theory]
    [InlineData(1)]
    [InlineData(30)]
    [InlineData(99)]
    public async Task 統計的根拠を下回る件数でも保存できる_警告は設定を妨げない(int value)
    {
        var client = OwnerClient();

        var res = await PutAsync(client, new { minimumTradeCount = value, reason = "統計的根拠より下げる" });

        res.StatusCode.Should().Be(HttpStatusCode.OK, "警告は設定を妨げない（質問票 第 13 回 Q6）");
        (await CurrentCountAsync(client)).Should().Be(value);

        await RestoreDefaultAsync(client);
    }

    // 保存された設定は**段階ゲートの応答へ実効値として現れ、警告の宣言を伴う**。
    // 「保存はできたが判定には効かない」状態を作らない。
    [Fact]
    public async Task 統計的根拠を下回る設定は段階ゲートの応答に警告つきで現れる()
    {
        var client = OwnerClient();

        (await PutAsync(client, new { minimumTradeCount = 10, reason = "警告の宣言を検証する" }))
            .StatusCode.Should().Be(HttpStatusCode.OK);

        var gate = await client.GetFromJsonAsync<StageGateDto>("/risk-controls/stage-gate");
        gate!.Stage1Criteria.MinimumTradeCount.Should().Be(10, "設定した件数が合格条件として効く");
        gate.Stage1Criteria.BelowStatisticalBasis.Should().BeTrue("100 件未満はサーバが警告を宣言する");

        // **条件 1・条件 2・打ち切りには及ばない**（裁定が明示）。設定で動くのは件数だけである。
        gate.Stage1Criteria.TargetTradingDays.Should().Be(60);
        gate.Stage1Criteria.MaximumTradingDays.Should().Be(120);

        await RestoreDefaultAsync(client);
    }

    // 逆方向の否定形。100 件以上では警告を宣言しない（常時警告は誰にも読まれなくなる）。
    [Fact]
    public async Task 既定の件数では警告を宣言しない()
    {
        var client = OwnerClient();
        await RestoreDefaultAsync(client);

        var gate = await client.GetFromJsonAsync<StageGateDto>("/risk-controls/stage-gate");

        gate!.Stage1Criteria.MinimumTradeCount.Should().Be(100);
        gate.Stage1Criteria.BelowStatisticalBasis.Should().BeFalse();
    }

    private static async Task<int> CurrentCountAsync(HttpClient client) =>
        (await client.GetFromJsonAsync<SettingsDto>("/risk-controls/settings"))!.Stage1MinimumTradeCount;

    private static async Task<int> HistoryCountAsync(HttpClient client) =>
        (await client.GetFromJsonAsync<List<HistoryDto>>("/risk-controls/settings/history"))!.Count;

    private sealed record SettingsDto(int Stage1MinimumTradeCount);

    private sealed record HistoryDto(
        string Actor, SettingsChangeType ChangeType, string Reason, string? Before, string? After);

    private sealed record StageGateDto(Stage1CriteriaDto Stage1Criteria);

    private sealed record Stage1CriteriaDto(
        int TargetTradingDays, int MinimumTradeCount, int MaximumTradingDays, bool BelowStatisticalBasis);
}

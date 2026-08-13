using System.Net;
using System.Net.Http.Json;
using AiStockTrading.RiskManagement.Application.State;
using AiStockTrading.RiskManagement.Domain;
using AiStockTrading.RiskManagement.Api.Foundation.Endpoints;
using AwesomeAssertions;
using Xunit;

namespace AiStockTrading.RiskManagement.Api.Tests;

// FR-10, UC-06, ADR-0003, ADR-0007: kill switch/設定エンドポイントの認可（OwnerOnly）と永続化・履歴を検証する。
public class RiskControlEndpointsTests(RiskWorkerWebApplicationFactory factory)
    : IClassFixture<RiskWorkerWebApplicationFactory>
{
    private const string Owner = "trading-owner";
    private const string Service = "trading-service";

    private HttpClient OwnerClient() => ClientWithRoles(Owner);

    private HttpClient ClientWithRoles(string roles)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add(TestAuthHandler.RolesHeader, roles);
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
        // FR-11, ADR-0003: 上限変更が変更履歴に残る。
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
        // 検証失敗（FR-11: 理由必須）は既定の 500 ではなく 400 に写像する。
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
    public async Task 未認証の_open_positions_取得は401()
    {
        // FR-03/10, IADR-0030: 保有ポジションも OwnerOnly。未認証は 401。
        var client = factory.CreateClient();

        var res = await client.GetAsync("/risk-controls/open-positions");

        res.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task 利用者は_open_positions_を取得できる()
    {
        // FR-03/10, IADR-0030: #63 台帳の射影から保有ポジション列を返す（空でも 200）。
        var client = OwnerClient();

        var res = await client.GetAsync("/risk-controls/open-positions");
        res.StatusCode.Should().Be(HttpStatusCode.OK);

        var positions = await res.Content.ReadFromJsonAsync<List<OpenPositionDto>>();
        positions.Should().NotBeNull(); // 台帳が空なら空列
    }

    [Fact]
    public async Task サービスロールは_sizing_context_を取得できる()
    {
        // IADR-0051: OwnerOrService。s2s の trading-service で読み取り系の同期照会が 200。
        var client = ClientWithRoles(Service);

        var res = await client.GetAsync("/risk-controls/sizing-context");

        res.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task サービスロールは_open_positions_を取得できる()
    {
        // IADR-0051: OwnerOrService。open-positions を最優先に s2s で 200（サイレント縮退の解消）。
        var client = ClientWithRoles(Service);

        var res = await client.GetAsync("/risk-controls/open-positions");

        res.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task 未認証の_fills_取得は401()
    {
        // FR-06/16, IADR-0115 決定5, #280: 期間約定も読み取り系（OwnerOrService）。未認証は 401。
        var client = factory.CreateClient();

        var res = await client.GetAsync("/risk-controls/fills?from=2026-07-06&to=2026-07-10");

        res.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task 読み取りロールを持たない_fills_取得は403()
    {
        var client = ClientWithRoles("viewer");

        var res = await client.GetAsync("/risk-controls/fills?from=2026-07-06&to=2026-07-10");

        res.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task サービスロールは_fills_を取得できる()
    {
        // IADR-0051/0115: 報告書サービスが s2s（trading-service）で期間約定を照会する。台帳が空なら空列で 200。
        var client = ClientWithRoles(Service);

        var res = await client.GetAsync("/risk-controls/fills?from=2026-07-06&to=2026-07-10");
        res.StatusCode.Should().Be(HttpStatusCode.OK);

        var fills = await res.Content.ReadFromJsonAsync<List<LedgerFill>>();
        fills.Should().NotBeNull();
    }

    [Fact]
    public async Task 期間未指定の_fills_取得は400()
    {
        // 期間は必須。黙って全期間を返すと、報告書の集計範囲が意図せず広がる。
        var client = ClientWithRoles(Service);

        var res = await client.GetAsync("/risk-controls/fills");

        res.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    // ---- FR-10, FR-06, FR-21, ADR-0016 決定15, #463, IADR-0181: 強制買戻しの推定の照会 ----
    //
    // **観測の到達（FR-21）と推定行を同じ応答で返す。** 2 つに分けると、呼び出し側が推定行だけを見て
    // 0 件と判断する経路が作れてしまう。

    [Fact]
    public async Task 未認証の_buy_in_inferences_取得は401()
    {
        // 🔴 受け入れ基準: **無認証の内部エンドポイントを増やさない**（IADR-0176 と同型）。
        var client = factory.CreateClient();

        var res = await client.GetAsync("/risk-controls/buy-in-inferences?from=2026-08-01&to=2026-08-08");

        res.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task 読み取りロールを持たない_buy_in_inferences_取得は403()
    {
        var client = ClientWithRoles("viewer");

        var res = await client.GetAsync("/risk-controls/buy-in-inferences?from=2026-08-01&to=2026-08-08");

        res.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task サービスロールは_buy_in_inferences_を取得できる()
    {
        // 報告書サービスが s2s（trading-service）で照会する。
        // **期間が観測に覆われていなければ periodCovered は false**——推定台帳が空でも
        // 呼び出し側は 0 件と読んではならない（FR-21・2026-08-08 改定）。
        var client = ClientWithRoles(Service);

        var res = await client.GetAsync("/risk-controls/buy-in-inferences?from=2026-08-01&to=2026-08-08");
        res.StatusCode.Should().Be(HttpStatusCode.OK);

        var body = await res.Content.ReadFromJsonAsync<BuyInInferenceQueryDto>();
        body.Should().NotBeNull();
        // **[2026-08-08 改定] 期間判定である**（計画 FR-21・裁定 project-planning#292）。
        body!.PeriodCovered.Should().BeFalse("観測はまだ一度も届いていない");
        body.ObservedTradingDays.Should().NotBeNull().And.BeEmpty();
        body.Inferences.Should().NotBeNull().And.BeEmpty();
    }

    // **否定形**: 期間の省略・逆順は 400。`/fills` は 200 空列だが**こちらは向きが違う** ——
    // 黙って空列を返すと、それが「推定 0 件」として報告書に載り得る。
    [Theory]
    [InlineData("/risk-controls/buy-in-inferences")]
    [InlineData("/risk-controls/buy-in-inferences?from=2026-08-01")]
    [InlineData("/risk-controls/buy-in-inferences?to=2026-08-08")]
    [InlineData("/risk-controls/buy-in-inferences?from=2026-08-08&to=2026-08-01")]
    public async Task 期間が不正な_buy_in_inferences_取得は400(string path)
    {
        var client = ClientWithRoles(Service);

        var res = await client.GetAsync(path);

        res.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    private sealed record BuyInInferenceQueryDto(
        bool PeriodCovered,
        List<DateOnly>? ObservedTradingDays,
        List<object>? Inferences);

    // ---- FR-19, FR-11, UC-06, #464, ADR-0028 決定2/決定3, IADR-0182: GFV 違反による停止の解除 ----
    //
    // **OwnerOnly。** ADR-0028 §結果 が「解除操作そのものが新たな攻撃面・誤操作面になる」と明記しており、
    // 既存の破壊的統制操作（kill switch・pause）と同じ権限管理の下に置く。

    [Fact]
    public async Task 未認証の_GFV解除は401()
    {
        var client = factory.CreateClient();

        var res = await client.PostAsJsonAsync("/risk-controls/good-faith-violations/clear",
            new { reason = "原因を是正した" });

        res.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    // 🔴 **否定形**: サービスロール（trading-service）では解除できない。
    // 生成AI・自動処理が統制を解けてはならない（IADR-0051 最小権限）。
    [Fact]
    public async Task サービスロールは_GFV解除できない_403()
    {
        var client = ClientWithRoles(Service);

        var res = await client.PostAsJsonAsync("/risk-controls/good-faith-violations/clear",
            new { reason = "原因を是正した" });

        res.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    // **否定形**: 理由が空の解除は 400（ADR-0028 決定2「原因の是正が済んでいることの確認を伴う」）。
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task 理由が空の_GFV解除は400(string reason)
    {
        var client = ClientWithRoles(Owner);

        var res = await client.PostAsJsonAsync("/risk-controls/good-faith-violations/clear", new { reason });

        res.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    // **否定形**: 解除対象が無い（停止していない）ときは 422 —— **成功に見せない**。
    // 200 を返すと、実際には何も起きていない操作が「解除した」という監査上の事実になる。
    [Fact]
    public async Task 解除対象が無い_GFV解除は422()
    {
        var client = ClientWithRoles(Owner);

        var res = await client.PostAsJsonAsync("/risk-controls/good-faith-violations/clear",
            new { reason = "原因を是正した" });

        res.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
    }

    [Fact]
    public async Task サービスロールは_kill_switch_を操作できない_403()
    {
        // IADR-0051 最小権限: trading-service は書き込み系（OwnerOnly）を持たない。kill switch は 403。
        var client = ClientWithRoles(Service);

        var res = await client.PostAsJsonAsync("/risk-controls/kill-switch/engage",
            new KillSwitchRequest("サービスは操作できない"));

        res.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task サービスロールは_設定変更できない_403()
    {
        // IADR-0051 最小権限: trading-service はリスク設定変更（OwnerOnly）を持たない。403。
        var client = ClientWithRoles(Service);
        var limits = TradingDefaults.CreateRiskLimits() with { MaxOpenPositions = 5 };

        var res = await client.PutAsJsonAsync("/risk-controls/settings/limits",
            new LimitsUpdateRequest(limits, "サービスは変更できない"));

        res.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task ロール無しは読み取り系も_403()
    {
        // OwnerOrService: 認証済みだが owner/service いずれも持たない → 403。
        var client = ClientWithRoles("viewer");

        (await client.GetAsync("/risk-controls/open-positions")).StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task 利用者は一時停止と再開ができ状態が永続化される()
    {
        // FR-10, ADR-0009: pause は OwnerOnly。往復（pause → resume）と別スコープ永続化を確認する。
        var client = OwnerClient();

        var pause = await client.PostAsJsonAsync("/risk-controls/pause", new PauseRequest("様子見のため一時停止"));
        pause.StatusCode.Should().Be(HttpStatusCode.OK);

        var afterPause = await client.GetFromJsonAsync<PauseStateDto>("/risk-controls/pause");
        afterPause!.Paused.Should().BeTrue();
        afterPause.Reason.Should().Be("様子見のため一時停止");

        var resume = await client.PostAsJsonAsync("/risk-controls/resume", new PauseRequest("再開"));
        resume.StatusCode.Should().Be(HttpStatusCode.OK);

        var afterResume = await client.GetFromJsonAsync<PauseStateDto>("/risk-controls/pause");
        afterResume!.Paused.Should().BeFalse();
    }

    [Fact]
    public async Task 一時停止と再開が変更履歴に記録される()
    {
        // FR-11・ADR-0009: pause/resume の監査（アクター・理由・種別）を設定変更履歴へ残す。
        var client = OwnerClient();

        await client.PostAsJsonAsync("/risk-controls/pause", new PauseRequest("様子見"));
        await client.PostAsJsonAsync("/risk-controls/resume", new PauseRequest("再開"));

        var history = await client.GetFromJsonAsync<List<SettingsChangeDto>>("/risk-controls/settings/history");
        history.Should().NotBeNull();
        history!.Should().Contain(e => e.ChangeType == SettingsChangeType.TradingPaused && e.Actor == "test-owner");
        history!.Should().Contain(e => e.ChangeType == SettingsChangeType.TradingResumed && e.Actor == "test-owner");
    }

    [Fact]
    public async Task 理由が空の一時停止操作は400()
    {
        var client = OwnerClient();

        var res = await client.PostAsJsonAsync("/risk-controls/pause", new PauseRequest(""));

        res.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task サービスロールは一時停止を操作できない_403()
    {
        // IADR-0051 最小権限: trading-service は OwnerOnly の pause を持たない。403。
        var client = ClientWithRoles(Service);

        var res = await client.PostAsJsonAsync("/risk-controls/pause", new PauseRequest("サービスは操作できない"));

        res.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task サービスロールは_status_を照会できない_403()
    {
        // ADR-0009: /status は OwnerOnly（当日損益・ポジション等の機微情報を含む表示）。trading-service は 403。
        var client = ClientWithRoles(Service);

        var res = await client.GetAsync("/risk-controls/status");

        res.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task 利用者は_status_を照会でき現在状態が返る()
    {
        // FR-10, UC-07, ADR-0009: /status は 3 統制の状態・優先順位・段階・損益・上限・ポジションを返す（表示専用）。
        var client = OwnerClient();

        // NFR, #352: 本テストは「pause が優先中の統制として示されること」を見るため、kill switch が
        // 起動していないことを前提にする。同クラスの別テスト（kill switch の永続化）は起動したまま
        // 終わるうえ、xUnit v3 は既定のテストケース順序が v2 と異なる（IClassFixture の InMemory DB は
        // クラス内で共有される）。前提を順序に依存させず、ここで自ら成立させる。
        await client.PostAsJsonAsync("/risk-controls/kill-switch/disengage",
            new KillSwitchRequest("前提条件の初期化"));

        // 一時停止を成立させてから照会する（優先中の統制が pause として示されること）。
        await client.PostAsJsonAsync("/risk-controls/pause", new PauseRequest("様子見"));

        var status = await client.GetFromJsonAsync<RiskStatusDto>("/risk-controls/status");

        status.Should().NotBeNull();
        status!.TradingPaused.Should().BeTrue();
        status.NewEntriesBlocked.Should().BeTrue();
        status.ActiveControl.Should().Be(ActiveTradingControl.Pause);

        // 後続テストへ状態を持ち越さないよう再開しておく。
        await client.PostAsJsonAsync("/risk-controls/resume", new PauseRequest("片付け"));
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

    private sealed record PauseStateDto(bool Paused, string? Actor, string? Reason);

    // ActiveControl は enum（SettingsChangeType と同様に enum 型で受ける＝数値/文字列いずれの表現でも往復する）。
    private sealed record RiskStatusDto(
        bool KillSwitchEngaged, bool TradingPaused, bool NewEntriesBlocked, ActiveTradingControl ActiveControl);

    private sealed record SettingsChangeDto(string Actor, SettingsChangeType ChangeType, string Reason);

    private sealed record SizingContextDto(decimal Capital, decimal StageCapitalRemaining, decimal DailyOrderRemaining);

    private sealed record OpenPositionDto(string Symbol, int Quantity, decimal EntryPrice, decimal StopLossPrice);
}

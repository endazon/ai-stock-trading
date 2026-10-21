using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using RiskManagementService.Common.Abstractions;
using RiskManagementService.Domain;
using RiskManagementService.Features.RiskManagement;
using RiskManagementService.Features.RiskManagement.GetSizingContext;
using RiskManagementService.Features.RiskManagement.SessionAndThemeControls;
using RiskManagementService.Infrastructure.ExternalServices;
using RiskManagementService.Infrastructure.Persistence;
using AiStockTrading.Shared.Contracts.Events;
using AiStockTrading.Shared.Contracts.Trading;
using AwesomeAssertions;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace RiskManagementService.Tests;

// T-10-2945〜T-10-2952, FR-10, FR-11, FR-19, SC-02, UC-06, ADR-0070 決定 5, ADR-0071 決定 5, #1407, IADR-0564:
// 閉場前の統制（締め切り N・手仕舞いの有効と M）とテーマの定義を SC-02 から保存する口（利用者のみ・理由必須・値域外は 400・履歴）、
// 効いている値の解決（保存値があれば保存値・無ければ構成の値）、保存値と構成の値の食い違いの検知、審査とサイジング文脈への反映。
public class SessionAndThemeControlsTests(RiskWorkerWebApplicationFactory factory)
    : IClassFixture<RiskWorkerWebApplicationFactory>
{
    private const string PreClosePath = "/risk-controls/settings/pre-close";
    private const string ThemePath = "/risk-controls/settings/theme-concentration";

    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    private static object PreCloseBody(int usCutoff = 60, int jpCutoff = 60, bool enabled = false, int usFlatten = 15, int jpFlatten = 15, string? reason = "画面からの変更") => new
    {
        cutoffMinutes = new[] { new { market = (int)Market.Japan, minutes = jpCutoff }, new { market = (int)Market.UnitedStates, minutes = usCutoff } },
        flattenEnabled = enabled,
        flattenMinutes = new[] { new { market = (int)Market.Japan, minutes = jpFlatten }, new { market = (int)Market.UnitedStates, minutes = usFlatten } },
        reason,
    };

    private static object ThemeBody(object[] themes, string? reason = "画面からの変更") => new { themes, reason };

    private static object Theme(string name, string[] symbols, int max = 1, int market = (int)Market.UnitedStates) =>
        new { name, market, symbols, maxPositions = max };

    private static RiskWorkerWebApplicationFactory Fresh(IDictionary<string, string?>? settings = null) =>
        new() { HostSettings = settings ?? new Dictionary<string, string?>() };

    private static HttpClient Owner(RiskWorkerWebApplicationFactory f)
    {
        var client = f.CreateClient();
        client.DefaultRequestHeaders.Add(TestAuthHandler.RolesHeader, "trading-owner");
        return client;
    }

    private static async Task<PreCloseControlsView> GetPreClose(HttpClient client) =>
        (await client.GetFromJsonAsync<PreCloseControlsView>(PreClosePath, Web, TestContext.Current.CancellationToken))!;

    private static async Task<ThemeConcentrationControlView> GetThemes(HttpClient client) =>
        (await client.GetFromJsonAsync<ThemeConcentrationControlView>(ThemePath, Web, TestContext.Current.CancellationToken))!;

    private static int Minutes(IReadOnlyList<MarketMinutesView> rows, Market market) => rows.Single(r => r.Market == market).Minutes;

    // ---- 効いている値の解決（保存値が無ければ構成の値） ----

    // T-10-2945: 保存していなければ構成の値が効き、食い違いは無い。構成で変えた値（リスク管理の PreCloseFlatten:* を含む）がそのまま効く値になる。
    [Fact]
    public async Task T_10_2945_保存値が無ければ構成の値が効き食い違いは無い()
    {
        using var f = Fresh(new Dictionary<string, string?>
        {
            ["PreCloseEntryCutoff:Minutes:UnitedStates"] = "45",
            ["PreCloseFlatten:Enabled"] = "true",
            ["PreCloseFlatten:Minutes:Japan"] = "10",
        });
        var client = Owner(f);

        var view = await GetPreClose(client);

        view.CutoffSource.Should().Be(ControlValueSource.Configuration);
        view.FlattenSource.Should().Be(ControlValueSource.Configuration);
        view.SavedCutoff.Should().BeNull();
        view.SavedFlatten.Should().BeNull();
        view.CutoffDrift.Should().BeFalse();
        view.FlattenDrift.Should().BeFalse();
        Minutes(view.EffectiveCutoff.Minutes, Market.UnitedStates).Should().Be(45);
        Minutes(view.EffectiveCutoff.Minutes, Market.Japan).Should().Be(60);
        view.EffectiveFlatten.Enabled.Should().BeTrue("手仕舞いの構成はリスク管理が読む（IADR-0564 決定 1）");
        Minutes(view.EffectiveFlatten.Minutes, Market.Japan).Should().Be(10);
        Minutes(view.CutoffMaxMinutes, Market.UnitedStates).Should().Be(390);
        Minutes(view.CutoffMaxMinutes, Market.Japan).Should().Be(330);
        view.FlattenMaxMinutes.Should().Be(60);

        var themes = await GetThemes(client);
        themes.Source.Should().Be(ControlValueSource.Configuration);
        themes.Saved.Should().BeNull();
        themes.Drift.Should().BeFalse();
        themes.Effective.Select(t => t.Name).Should().Equal("暗号資産", "AI・半導体");
    }

    // T-10-2946: 保存値は設定行で往復する。キーの無い旧行は null（構成の値）。値域の外の永続値（手での編集など）も null＝構成の値へ倒す（黙って丸めない）。
    [Fact]
    public void T_10_2946_保存値は往復し旧行と値域外の永続値は構成の値で読む()
    {
        var saved = TradingDefaults.CreateSettings() with
        {
            PreCloseEntryCutoff = new PreCloseEntryCutoffSettings(new Dictionary<Market, int> { [Market.UnitedStates] = 90, [Market.Japan] = 30 }),
            PreCloseFlatten = new PreCloseFlattenSettings(true, new Dictionary<Market, int> { [Market.UnitedStates] = 20, [Market.Japan] = 15 }),
            ThemeConcentration = new ThemeConcentrationSettings([new ThemeLimit("暗号資産", Market.UnitedStates, ["COIN", "MARA"], 2)]),
        };

        var restored = RiskSettingsSerialization.Deserialize(RiskSettingsSerialization.Serialize(saved));

        restored.PreCloseEntryCutoff!.MinutesFor(Market.UnitedStates).Should().Be(90);
        restored.PreCloseEntryCutoff.MinutesFor(Market.Japan).Should().Be(30);
        restored.PreCloseFlatten!.SameAs(saved.PreCloseFlatten).Should().BeTrue();
        SessionAndThemeControlResolution.SameAs(restored.ThemeConcentration!, saved.ThemeConcentration!).Should().BeTrue();

        var legacy = RiskSettingsSerialization.Deserialize(RiskSettingsSerialization.Serialize(TradingDefaults.CreateSettings()));
        legacy.PreCloseEntryCutoff.Should().BeNull();
        legacy.PreCloseFlatten.Should().BeNull();
        legacy.ThemeConcentration.Should().BeNull();

        var json = RiskSettingsSerialization.Serialize(saved)
            .Replace("\"UnitedStates\":90", "\"UnitedStates\":0", StringComparison.Ordinal)
            .Replace("\"UnitedStates\":20", "\"UnitedStates\":61", StringComparison.Ordinal)
            .Replace("\"maxPositions\":2", "\"maxPositions\":0", StringComparison.Ordinal);
        var invalid = RiskSettingsSerialization.Deserialize(json);
        invalid.PreCloseEntryCutoff.Should().BeNull("締め切りを外す値（0 分）を効かせない");
        invalid.PreCloseFlatten.Should().BeNull();
        invalid.ThemeConcentration.Should().BeNull("上限を外す値（0）を効かせない");
    }

    // ---- 認可・理由・値域（拒否の実効はサーバ） ----

    // T-10-2947: 生成 AI・サービス間呼び出し（trading-service）と未認証は変更できない。読み取りも owner だけ（SC-02 の設定画面の口）。
    [Fact]
    public async Task T_10_2947_サービスロールと未認証は変更できない()
    {
        var service = factory.CreateClient();
        service.DefaultRequestHeaders.Add(TestAuthHandler.RolesHeader, "trading-service");
        var anonymous = factory.CreateClient();

        (await anonymous.PutAsJsonAsync(PreClosePath, PreCloseBody(), TestContext.Current.CancellationToken)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await service.PutAsJsonAsync(PreClosePath, PreCloseBody(), TestContext.Current.CancellationToken)).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        var themes = ThemeBody([Theme("暗号資産", ["COIN"])]);
        (await anonymous.PutAsJsonAsync(ThemePath, themes, TestContext.Current.CancellationToken)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await service.PutAsJsonAsync(ThemePath, themes, TestContext.Current.CancellationToken)).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await service.GetAsync(PreClosePath, TestContext.Current.CancellationToken)).StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    // T-10-2948: 値域の外・理由なし・欠けた市場・重複は 400 で、設定も履歴も変えない。
    public static TheoryData<string, object> InvalidPreClose => new()
    {
        { "N 0 分（締め切りを外す値）", PreCloseBody(usCutoff: 0) },
        { "米国 N 391 分（場中の長さ超）", PreCloseBody(usCutoff: 391) },
        { "日本 N 331 分", PreCloseBody(jpCutoff: 331) },
        { "M 0 分", PreCloseBody(usFlatten: 0) },
        { "M 61 分", PreCloseBody(jpFlatten: 61) },
        { "理由なし", PreCloseBody(reason: " ") },
        {
            "市場の欠け",
            new
            {
                cutoffMinutes = new[] { new { market = (int)Market.UnitedStates, minutes = 60 } },
                flattenEnabled = false,
                flattenMinutes = new[] { new { market = (int)Market.Japan, minutes = 15 }, new { market = (int)Market.UnitedStates, minutes = 15 } },
                reason = "x",
            }
        },
        {
            "市場の重複",
            new
            {
                cutoffMinutes = new[] { new { market = (int)Market.UnitedStates, minutes = 60 }, new { market = (int)Market.UnitedStates, minutes = 30 }, new { market = (int)Market.Japan, minutes = 60 } },
                flattenEnabled = false,
                flattenMinutes = new[] { new { market = (int)Market.Japan, minutes = 15 }, new { market = (int)Market.UnitedStates, minutes = 15 } },
                reason = "x",
            }
        },
        { "有効の欠け", new { cutoffMinutes = Array.Empty<object>(), flattenMinutes = Array.Empty<object>(), reason = "x" } },
    };

    [Theory]
    [MemberData(nameof(InvalidPreClose))]
    public async Task T_10_2948_閉場前の統制の値域外と理由なしは400で設定も履歴も変えない(string label, object body)
    {
        using var f = Fresh();
        var client = Owner(f);
        var history = await client.GetStringAsync("/risk-controls/settings/history", TestContext.Current.CancellationToken);

        var res = await client.PutAsJsonAsync(PreClosePath, body, TestContext.Current.CancellationToken);

        res.StatusCode.Should().Be(HttpStatusCode.BadRequest, label);
        (await GetPreClose(client)).SavedCutoff.Should().BeNull(label);
        (await client.GetStringAsync("/risk-controls/settings/history", TestContext.Current.CancellationToken)).Should().Be(history, label);
    }

    public static TheoryData<string, object> InvalidThemes => new()
    {
        { "名前が空", ThemeBody([Theme(" ", ["COIN"])]) },
        { "名前の重複", ThemeBody([Theme("暗号資産", ["COIN"]), Theme("暗号資産 ", ["NVDA"])]) },
        { "銘柄なし", ThemeBody([Theme("暗号資産", [])]) },
        { "空の銘柄コード", ThemeBody([Theme("暗号資産", ["COIN", " "])]) },
        { "上限 0（上限を外す値）", ThemeBody([Theme("暗号資産", ["COIN"], max: 0)]) },
        { "未知の市場", ThemeBody([Theme("暗号資産", ["COIN"], market: 9)]) },
        { "理由なし", ThemeBody([Theme("暗号資産", ["COIN"])], reason: null) },
        { "一覧の欠け", new { reason = "x" } },
    };

    [Theory]
    [MemberData(nameof(InvalidThemes))]
    public async Task T_10_2948_テーマの定義の値域外と理由なしは400で設定も履歴も変えない(string label, object body)
    {
        using var f = Fresh();
        var client = Owner(f);
        var history = await client.GetStringAsync("/risk-controls/settings/history", TestContext.Current.CancellationToken);

        var res = await client.PutAsJsonAsync(ThemePath, body, TestContext.Current.CancellationToken);

        res.StatusCode.Should().Be(HttpStatusCode.BadRequest, label);
        (await GetThemes(client)).Saved.Should().BeNull(label);
        (await client.GetStringAsync("/risk-controls/settings/history", TestContext.Current.CancellationToken)).Should().Be(history, label);
    }

    // ---- 保存・食い違い・履歴 ----

    // T-10-2949: 保存すると保存値が効き（source=Saved）、構成の値と違えば食い違いを示す。構成と同じ値を保存すれば食い違いは無い。
    // 履歴に前後値つきで 1 件ずつ残る（PreCloseControlsChanged=11・ThemeConcentrationChanged=12）。
    [Fact]
    public async Task T_10_2949_保存値が効き構成と違えば食い違いを示し履歴に残る()
    {
        using var f = Fresh();
        var client = Owner(f);

        var saved = await client.PutAsJsonAsync(PreClosePath, PreCloseBody(usCutoff: 90, enabled: true), TestContext.Current.CancellationToken);
        saved.StatusCode.Should().Be(HttpStatusCode.OK);

        var view = await GetPreClose(client);
        view.CutoffSource.Should().Be(ControlValueSource.Saved);
        Minutes(view.EffectiveCutoff.Minutes, Market.UnitedStates).Should().Be(90);
        Minutes(view.ConfiguredCutoff.Minutes, Market.UnitedStates).Should().Be(60);
        view.CutoffDrift.Should().BeTrue("保存値 90 分 ≠ 構成の値 60 分");
        view.EffectiveFlatten.Enabled.Should().BeTrue();
        view.FlattenDrift.Should().BeTrue("保存値は有効・構成の値は無効");

        // 構成と同じ値を保存すれば、保存値が効いていても食い違いは無い。
        (await client.PutAsJsonAsync(PreClosePath, PreCloseBody(), TestContext.Current.CancellationToken)).StatusCode.Should().Be(HttpStatusCode.OK);
        var same = await GetPreClose(client);
        same.CutoffSource.Should().Be(ControlValueSource.Saved);
        same.CutoffDrift.Should().BeFalse();
        same.FlattenDrift.Should().BeFalse();

        var themes = await client.PutAsJsonAsync(
            ThemePath, ThemeBody([Theme(" 暗号資産 ", ["coin", " MARA ", "COIN"], max: 2)]), TestContext.Current.CancellationToken);
        themes.StatusCode.Should().Be(HttpStatusCode.OK);
        var themeView = await GetThemes(client);
        themeView.Source.Should().Be(ControlValueSource.Saved);
        themeView.Effective.Should().ContainSingle();
        themeView.Effective[0].Name.Should().Be("暗号資産", "名前の前後の空白を落とす");
        themeView.Effective[0].Symbols.Should().Equal(["COIN", "MARA"], "大文字へ揃え重複を除く");
        themeView.Drift.Should().BeTrue("定義が既定の 2 テーマと違う");

        var history = await client.GetFromJsonAsync<List<SettingsChangeEntry>>("/risk-controls/settings/history", Web, TestContext.Current.CancellationToken);
        history!.Count(h => h.ChangeType == SettingsChangeType.PreCloseControlsChanged).Should().Be(2);
        var first = history!.Where(h => h.ChangeType == SettingsChangeType.PreCloseControlsChanged).OrderBy(h => h.ChangedAt).First();
        first.Before.Should().Contain("未保存（構成の値）");
        first.After.Should().Contain("UnitedStates = 90").And.Contain("Enabled = True");
        history!.Should().ContainSingle(h => h.ChangeType == SettingsChangeType.ThemeConcentrationChanged)
            .Which.After.Should().Contain("暗号資産").And.Contain("COIN/MARA");
    }

    // ---- 審査とサイジング文脈への反映（再起動なし） ----

    private static PortfolioState State(params string[] held) => new()
    {
        LedgerEquity = 100_000m,
        OpenPositionCount = 0,
        InvestedCapital = 0m,
        DailyOrderedAmount = 0m,
        DailyRealizedPnl = 0m,
        UnrealizedPnl = 0m,
        HeldOrPendingSymbols = held.Select(s => (s, Market.UnitedStates)).ToHashSet(),
    };

    private static OrderScreeningService Screening(IRiskSettingsStore store, DateTimeOffset now, PortfolioState state)
    {
        var clock = new FakeClock(now, DateOnly.FromDateTime(now.UtcDateTime));
        var builder = new PortfolioSnapshotBuilder(
            new FakePortfolioStateProvider(state), new InMemoryKillSwitchStore(), new InMemoryPauseStore(),
            FakeBrokerAccountObservations.NotObserved(), FakeInformationDegradation.Affirmed(),
            capitalBaseline: FakeCapitalBaseline.Of(100_000m));
        return new OrderScreeningService(
            store, builder, new InMemoryLockoutStore(), clock, new WeekendBusinessCalendar(),
            new InMemoryBuyInInferenceStore(), new InMemoryPortfolioLedgerStore(), TestShortSellContexts.Unavailable(clock),
            new InMemoryEntryReservationStore(), ConfiguredSessionAndThemeControls.Default);
    }

    private static Task<ScreeningOutcome> Screen(OrderScreeningService service, string symbol, DateTimeOffset at) =>
        service.ScreenAsync(
            new TradeDecisionMade(
                Guid.NewGuid(),
                new OrderIntent(symbol, Market.UnitedStates, TradeSide.Buy, ProductType.Cash, BrokerProvider.InternalPaper, 10, 20m, PositionEffect.Open),
                "定時判断", at),
            TestContext.Current.CancellationToken);

    // T-10-2950: 審査は保存値で締め切りを判定する（構成 60 分のままでも、保存した 90 分が効く）。保存値を消せば（null）構成の値へ戻る。
    [Fact]
    public async Task T_10_2950_審査は保存した締め切りで判定する()
    {
        var store = new InMemoryRiskSettingsStore();
        var now = new DateTimeOffset(2026, 10, 19, 18, 45, 0, TimeSpan.Zero); // 14:45 ET＝閉場の 75 分前

        (await Screen(Screening(store, now, State()), "AAPL", now)).IsApproved.Should().BeTrue("構成の 60 分では締め切りの前");

        store.Save(store.GetCurrent() with
        {
            PreCloseEntryCutoff = new PreCloseEntryCutoffSettings(new Dictionary<Market, int> { [Market.UnitedStates] = 90, [Market.Japan] = 60 }),
        });
        var outcome = await Screen(Screening(store, now, State()), "AAPL", now);
        outcome.IsApproved.Should().BeFalse("保存した 90 分では締め切りの中");
        outcome.Rejected!.Reasons.Should().Equal(RejectionReason.PreCloseEntryCutoff);
    }

    // T-10-2951: 審査は保存したテーマの定義で判定する（定義は既定を丸ごと置き換える）。
    [Fact]
    public async Task T_10_2951_審査は保存したテーマの定義で判定する()
    {
        var store = new InMemoryRiskSettingsStore();
        var now = new DateTimeOffset(2026, 10, 19, 15, 0, 0, TimeSpan.Zero); // 11:00 ET

        (await Screen(Screening(store, now, State("AAPL")), "MSFT", now)).IsApproved.Should().BeTrue("既定の定義では AAPL・MSFT はテーマに属さない");
        (await Screen(Screening(store, now, State("MARA")), "COIN", now)).IsApproved.Should().BeFalse("既定の暗号資産の上限 1");

        store.Save(store.GetCurrent() with
        {
            ThemeConcentration = new ThemeConcentrationSettings([new ThemeLimit("大型テック", Market.UnitedStates, ["AAPL", "MSFT"], 1)]),
        });

        var outcome = await Screen(Screening(store, now, State("AAPL")), "MSFT", now);
        outcome.IsApproved.Should().BeFalse("保存した定義の上限 1");
        outcome.Rejected!.Reasons.Should().Equal(RejectionReason.ThemeConcentrationExceeded);
        (await Screen(Screening(store, now, State("MARA")), "COIN", now)).IsApproved.Should().BeTrue("保存した定義は既定を丸ごと置き換える");
    }

    // T-10-2952: サイジング文脈は手仕舞いの効いている値を運ぶ（保存値が無ければ構成の値・あれば保存値）。本番の配線で構成の値が渡る
    // （SizingContextService が省略時の既定へ倒れていない）。REST の応答にも同じ値が載る。
    [Fact]
    public async Task T_10_2952_サイジング文脈は手仕舞いの効いている値を運び本番の配線で構成の値が渡る()
    {
        using var f = Fresh(new Dictionary<string, string?> { ["PreCloseFlatten:Enabled"] = "true", ["PreCloseFlatten:Minutes:UnitedStates"] = "20" });
        using (var scope = f.Services.CreateScope())
        {
            var view = scope.ServiceProvider.GetRequiredService<SizingContextService>().Build();
            view.PreCloseFlatten!.Enabled.Should().BeTrue("構成の値（リスク管理の PreCloseFlatten:Enabled）");
            view.PreCloseFlatten.MinutesFor(Market.UnitedStates).Should().Be(20);
        }

        var client = Owner(f);
        (await client.PutAsJsonAsync(PreClosePath, PreCloseBody(enabled: false, usFlatten: 30), TestContext.Current.CancellationToken))
            .StatusCode.Should().Be(HttpStatusCode.OK);

        using (var scope = f.Services.CreateScope())
        {
            var view = scope.ServiceProvider.GetRequiredService<SizingContextService>().Build();
            view.PreCloseFlatten!.Enabled.Should().BeFalse("保存値が効く");
            view.PreCloseFlatten.MinutesFor(Market.UnitedStates).Should().Be(30);
        }

        var service = f.CreateClient();
        service.DefaultRequestHeaders.Add(TestAuthHandler.RolesHeader, "trading-service");
        using var doc = JsonDocument.Parse(await service.GetStringAsync("/risk-controls/sizing-context", TestContext.Current.CancellationToken));
        var flatten = doc.RootElement.GetProperty("preCloseFlatten");
        flatten.GetProperty("enabled").GetBoolean().Should().BeFalse();
        flatten.GetProperty("minutes").GetProperty("UnitedStates").GetInt32().Should().Be(30);
    }

    // T-10-2952 の補い: 構成の手仕舞いの値が範囲外なら起動を止める（締め切り・テーマの構成と同じ fail-fast）。
    [Theory]
    [InlineData("PreCloseFlatten:Enabled", "yes")]
    [InlineData("PreCloseFlatten:Minutes:UnitedStates", "0")]
    [InlineData("PreCloseFlatten:Minutes:Japan", "61")]
    [InlineData("PreCloseFlatten:Minutes:Europe", "15")]
    public void T_10_2952_リスク管理の手仕舞いの構成が範囲外なら起動を止める(string key, string value)
    {
        using var f = Fresh(new Dictionary<string, string?> { [key] = value });

        var act = () => f.CreateClient();

        act.Should().Throw<InvalidOperationException>();
    }
}

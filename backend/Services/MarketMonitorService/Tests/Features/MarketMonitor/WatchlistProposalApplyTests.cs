using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AiStockTrading.Shared.Contracts.Trading;
using AwesomeAssertions;
using MarketMonitorService.Domain;
using MarketMonitorService.Features.MarketMonitor;
using MarketMonitorService.Infrastructure.Persistence;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;

namespace MarketMonitorService.Tests;

// FR-13, FR-14, ADR-0042 決定 1・2, ADR-0031, #1025, IADR-0433: `/policy` の入れ替え案の一括適用（T-10-1376〜T-10-1384）。
// 🔴 案を作った時点から監視銘柄が変わっていれば 1 件も適用しない／各銘柄は SC-02 と同じ規則／変更者は本人（代理）／
// 変更履歴は SC-02 と同じ形／Finnhub の推定は警告のみ（適用を止めない）。
public class WatchlistProposalApplyTests
{
    private static readonly MonitoredSymbol Aapl = new("AAPL", Market.UnitedStates);
    private static readonly MonitoredSymbol Msft = new("MSFT", Market.UnitedStates);
    private static readonly MonitoredSymbol Toyota = new("7203", Market.Japan);

    private static ProposedWatchlistChange Add(string symbol, string reason = "理由") => new(ProposedWatchlistAction.Add, symbol, reason);

    private static ProposedWatchlistChange Remove(string symbol, string reason = "理由") => new(ProposedWatchlistAction.Remove, symbol, reason);

    // T-10-1376: 期待値（案を作った時点の監視銘柄）と現在が違えば 1 件も適用しない（並び・大小文字は問わない）。
    [Fact]
    public void 案の作成後に監視銘柄が変わっていれば適用しない()
    {
        WatchlistProposalPlan.Plan([Aapl, Msft], [Aapl], [Add("NVDA")]).Stale.Should().BeTrue();
        WatchlistProposalPlan.Plan([Aapl], [Aapl, Msft], [Add("NVDA")]).Stale.Should().BeTrue();
        WatchlistProposalPlan.Plan([Aapl, Msft], [new("msft", Market.UnitedStates), Aapl], [Add("NVDA")]).Stale.Should().BeFalse();
        WatchlistProposalPlan.Plan([Aapl, Toyota], [Aapl, new("7203", Market.UnitedStates)], [Add("NVDA")]).Stale.Should().BeTrue("市場も比べる");
    }

    // T-10-1377: 銘柄ごとに SC-02 と同じ規則で検証し、通らない銘柄は理由つきで適用しない（通った銘柄だけ適用）。
    [Fact]
    public void 銘柄ごとに検証し通った銘柄だけを適用する()
    {
        var plan = WatchlistProposalPlan.Plan([Aapl, Msft], [Aapl, Msft], [Add("NVDA"), Add("AAPL"), Remove("MSFT"), Remove("META")]);

        plan.Stale.Should().BeFalse();
        plan.Items.Select(i => (i.Change.Symbol, i.Applied)).Should().Equal(("NVDA", true), ("AAPL", false), ("MSFT", true), ("META", false));
        plan.Items[1].SkipReason.Should().Contain("既に監視対象");
        plan.Items[3].SkipReason.Should().Contain("監視対象にありません");
        plan.Resulting.Should().BeEquivalentTo([Aapl, new MonitoredSymbol("NVDA", Market.UnitedStates)]);
    }

    // T-10-1378: 案の形の違反（件数・書式・理由・重複・期待値の欠落）は要求の誤り（1 件も適用しない）。
    [Theory]
    [MemberData(nameof(BadShapes))]
    public void 案の形の違反は検証で止める(IReadOnlyList<ProposedWatchlistChange>? changes, bool expectedMissing)
    {
        WatchlistProposalPlan.ValidateShape(changes, expectedMissing ? null : [Aapl]).Should().NotBeNull();
    }

    public static TheoryData<IReadOnlyList<ProposedWatchlistChange>?, bool> BadShapes() => new()
    {
        { null, false },
        { [], false },
        { [Add("NVDA")], true },
        { [.. new[] { "A", "B", "C", "D", "E", "F" }.Select(s => Add(s))], false },
        { [Add("nvda")], false },
        { [Add("7203")], false },
        { [Add("NVDA", "")], false },
        { [Add("NVDA", new string('理', 201))], false },
        { [Add("NVDA"), Remove("NVDA")], false },
    };

    // T-10-1379: 適用は 1 回の保存で行い、変更履歴は SC-02 と同じ種別・前後値で 1 件ずつ（変更者・案の理由と出所）。
    [Fact]
    public void 適用は変更履歴にSC02と同じ形で残る()
    {
        var store = new InMemoryMonitoredSymbolStore(MonitorDefaults.CreateSettings([Aapl, Msft]));
        var log = new InMemoryMonitorSettingsChangeLog();
        var svc = new MonitorWatchlistService(store, log, new FakeClock(DateTimeOffset.UnixEpoch));

        var plan = svc.ApplyProposal([Aapl, Msft], [Add("NVDA", "AI 需要"), Remove("MSFT", "値動きが小さい"), Add("AAPL")], "developer", "daily-2026-09-28-v3");

        plan.Items.Count(i => i.Applied).Should().Be(2);
        store.GetSettings().MonitoredSymbols.Select(s => s.Symbol).Should().BeEquivalentTo(["AAPL", "NVDA"]);
        var history = log.GetHistory();
        history.Should().HaveCount(2, "適用しなかった銘柄は変更履歴に書かない（変更ではない）");
        history.Should().OnlyContain(h => h.Actor == "developer");
        history.Select(h => h.ChangeType).Should().BeEquivalentTo(
            [MonitorSettingsChangeType.WatchlistSymbolAdded, MonitorSettingsChangeType.WatchlistSymbolRemoved]);
        var added = history.Single(h => h.ChangeType == MonitorSettingsChangeType.WatchlistSymbolAdded);
        added.Reason.Should().StartWith("AI 需要").And.Contain("daily-2026-09-28-v3").And.Contain("Discord");
        added.Before.Should().Be("AAPL@UnitedStates, MSFT@UnitedStates");
        added.After.Should().Be("AAPL@UnitedStates, MSFT@UnitedStates, NVDA@UnitedStates");
    }

    // T-10-1380: 変わっていれば保存も履歴も無い。適用できる銘柄が 1 件も無ければ保存しない。
    [Fact]
    public void 変わっていれば保存も履歴も無い()
    {
        var store = new InMemoryMonitoredSymbolStore(MonitorDefaults.CreateSettings([Aapl, Msft]));
        var log = new InMemoryMonitorSettingsChangeLog();
        var svc = new MonitorWatchlistService(store, log, new FakeClock(DateTimeOffset.UnixEpoch));

        svc.ApplyProposal([Aapl], [Add("NVDA")], "developer", "ref").Stale.Should().BeTrue();
        svc.ApplyProposal([Aapl, Msft], [Add("AAPL")], "developer", "ref").AnyApplied.Should().BeFalse();

        store.GetSettings().MonitoredSymbols.Should().BeEquivalentTo([Aapl, Msft]);
        log.GetHistory().Should().BeEmpty();
    }

    // T-10-1381（利用者裁定 2026-09-26・ADR-0031）: Finnhub の推定は警告だけ（適用を止めない）。Finnhub を使わない構成では出さない。
    [Fact]
    public void Finnhubの推定は警告だけで対象外の構成では出さない()
    {
        var finnhub = new WatchlistVolumeEstimator("finnhub", pollIntervalSeconds: 60, provisionalDailyLimit: 300);
        finnhub.Estimate(3).Should().Be(new MarketMonitorService.Features.MarketMonitor.ApplyWatchlistProposal.FinnhubDailyVolumeEstimateView(4320, 300, true));
        new WatchlistVolumeEstimator("finnhub", 86400, 300).Estimate(1)!.Exceeds.Should().BeFalse();
        new WatchlistVolumeEstimator(null, 60, 300).Estimate(3).Should().BeNull();
        new WatchlistVolumeEstimator("moomoo", 60, 300).Estimate(3).Should().BeNull();
    }

    // ---- 本番の組み立て（Program.cs）を通す ----

    private static WebApplicationFactory<Program> Configured(MonitorWorkerWebApplicationFactory baseFactory) =>
        baseFactory.WithWebHostBuilder(b =>
        {
            b.UseSetting("Monitor:DelegatedActor:TrustedClientIds", "ai-stock-trading-owner");
            b.UseSetting("MarketData:Provider", "finnhub");
            b.UseSetting("Monitor:PollIntervalSeconds", "60");
        });

    private static HttpClient Bot(WebApplicationFactory<Program> factory, string roles = "trading-owner")
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add(TestAuthHandler.RolesHeader, roles);
        client.DefaultRequestHeaders.Add(TestAuthHandler.NameHeader, TestAuthHandler.NoName);
        client.DefaultRequestHeaders.Add(TestAuthHandler.AzpHeader, "ai-stock-trading-owner");
        return client;
    }

    private static async Task SeedAsync(HttpClient owner, params string[] symbols)
    {
        foreach (var s in symbols)
            (await owner.PostAsJsonAsync("/monitor/watchlist", new { Symbol = s, Market = Market.UnitedStates, Reason = "初期" }))
                .StatusCode.Should().Be(HttpStatusCode.OK);
    }

    private static object Body(IEnumerable<string> expected, object[] changes, string? onBehalfOf = "developer") => new
    {
        ExpectedWatchlist = expected.Select(s => new { Symbol = s, Market = Market.UnitedStates }).ToArray(),
        Changes = changes,
        ProposalRef = "daily-2026-09-28-v3",
        OnBehalfOf = onBehalfOf,
    };

    // T-10-1382: 本番の組み立てで、代理の変更者（本人）が変更履歴に残り、内訳と Finnhub の推定（警告のみ）が返る。
    [Fact]
    public async Task 本番の組み立てで適用し変更者は本人として残る()
    {
        await using var baseFactory = new MonitorWorkerWebApplicationFactory();
        await using var factory = Configured(baseFactory);
        var owner = factory.CreateClient();
        owner.DefaultRequestHeaders.Add(TestAuthHandler.RolesHeader, "trading-owner");
        var before = (await owner.GetFromJsonAsync<JsonElement>("/monitor/watchlist")).EnumerateArray().Select(e => e.GetProperty("symbol").GetString()!).ToList();
        await SeedAsync(owner, "MSFT");

        var response = await Bot(factory).PostAsJsonAsync("/monitor/watchlist/proposal-apply", Body(
            [.. before, "MSFT"],
            [new { Action = "add", Symbol = "NVDA", Reason = "AI 需要" }, new { Action = "add", Symbol = "MSFT", Reason = "重複" }]));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("actor").GetString().Should().Be("developer");
        body.GetProperty("items").EnumerateArray().Select(i => (i.GetProperty("symbol").GetString(), i.GetProperty("applied").GetBoolean()))
            .Should().Equal(("NVDA", true), ("MSFT", false));
        var estimate = body.GetProperty("estimate");
        estimate.GetProperty("estimatedDailyRequests").GetInt64().Should().Be((before.Count + 2) * 1440L);
        estimate.GetProperty("exceeds").GetBoolean().Should().BeTrue("推定は警告だけで、適用は止めていない");

        var history = await owner.GetFromJsonAsync<JsonElement>("/monitor/watchlist/history");
        history.EnumerateArray().Should().Contain(h =>
            h.GetProperty("actor").GetString() == "developer" && h.GetProperty("reason").GetString()!.StartsWith("AI 需要"));
    }

    // T-10-1383: 案の作成後に変わっていれば 409 で 1 件も適用しない。形の違反・代理の値域外は 400。
    [Fact]
    public async Task 変わっていれば409で形の違反は400()
    {
        await using var baseFactory = new MonitorWorkerWebApplicationFactory();
        await using var factory = Configured(baseFactory);
        var owner = factory.CreateClient();
        owner.DefaultRequestHeaders.Add(TestAuthHandler.RolesHeader, "trading-owner");
        await SeedAsync(owner, "MSFT");

        var stale = await Bot(factory).PostAsJsonAsync("/monitor/watchlist/proposal-apply", Body(
            ["ZZZZ"], [new { Action = "add", Symbol = "NVDA", Reason = "r" }]));
        stale.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await owner.GetFromJsonAsync<JsonElement>("/monitor/watchlist")).EnumerateArray()
            .Should().NotContain(e => e.GetProperty("symbol").GetString() == "NVDA");

        (await Bot(factory).PostAsJsonAsync("/monitor/watchlist/proposal-apply", Body(["MSFT"], [new { Action = "buy", Symbol = "NVDA", Reason = "r" }])))
            .StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await Bot(factory).PostAsJsonAsync("/monitor/watchlist/proposal-apply", Body(["MSFT"], [new { Action = "add", Symbol = "nvda", Reason = "r" }])))
            .StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await Bot(factory).PostAsJsonAsync("/monitor/watchlist/proposal-apply", Body(["MSFT"], [new { Action = "add", Symbol = "NVDA", Reason = "r" }], "bad name\n")))
            .StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    // T-10-1384: 変更は利用者のみ（サービス主体は 403）。登録を読み取りグループへ移すとこの試験が赤になる。
    [Fact]
    public async Task サービス主体は適用できない()
    {
        await using var baseFactory = new MonitorWorkerWebApplicationFactory();
        await using var factory = Configured(baseFactory);

        var response = await Bot(factory, roles: "trading-service").PostAsJsonAsync("/monitor/watchlist/proposal-apply", Body(
            [], [new { Action = "add", Symbol = "NVDA", Reason = "r" }]));

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }
}

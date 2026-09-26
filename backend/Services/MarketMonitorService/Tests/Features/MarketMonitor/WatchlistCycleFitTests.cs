using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AiStockTrading.Shared.Contracts.Trading;
using AwesomeAssertions;
using MarketMonitorService.Domain;
using MarketMonitorService.Features.MarketMonitor;
using MarketMonitorService.Infrastructure.ExternalServices;
using MarketMonitorService.Infrastructure.Persistence;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Xunit;

namespace MarketMonitorService.Tests;

// FR-03, FR-13, SC-02, ADR-0043（計画）決定 2 (b)・3・4, ADR-0042 決定 1, #1030, IADR-0437（T-10-1436〜T-10-1445）:
// 監視銘柄を増やす変更は「1 巡回（保有 ＋ 監視銘柄）が巡回間隔に収まること」を満たさなければ適用しない。SC-02 の追加・全置換・
// Discord の入れ替え案の適用の 3 つの口で同じ検査を通す。除外は止めない。1 日の見積りは開場中の巡回で数え、300 回/日と比べない。
public class WatchlistCycleFitTests
{
    private static readonly MonitoredSymbol Aapl = new("AAPL", Market.UnitedStates);
    private static readonly MonitoredSymbol Msft = new("MSFT", Market.UnitedStates);
    private static readonly MonitoredSymbol Nvda = new("NVDA", Market.UnitedStates);

    private static ProposedWatchlistChange Add(string symbol) => new(ProposedWatchlistAction.Add, symbol, "理由");

    private static ProposedWatchlistChange Remove(string symbol) => new(ProposedWatchlistAction.Remove, symbol, "理由");

    // T-10-1436: 収まる ⇔ (保有 ＋ 監視銘柄) × 60 ≤ 自制レート × 巡回間隔（秒）。境界は整数で比べる。
    [Theory]
    [InlineData(5, 60, 0, 5, true)]    // 既定の組は 5 銘柄まで（ADR-0043 実測 7）
    [InlineData(5, 60, 0, 6, false)]   // 6 銘柄は 72 秒かかる
    [InlineData(5, 60, 1, 5, false)]   // 保有も数える（6 要求）
    [InlineData(12, 60, 1, 11, true)]  // IADR-0434 の稼働構成は 12 要求まで
    [InlineData(12, 60, 1, 12, false)]
    [InlineData(5, 90, 0, 7, true)]    // 7.5 要求の切り捨て
    [InlineData(5, 90, 0, 8, false)]
    [InlineData(0, 60, 0, 1, true)]    // 0 以下のレートは実装と同じく 1 回/分へ寄せる
    [InlineData(0, 60, 0, 2, false)]
    public void 巡回が間隔に収まるかは整数で比べる(int rate, int interval, int holdings, int watchlist, bool fits)
    {
        var fit = new WatchlistCycleFit(rate, interval, holdings);

        fit.Fits(watchlist).Should().Be(fits);
        fit.Capacity.Should().Be(Math.Max(1, rate) * interval / 60);
        fit.Describe(watchlist).Should().Contain($"保有 {holdings}").And.Contain($"監視銘柄 {watchlist}");
    }

    // T-10-1437: 入れ替え案では除外を先に当て、追加は案の順に収まる範囲だけ適用する。収まらない追加は理由つきで適用しない。
    // 除外は、監視銘柄が既に予算を超えていても止めない。
    [Fact]
    public void 入れ替え案は除外を先に当て収まる範囲だけ追加する()
    {
        var fit = new WatchlistCycleFit(RequestsPerMinute: 3, PollIntervalSeconds: 60, HoldingsCount: 1); // 3 要求まで

        var plan = WatchlistProposalPlan.Plan([Aapl], [Aapl], [Add("NVDA"), Add("AMZN"), Remove("AAPL"), Add("META")], fit);

        plan.Items.Select(i => (i.Change.Symbol, i.Applied)).Should().Equal(
            ("NVDA", true), ("AMZN", true), ("AAPL", true), ("META", false));
        plan.Items[3].SkipReason.Should().Contain("Finnhub の巡回に収まりません").And.Contain("1 巡回 4 要求").And.Contain("3 要求");
        plan.Resulting.Select(s => s.Symbol).Should().BeEquivalentTo(["NVDA", "AMZN"]);

        var over = WatchlistProposalPlan.Plan([Aapl, Msft, Nvda], [Aapl, Msft, Nvda], [Remove("MSFT"), Add("META")],
            new WatchlistCycleFit(1, 60, 0));
        over.Items.Select(i => (i.Change.Symbol, i.Applied)).Should().Equal(("MSFT", true), ("META", false));

        WatchlistProposalPlan.Plan([Aapl], [Aapl], [Add("NVDA"), Add("AMZN")], cycleFit: null).Items
            .Should().OnlyContain(i => i.Applied, "検査しない構成（Finnhub 以外）では止めない");
    }

    // T-10-1438: SC-02 の追加は、足した後に収まらないなら検証エラー（400）。収まるなら通る。除外は予算を超えていても止めない。
    [Fact]
    public void SC02の追加は収まらないなら拒否し除外は止めない()
    {
        var store = new InMemoryMonitoredSymbolStore(MonitorDefaults.CreateSettings([Aapl, Msft]));
        var log = new InMemoryMonitorSettingsChangeLog();
        var svc = new MonitorWatchlistService(store, log, new FakeClock(DateTimeOffset.UnixEpoch));
        var fit = new WatchlistCycleFit(RequestsPerMinute: 3, PollIntervalSeconds: 60, HoldingsCount: 1);

        var reject = () => svc.Add("NVDA", Market.UnitedStates, "owner", "追加", fit);
        reject.Should().Throw<ArgumentException>().WithMessage("*Finnhub の巡回に収まりません*1 巡回 4 要求*");
        store.GetSettings().MonitoredSymbols.Should().HaveCount(2);
        log.GetHistory().Should().BeEmpty("拒否した追加は変更ではない");

        svc.Add("NVDA", Market.UnitedStates, "owner", "追加", new WatchlistCycleFit(4, 60, 1)).Should().HaveCount(3);

        svc.Remove("MSFT", Market.UnitedStates, "owner", "除外").Should().HaveCount(2, "除外は検査しない（予算を減らす向き）");
    }

    // T-10-1439: 全置換は、今は無い銘柄を含み、かつ収まらないなら拒否する。除外だけ・並べ替えだけは予算を超えていても通す。
    [Fact]
    public void 全置換は増やして収まらないなら拒否する()
    {
        var store = new InMemoryMonitoredSymbolStore(MonitorDefaults.CreateSettings([Aapl, Msft, Nvda]));
        var svc = new MonitorSettingsService(store, new InMemoryMonitorSettingsChangeLog(), new FakeClock(DateTimeOffset.UnixEpoch));
        var current = store.GetSettings();
        var tight = new WatchlistCycleFit(RequestsPerMinute: 2, PollIntervalSeconds: 60, HoldingsCount: 0);

        var swap = () => svc.Replace(current with { MonitoredSymbols = [Aapl, Msft, new("META", Market.UnitedStates)] }, "owner", "入れ替え", tight);
        swap.Should().Throw<ArgumentException>().WithMessage("*Finnhub の巡回に収まりません*");
        store.GetSettings().MonitoredSymbols.Should().BeEquivalentTo([Aapl, Msft, Nvda]);

        svc.Replace(current with { MonitoredSymbols = [Nvda, Aapl, Msft] }, "owner", "並べ替え", tight).MonitoredSymbols
            .Should().HaveCount(3, "増やさない置換は止めない");
        svc.Replace(current with { MonitoredSymbols = [Aapl] }, "owner", "除外", tight).MonitoredSymbols.Should().ContainSingle();
        svc.Replace(store.GetSettings() with { MonitoredSymbols = [Aapl, Msft] }, "owner", "追加", tight).MonitoredSymbols
            .Should().HaveCount(2, "収まる追加は通す");
    }

    // T-10-1444（ADR-0043 決定 3）: 見積りは銘柄ごとにその市場の場中 ÷ 巡回間隔で数え、保有も数える。上限は設定したときだけ比べる。
    [Fact]
    public void 見積りは開場中の巡回で数え上限は設定したときだけ比べる()
    {
        Market[] markets = [Market.UnitedStates, Market.UnitedStates, Market.Japan];

        var estimate = new WatchlistVolumeEstimator("finnhub", 60, dailyLimit: null).Estimate(markets)!;
        estimate.EstimatedDailyRequests.Should().Be(390 + 390 + 330);
        estimate.ProvisionalDailyLimit.Should().BeNull();
        estimate.Exceeds.Should().BeFalse("暫定の 300 回/日とは比べない");

        new WatchlistVolumeEstimator("finnhub", 120, dailyLimit: 500).Estimate(markets)!
            .Should().Be(new MarketMonitorService.Features.MarketMonitor.ApplyWatchlistProposal.FinnhubDailyVolumeEstimateView(195 + 195 + 165, 500, true));
    }

    // ---- 本番の組み立て（Program.cs）を通す ----

    private static WebApplicationFactory<Program> Configured(
        MonitorWorkerWebApplicationFactory baseFactory, InMemoryPositionStore positions, string provider = "finnhub", string rate = "3") =>
        baseFactory.WithWebHostBuilder(b =>
        {
            b.UseSetting("Monitor:DelegatedActor:TrustedClientIds", "ai-stock-trading-owner");
            b.UseSetting("MarketData:Provider", provider);
            b.UseSetting("MarketData:Finnhub:RequestsPerMinute", rate);
            b.UseSetting("Monitor:PollIntervalSeconds", "60");
            b.ConfigureTestServices(s =>
            {
                s.RemoveAll<IPositionStore>();
                s.AddSingleton<IPositionStore>(positions);
            });
        });

    private static InMemoryPositionStore Holding(params string[] symbols)
    {
        var store = new InMemoryPositionStore();
        store.Set([.. symbols.Select(s => new HeldPosition(s, Market.UnitedStates, TradeSide.Buy, 1, 100m, 90m))]);
        return store;
    }

    private static HttpClient Owner(WebApplicationFactory<Program> factory)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add(TestAuthHandler.RolesHeader, "trading-owner");
        return client;
    }

    private static Task<HttpResponseMessage> PostAdd(HttpClient owner, string symbol) =>
        owner.PostAsJsonAsync("/monitor/watchlist", new { Symbol = symbol, Market = Market.UnitedStates, Reason = "追加" });

    private static async Task<List<string>> WatchlistOf(HttpClient owner) =>
        [.. (await owner.GetFromJsonAsync<JsonElement>("/monitor/watchlist")).EnumerateArray().Select(e => e.GetProperty("symbol").GetString()!)];

    // T-10-1440: SC-02 の追加の口（POST /monitor/watchlist）は、構成のレート・間隔と保有（リスク管理の照会）で数え、収まらないなら 400。
    [Fact]
    public async Task SC02の追加の口は保有を数えて収まらないなら400()
    {
        await using var baseFactory = new MonitorWorkerWebApplicationFactory();
        await using var factory = Configured(baseFactory, Holding("AAPL"));
        var owner = Owner(factory);

        (await PostAdd(owner, "MSFT")).StatusCode.Should().Be(HttpStatusCode.OK);
        (await PostAdd(owner, "NVDA")).StatusCode.Should().Be(HttpStatusCode.OK); // 保有 1 ＋ 監視 2 ＝ 3 要求（上限 3）
        var rejected = await PostAdd(owner, "AMZN");

        rejected.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await rejected.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("error").GetString()
            .Should().Contain("Finnhub の巡回に収まりません").And.Contain("保有 1");
        (await WatchlistOf(owner)).Should().BeEquivalentTo(["MSFT", "NVDA"]);
        using var remove = new HttpRequestMessage(HttpMethod.Delete, "/monitor/watchlist")
        {
            Content = JsonContent.Create(new { Symbol = "MSFT", Market = Market.UnitedStates, Reason = "除外" }),
        };
        (await owner.SendAsync(remove)).StatusCode.Should().Be(HttpStatusCode.OK, "除外は止めない");
    }

    // T-10-1441: 入れ替え案の口は、収まらない追加を適用せず内訳に理由を載せ、除外と収まる追加は適用する（200）。
    [Fact]
    public async Task 入れ替え案の口は収まらない追加を内訳に理由つきで残す()
    {
        await using var baseFactory = new MonitorWorkerWebApplicationFactory();
        await using var factory = Configured(baseFactory, Holding("AAPL"));
        var owner = Owner(factory);
        (await PostAdd(owner, "MSFT")).StatusCode.Should().Be(HttpStatusCode.OK);
        var bot = factory.CreateClient();
        bot.DefaultRequestHeaders.Add(TestAuthHandler.RolesHeader, "trading-owner");
        bot.DefaultRequestHeaders.Add(TestAuthHandler.NameHeader, TestAuthHandler.NoName);
        bot.DefaultRequestHeaders.Add(TestAuthHandler.AzpHeader, "ai-stock-trading-owner");

        var response = await bot.PostAsJsonAsync("/monitor/watchlist/proposal-apply", new
        {
            ExpectedWatchlist = new[] { new { Symbol = "MSFT", Market = Market.UnitedStates } },
            Changes = new object[]
            {
                new { Action = "add", Symbol = "NVDA", Reason = "AI 需要" },
                new { Action = "add", Symbol = "AMZN", Reason = "小売" },
                new { Action = "remove", Symbol = "MSFT", Reason = "値動きが小さい" },
                new { Action = "add", Symbol = "META", Reason = "広告" },
            },
            ProposalRef = "daily-2026-09-28-v3",
            OnBehalfOf = "developer",
        });

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var items = (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("items").EnumerateArray().ToList();
        items.Select(i => (i.GetProperty("symbol").GetString(), i.GetProperty("applied").GetBoolean()))
            .Should().Equal(("NVDA", true), ("AMZN", true), ("MSFT", true), ("META", false));
        items[3].GetProperty("skipReason").GetString().Should().Contain("Finnhub の巡回に収まりません");
        (await WatchlistOf(owner)).Should().BeEquivalentTo(["NVDA", "AMZN"]);
    }

    // T-10-1442: Finnhub を使わない構成では検査しない（自制レートが無い）。
    [Fact]
    public async Task Finnhubを使わない構成では検査しない()
    {
        await using var baseFactory = new MonitorWorkerWebApplicationFactory();
        await using var factory = Configured(baseFactory, Holding("AAPL"), provider: "", rate: "1");
        var owner = Owner(factory);

        foreach (var s in new[] { "MSFT", "NVDA", "AMZN" })
            (await PostAdd(owner, s)).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    // T-10-1443: 全置換の口（PUT /monitor/settings）も、増やして収まらないなら 400。
    [Fact]
    public async Task 全置換の口も増やして収まらないなら400()
    {
        await using var baseFactory = new MonitorWorkerWebApplicationFactory();
        await using var factory = Configured(baseFactory, Holding("AAPL"), rate: "2");
        var owner = Owner(factory);
        var settings = await owner.GetFromJsonAsync<JsonElement>("/monitor/settings");

        object Body(params string[] symbols) => new
        {
            MovementThresholdRatio = settings.GetProperty("movementThresholdRatio").GetDecimal(),
            Cooldown = settings.GetProperty("cooldown").GetString(),
            MonitoredSymbols = symbols.Select(s => new { Symbol = s, Market = Market.UnitedStates }).ToArray(),
            Reason = "全置換",
        };

        (await owner.PutAsJsonAsync("/monitor/settings", Body("MSFT", "NVDA"))).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await owner.PutAsJsonAsync("/monitor/settings", Body("MSFT"))).StatusCode.Should().Be(HttpStatusCode.OK);
        (await WatchlistOf(owner)).Should().BeEquivalentTo(["MSFT"]);
    }

    // T-10-1445（ADR-0043 決定 3）: 入れ替え案の応答の推定は、適用後の監視銘柄 ＋ 保有を開場中の巡回（390）で数え、上限とは比べない。
    [Fact]
    public async Task 入れ替え案の応答の推定は開場中の巡回で数え上限と比べない()
    {
        await using var baseFactory = new MonitorWorkerWebApplicationFactory();
        await using var factory = Configured(baseFactory, Holding("AAPL"), rate: "12");
        var bot = Owner(factory);

        var response = await bot.PostAsJsonAsync("/monitor/watchlist/proposal-apply", new
        {
            ExpectedWatchlist = Array.Empty<object>(),
            Changes = new object[] { new { Action = "add", Symbol = "NVDA", Reason = "AI 需要" } },
            ProposalRef = "daily-2026-09-28-v4",
        });

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var estimate = (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("estimate");
        estimate.GetProperty("estimatedDailyRequests").GetInt64().Should().Be(2 * 390, "監視 1 ＋ 保有 1、各 390 巡回");
        estimate.GetProperty("provisionalDailyLimit").ValueKind.Should().Be(JsonValueKind.Null);
        estimate.GetProperty("exceeds").GetBoolean().Should().BeFalse();
    }
}

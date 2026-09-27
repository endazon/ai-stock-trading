extern alias MarketMonitorWorker;
extern alias ReportWorker;

using AiStockTrading.Shared.Contracts.Trading;
using AwesomeAssertions;
using Grpc.Core;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using TradeDecisionService.Features.TradeDecision;
using TradeDecisionService.Infrastructure.ExternalServices;
using Xunit;
using ConfirmedDailyPolicy = ReportWorker::ReportService.Features.Reports.ConfirmedDailyPolicy;
using DailyPolicyWireMapping = ReportWorker::ReportService.Features.Reports.DailyPolicyWireMapping;
using MonitoredSymbol = MarketMonitorWorker::MarketMonitorService.Domain.MonitoredSymbol;
using MonitorProto = AiStockTrading.Shared.Grpc.MarketMonitor.V1;
using ReportProto = AiStockTrading.Shared.Grpc.Report.V1;
using WatchlistAsOfResponse = MarketMonitorWorker::MarketMonitorService.Features.MarketMonitor.GetWatchlistAsOf.WatchlistAsOfResponse;
using WatchlistWireMapping = MarketMonitorWorker::MarketMonitorService.Features.MarketMonitor.WatchlistWireMapping;

namespace TradeDecisionService.Tests.Infrastructure.ExternalServices;

// T-10-1693, T-10-1694, T-10-1696, T-10-1698, NFR, FR-02, FR-04, FR-07, FR-13, FR-15, IADR-0446, #1061 (#753):
// 取引判断が gRPC で読む段 4 の 3 つの読み取り（日報の方針・監視銘柄・当時の監視銘柄）の**原則 A**・**契約**・**timeout / retry**。
// 🔴 実 Kestrel の h2c（127.0.0.1）で本当に往復させる（Stage4ReadStubHost）。輸送は本番と同じ拡張メソッドから組む。
public class GrpcStage4ReadsTests
{
    private static ServiceProvider Compose(string address, Dictionary<string, string?>? extra = null)
    {
        var values = new Dictionary<string, string?> { ["Reports:Grpc"] = address, ["MarketMonitor:Grpc"] = address };
        foreach (var (k, v) in extra ?? [])
            values[k] = v;
        var config = new ConfigurationBuilder().AddInMemoryCollection(values).Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddAiStockTradingReportsGrpc(config);
        services.AddAiStockTradingMarketMonitorGrpc(config);
        return services.BuildServiceProvider();
    }

    private static GrpcDailyPolicyProvider Policy(ServiceProvider sp) =>
        new(sp.GetRequiredService<ReportsGrpcTransport>(), NullLogger<GrpcDailyPolicyProvider>.Instance);

    private static GrpcWatchlistProvider Watchlist(ServiceProvider sp, IWatchlistProvider? fallback = null) =>
        new(sp.GetRequiredService<MarketMonitorGrpcTransport>(), fallback ?? new FixedFallback(),
            NullLogger<GrpcWatchlistProvider>.Instance);

    private static GrpcAsOfWatchlistSource AsOf(ServiceProvider sp) =>
        new(sp.GetRequiredService<MarketMonitorGrpcTransport>(), NullLogger<GrpcAsOfWatchlistSource>.Instance);

    private sealed class FixedFallback : IWatchlistProvider
    {
        internal static readonly IReadOnlyList<WatchedSymbol> List = [new("CONFIG", Market.Japan)];

        public Task<IReadOnlyList<WatchedSymbol>> GetWatchlistAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(List);

        public Task<IReadOnlyList<WatchedSymbol>?> GetAuthoritativeWatchlistAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<WatchedSymbol>?>(List);
    }

    private static MonitorProto.WatchlistItem Item(string? symbol, MonitorProto.Market market)
    {
        var item = new MonitorProto.WatchlistItem { Market = market };
        if (symbol is not null)
            item.Symbol = symbol;
        return item;
    }

    private static MonitorProto.GetWatchlistResponse Items(params MonitorProto.WatchlistItem[] items)
    {
        var r = new MonitorProto.GetWatchlistResponse();
        r.Items.AddRange(items);
        return r;
    }

    // ---- T-10-1693: 日報の方針（倒す向きは「取引しない」＝ null） ----

    [Theory]
    [InlineData("未確定（方針の無い応答）")]
    [InlineData("日付なし")]
    [InlineData("要約なし")]
    [InlineData("読めない日付")]
    [InlineData("照会の失敗")]
    public async Task T_10_1693_未確定と欠けた方針と失敗は取引しない(string how)
    {
        var policy = new ReportProto.DailyPolicyRecord { Date = "2026-09-10", Summary = "押し目買い" };
        switch (how)
        {
            case "日付なし": policy.ClearDate(); break;
            case "要約なし": policy.ClearSummary(); break;
            case "読めない日付": policy.Date = "2026/09/10"; break;
        }

        var behavior = new Stage4ReadStubBehavior
        {
            Policy = how switch
            {
                "未確定（方針の無い応答）" => Stage4ReadStubBehavior.Returns(new ReportProto.GetConfirmedDailyPolicyResponse()),
                "照会の失敗" => Stage4ReadStubBehavior.Fails<ReportProto.GetConfirmedDailyPolicyResponse>(StatusCode.Unavailable),
                _ => Stage4ReadStubBehavior.Returns(new ReportProto.GetConfirmedDailyPolicyResponse { Policy = policy }),
            },
        };
        await using var host = await Stage4ReadStubHost.StartAsync(behavior);
        await using var sp = Compose(host.Address);

        (await Policy(sp).GetCurrentAsync()).Should().BeNull(how);
        behavior.PolicyCalls.Should().Be(1);
    }

    [Fact]
    public async Task T_10_1693_確定済みの方針は日付と要約をそのまま読む()
    {
        await using var host = await Stage4ReadStubHost.StartAsync(new Stage4ReadStubBehavior
        {
            Policy = Stage4ReadStubBehavior.Returns(new ReportProto.GetConfirmedDailyPolicyResponse
            {
                Policy = new ReportProto.DailyPolicyRecord { Date = "2026-09-10", Summary = "" },
            }),
        });
        await using var sp = Compose(host.Address);

        (await Policy(sp).GetCurrentAsync()).Should().Be(new DailyPolicy(new DateOnly(2026, 9, 10), ""), "在る空文字は空文字");
    }

    // ---- T-10-1694: 監視銘柄（定時サイクルは寛容・プロンプトは厳格・当時は再構成の可否つき。解釈は REST と同じ） ----

    [Fact]
    public async Task T_10_1694_定時サイクルは_REST_と同じ寛容な読みで_プロンプトは欠けた行があれば不明()
    {
        await using var host = await Stage4ReadStubHost.StartAsync(new Stage4ReadStubBehavior
        {
            Watchlist = Stage4ReadStubBehavior.Returns(Items(
                Item("AAPL", MonitorProto.Market.UnitedStates),
                Item("7203", MonitorProto.Market.Japan),
                Item(null, MonitorProto.Market.UnitedStates),
                Item("MSFT", MonitorProto.Market.Unspecified))),
        });
        await using var sp = Compose(host.Address);
        var provider = Watchlist(sp);

        // REST の定時サイクルの読み（HttpWatchlistProvider.ToCycleWatchlist）: 銘柄の無い行も、市場が未指定の行も落とす。
        // ［2026-09-27 改訂 / #1063 A］以前は市場が未指定の MSFT を日本（列挙の既定値）として読んでいた。
        (await provider.GetWatchlistAsync()).Should().Equal(
            new WatchedSymbol("AAPL", Market.UnitedStates),
            new WatchedSymbol("7203", Market.Japan));
        // 🔴 プロンプトの読み: 1 行でも欠けていれば一覧ごと不明（既定値で読むと別の市場の銘柄に化ける。#1041 監査 F1）。
        (await provider.GetAuthoritativeWatchlistAsync()).Should().BeNull();
    }

    // T-10-1711, #1063 C: 市場の写しを**単独で**確かめる。上の試験は銘柄の無い行も混ぜているので、未指定の市場を日本と読む写しの誤りが
    // 銘柄の無い行の検査で先に不明へ倒れて隠れる（PR #1062 の監査で変異が生き残った）。欠けているのが市場だけの応答で、
    // プロンプトの口は一覧ごと不明・定時サイクルの口はその行だけを落とすことを、それぞれ別に表明する。
    [Theory]
    [InlineData(MonitorProto.Market.Unspecified)]
    [InlineData((MonitorProto.Market)9)]
    public async Task T_10_1711_市場だけが欠けた行はプロンプトでは一覧ごと不明_定時サイクルではその行だけを落とす(MonitorProto.Market market)
    {
        await using var host = await Stage4ReadStubHost.StartAsync(new Stage4ReadStubBehavior
        {
            Watchlist = Stage4ReadStubBehavior.Returns(Items(
                Item("AAPL", MonitorProto.Market.UnitedStates),
                Item("MSFT", market))),
        });
        await using var sp = Compose(host.Address);
        var provider = Watchlist(sp);

        (await provider.GetAuthoritativeWatchlistAsync()).Should().BeNull("市場の欠けた行を日本として読まない");
        (await provider.GetWatchlistAsync()).Should().Equal(new WatchedSymbol("AAPL", Market.UnitedStates));
    }

    // T-10-1711 の対: 欠けているのが銘柄だけの応答（市場は正しい）でも、プロンプトの口は一覧ごと不明。
    [Fact]
    public async Task T_10_1711_銘柄だけが欠けた行はプロンプトでは一覧ごと不明_定時サイクルではその行だけを落とす()
    {
        await using var host = await Stage4ReadStubHost.StartAsync(new Stage4ReadStubBehavior
        {
            Watchlist = Stage4ReadStubBehavior.Returns(Items(
                Item("AAPL", MonitorProto.Market.UnitedStates),
                Item(null, MonitorProto.Market.Japan))),
        });
        await using var sp = Compose(host.Address);
        var provider = Watchlist(sp);

        (await provider.GetAuthoritativeWatchlistAsync()).Should().BeNull();
        (await provider.GetWatchlistAsync()).Should().Equal(new WatchedSymbol("AAPL", Market.UnitedStates));
    }

    [Fact]
    public async Task T_10_1694_読めなければ定時サイクルは構成の監視銘柄_プロンプトは不明()
    {
        await using var host = await Stage4ReadStubHost.StartAsync(new Stage4ReadStubBehavior
        {
            Watchlist = Stage4ReadStubBehavior.Fails<MonitorProto.GetWatchlistResponse>(StatusCode.PermissionDenied),
        });
        await using var sp = Compose(host.Address);
        var provider = Watchlist(sp);

        (await provider.GetWatchlistAsync()).Should().Equal(FixedFallback.List);
        (await provider.GetAuthoritativeWatchlistAsync()).Should().BeNull();
    }

    [Fact]
    public async Task T_10_1694_空の一覧は空_不明ではない()
    {
        await using var host = await Stage4ReadStubHost.StartAsync(new Stage4ReadStubBehavior());
        await using var sp = Compose(host.Address);

        (await Watchlist(sp).GetAuthoritativeWatchlistAsync()).Should().BeEmpty();
    }

    [Theory]
    [InlineData("可否の欠落")]
    [InlineData("再構成できたのに一覧が無い")]
    [InlineData("市場が未指定の行")]
    [InlineData("銘柄の無い行")]
    [InlineData("照会の失敗")]
    public async Task T_10_1694_当時の監視銘柄の欠落と失敗は再構成できない(string how)
    {
        var response = new MonitorProto.GetWatchlistAsOfResponse
        {
            Reconstructed = true,
            Symbols = new MonitorProto.WatchlistItems { Items = { Item("AAPL", MonitorProto.Market.UnitedStates) } },
        };
        switch (how)
        {
            case "可否の欠落": response.ClearReconstructed(); break;
            case "再構成できたのに一覧が無い": response.Symbols = null; break;
            case "市場が未指定の行": response.Symbols.Items.Add(Item("X", MonitorProto.Market.Unspecified)); break;
            case "銘柄の無い行": response.Symbols.Items.Add(Item(null, MonitorProto.Market.Japan)); break;
        }

        await using var host = await Stage4ReadStubHost.StartAsync(new Stage4ReadStubBehavior
        {
            AsOf = how == "照会の失敗"
                ? Stage4ReadStubBehavior.Fails<MonitorProto.GetWatchlistAsOfResponse>(StatusCode.Internal)
                : Stage4ReadStubBehavior.Returns(response),
        });
        await using var sp = Compose(host.Address);

        var result = await AsOf(sp).GetWatchlistAtAsync(DateTimeOffset.UtcNow);

        result.Symbols.Should().BeNull(how);
        result.Reason.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task T_10_1694_当時の監視銘柄は_REST_と同じ書式の時刻で引き_再構成できない理由はそのまま運ぶ()
    {
        await using var host = await Stage4ReadStubHost.StartAsync(new Stage4ReadStubBehavior
        {
            AsOf = Stage4ReadStubBehavior.Returns(new MonitorProto.GetWatchlistAsOfResponse { Reconstructed = false, Reason = "seed より前" }),
        });
        await using var sp = Compose(host.Address);
        var at = new DateTimeOffset(2026, 9, 10, 9, 30, 0, TimeSpan.FromHours(9));

        var result = await AsOf(sp).GetWatchlistAtAsync(at);

        result.Symbols.Should().BeNull();
        result.Reason.Should().Be("seed より前");
        host.Behavior.LastAsOfAt.Should().Be(HttpAsOfWatchlistSource.WireInstant(at)).And.Be("2026-09-10T00:30:00.0000000Z");
    }

    // ---- T-10-1696: 契約（送り手の本物の型 → 提供側の写し → 実 h2c → 受け手） ----

    [Fact]
    public async Task T_10_1696_送り手の型の方針と監視銘柄と当時の一覧を受け手が同じ値で読む()
    {
        var senderPolicy = new ConfirmedDailyPolicy(new DateOnly(2026, 9, 10), "押し目買いを優先する", 3);
        MonitoredSymbol[] senderRows = [new("7203", Market.Japan), new("AAPL", Market.UnitedStates)];
        var senderAsOf = new WatchlistAsOfResponse(true, senderRows, "after-change", DateTimeOffset.UtcNow, null, null);
        var watchlist = new MonitorProto.GetWatchlistResponse();
        watchlist.Items.AddRange(senderRows.Select(WatchlistWireMapping.ToProto));

        await using var host = await Stage4ReadStubHost.StartAsync(new Stage4ReadStubBehavior
        {
            Policy = Stage4ReadStubBehavior.Returns(
                new ReportProto.GetConfirmedDailyPolicyResponse { Policy = DailyPolicyWireMapping.ToProto(senderPolicy) }),
            Watchlist = Stage4ReadStubBehavior.Returns(watchlist),
            AsOf = Stage4ReadStubBehavior.Returns(WatchlistWireMapping.ToProto(senderAsOf)),
        });
        await using var sp = Compose(host.Address);

        (await Policy(sp).GetCurrentAsync()).Should().Be(new DailyPolicy(senderPolicy.Date, senderPolicy.Summary));
        var expected = senderRows.Select(r => new WatchedSymbol(r.Symbol, r.Market)).ToArray();
        (await Watchlist(sp).GetAuthoritativeWatchlistAsync()).Should().Equal(expected, "日本（C# の 0）が線上で未指定に化けない");
        (await Watchlist(sp).GetWatchlistAsync()).Should().Equal(expected);
        var asOf = await AsOf(sp).GetWatchlistAtAsync(DateTimeOffset.UtcNow);
        asOf.Symbols.Should().NotBeNull(asOf.Reason);
        asOf.Symbols.Should().Equal(expected);
    }

    // ---- T-10-1698: timeout / retry（既定の deadline は REST の HttpClient と同じ 5 秒） ----

    [Fact]
    public async Task T_10_1698_提供側が黙れば構成した_deadline_で取引しない側へ倒れ再試行しない()
    {
        var ok = new ReportProto.GetConfirmedDailyPolicyResponse
        {
            Policy = new ReportProto.DailyPolicyRecord { Date = "2026-09-10", Summary = "s" },
        };
        await using var host = await Stage4ReadStubHost.StartAsync(new Stage4ReadStubBehavior
        {
            Policy = Stage4ReadStubBehavior.RespondsOnceThenHangs(ok),
        });
        await using var sp = Compose(host.Address, new() { ["Reports:GrpcTimeoutSeconds"] = "2" });
        var provider = Policy(sp);
        (await provider.GetCurrentAsync()).Should().NotBeNull("暖機");

        var elapsed = System.Diagnostics.Stopwatch.StartNew();
        var policy = await provider.GetCurrentAsync();
        elapsed.Stop();

        policy.Should().BeNull();
        host.Behavior.PolicyCalls.Should().Be(2, "既定は再試行しない");
        elapsed.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(10), "構成した 2 秒の deadline で打ち切られること");
    }

    [Fact]
    public async Task T_10_1698_一時的な_UNAVAILABLE_は構成した回数だけ再試行し恒久的な失敗は再試行しない()
    {
        var ok = Items(Item("AAPL", MonitorProto.Market.UnitedStates));
        await using var transient = await Stage4ReadStubHost.StartAsync(new Stage4ReadStubBehavior
        {
            Watchlist = Stage4ReadStubBehavior.FailsThenSucceeds(StatusCode.Unavailable, 1, ok),
        });
        await using var permanent = await Stage4ReadStubHost.StartAsync(new Stage4ReadStubBehavior
        {
            Watchlist = Stage4ReadStubBehavior.Fails<MonitorProto.GetWatchlistResponse>(StatusCode.Unauthenticated),
        });
        await using var sp1 = Compose(transient.Address, new() { ["MarketMonitor:GrpcMaxAttempts"] = "2" });
        await using var sp2 = Compose(permanent.Address, new() { ["MarketMonitor:GrpcMaxAttempts"] = "3" });

        (await Watchlist(sp1).GetAuthoritativeWatchlistAsync()).Should().ContainSingle();
        (await Watchlist(sp2).GetAuthoritativeWatchlistAsync()).Should().BeNull();
        transient.Behavior.WatchlistCalls.Should().Be(2);
        permanent.Behavior.WatchlistCalls.Should().Be(1);
    }
}

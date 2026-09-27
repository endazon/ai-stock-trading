extern alias CostControlWorker;
extern alias MarketMonitorWorker;

using AiStockTrading.Shared.Contracts.Trading;
using AwesomeAssertions;
using Grpc.Core;
using InformationCollectionService.Features.InformationCollection;
using InformationCollectionService.Infrastructure.ExternalServices;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using CostControlDecision = CostControlWorker::CostControlService.Domain.CostControlDecision;
using CostControlState = CostControlWorker::CostControlService.Domain.CostControlState;
using CostProto = AiStockTrading.Shared.Grpc.CostControl.V1;
using CostStateWireMapping = CostControlWorker::CostControlService.Features.CostControl.CostStateWireMapping;
using MonitoredSymbol = MarketMonitorWorker::MarketMonitorService.Domain.MonitoredSymbol;
using MonitorProto = AiStockTrading.Shared.Grpc.MarketMonitor.V1;
using WatchlistWireMapping = MarketMonitorWorker::MarketMonitorService.Features.MarketMonitor.WatchlistWireMapping;

namespace InformationCollectionService.Tests;

// T-10-1695, T-10-1696, T-10-1698, NFR（費用）, FR-01, FR-13, IADR-0031, IADR-0435, IADR-0446, #1061 (#753):
// 情報収集が gRPC で読む段 4 の 2 つの読み取り（監視銘柄・費用統制の判定）の**原則 A**・**契約**・**timeout / retry**。
// 🔴 実 Kestrel の h2c（127.0.0.1）で本当に往復させる（Stage4ReadStubHost）。輸送は本番と同じ拡張メソッドから組む。
public class GrpcStage4ReadsTests
{
    private static ServiceProvider Compose(string address, Dictionary<string, string?>? extra = null)
    {
        var values = new Dictionary<string, string?> { ["MarketMonitor:Grpc"] = address, ["CostControl:Grpc"] = address };
        foreach (var (k, v) in extra ?? [])
            values[k] = v;
        var config = new ConfigurationBuilder().AddInMemoryCollection(values).Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddAiStockTradingMarketMonitorGrpc(config);
        services.AddAiStockTradingCostControlGrpc(config);
        return services.BuildServiceProvider();
    }

    private static GrpcMarketMonitorWatchlistReader Reader(ServiceProvider sp) =>
        new(sp.GetRequiredService<MarketMonitorGrpcTransport>(), NullLogger<GrpcMarketMonitorWatchlistReader>.Instance);

    private static GrpcCostControlGate Gate(ServiceProvider sp) =>
        new(sp.GetRequiredService<CostControlGrpcTransport>(), NullLogger<GrpcCostControlGate>.Instance);

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

    private static async Task<IReadOnlyList<WatchedSymbol>?> ReadWatchlistAsync(params MonitorProto.WatchlistItem[] items)
    {
        await using var host = await Stage4ReadStubHost.StartAsync(
            new Stage4ReadStubBehavior { Watchlist = Stage4ReadStubBehavior.Returns(Items(items)) });
        await using var sp = Compose(host.Address);
        return await Reader(sp).ReadAsync();
    }

    private static async Task<CostControlGate> ReadGateAsync(CostProto.GetCostStateResponse response)
    {
        await using var host = await Stage4ReadStubHost.StartAsync(
            new Stage4ReadStubBehavior { Cost = Stage4ReadStubBehavior.Returns(response) });
        await using var sp = Compose(host.Address);
        return await Gate(sp).GetAsync();
    }

    // ---- T-10-1695: 監視銘柄（欠けた行が 1 つでもあれば一覧ごと不明・空は空。解釈は REST と同じ） ----

    [Theory]
    [InlineData("銘柄なし")]
    [InlineData("空白の銘柄")]
    [InlineData("市場が未指定")]
    [InlineData("未知の市場の番号")]
    public async Task T_10_1695_監視銘柄に欠けた行があれば一覧ごと不明(string how)
    {
        var broken = how switch
        {
            "銘柄なし" => Item(null, MonitorProto.Market.UnitedStates),
            "空白の銘柄" => Item("  ", MonitorProto.Market.UnitedStates),
            "市場が未指定" => Item("MSFT", MonitorProto.Market.Unspecified),
            _ => Item("MSFT", (MonitorProto.Market)9),
        };

        (await ReadWatchlistAsync(Item("AAPL", MonitorProto.Market.UnitedStates), broken)).Should().BeNull(how);
    }

    [Fact]
    public async Task T_10_1695_監視銘柄の空は空_読めた銘柄は前後の空白を削る()
    {
        (await ReadWatchlistAsync()).Should().BeEmpty("空の一覧は「0 件」であり不明ではない");
        (await ReadWatchlistAsync(Item(" 7203 ", MonitorProto.Market.Japan))).Should().Equal(new WatchedSymbol("7203", Market.Japan));
    }

    [Fact]
    public async Task T_10_1695_監視銘柄の照会に失敗すれば不明()
    {
        await using var host = await Stage4ReadStubHost.StartAsync(new Stage4ReadStubBehavior
        {
            Watchlist = Stage4ReadStubBehavior.Fails<MonitorProto.GetWatchlistResponse>(StatusCode.Unavailable),
        });
        await using var sp = Compose(host.Address);

        (await Reader(sp).ReadAsync()).Should().BeNull();
    }

    // ---- T-10-1695: 費用統制の判定（REST と同じ写し。#915 の是正） ----

    public static TheoryData<string, bool?, string?, bool, decimal> GateCases => new()
    {
        // how, isHalted（null＝欠落）, 倍率（null＝欠落）, 期待の停止, 期待の倍率
        { "停止の欠落は Normal", null, "2", false, 1m },
        { "停止は倍率を見ずに尊重", true, null, true, 0m },
        { "停止中の倍率 0 は値", true, "0", true, 0m },
        { "倍率の欠落は 1×", false, null, false, 1m },
        { "倍率の空は 1×", false, "", false, 1m },
        { "倍率の非正は 1×", false, "0", false, 1m },
        { "間隔延長は 2×", false, "2", false, 2m },
        { "読めない倍率は Normal", false, "two", false, 1m },
        { "桁あふれの倍率は Normal", false, "79228162514264337593543950336", false, 1m },
        // T-10-1712, #1063 B: 停止の旗が読めていれば、倍率が読めなくても停止を守る（以前は応答全体を Normal へ倒していた）。
        { "停止中の読めない倍率でも停止を守る", true, "two", true, 0m },
        { "停止中の桁あふれの倍率でも停止を守る", true, "79228162514264337593543950336", true, 0m },
    };

    [Theory]
    [MemberData(nameof(GateCases))]
    public async Task T_10_1695_費用統制の判定は_REST_と同じ写しで読む(
        string how, bool? isHalted, string? multiplier, bool expectedHalted, decimal expectedMultiplier)
    {
        var response = new CostProto.GetCostStateResponse();
        if (isHalted is { } h)
            response.IsHalted = h;
        if (multiplier is not null)
            response.IntervalMultiplier = multiplier;

        (await ReadGateAsync(response)).Should().Be(new CostControlGate(expectedHalted, expectedMultiplier), how);
    }

    [Fact]
    public async Task T_10_1695_費用統制の照会に失敗すれば_Normal()
    {
        await using var host = await Stage4ReadStubHost.StartAsync(new Stage4ReadStubBehavior
        {
            Cost = Stage4ReadStubBehavior.Fails<CostProto.GetCostStateResponse>(StatusCode.PermissionDenied),
        });
        await using var sp = Compose(host.Address);

        (await Gate(sp).GetAsync()).Should().Be(CostControlGate.Normal);
    }

    // ---- T-10-1696: 契約（送り手の本物の型 → 提供側の写し → 実 h2c → 受け手） ----

    [Theory]
    [InlineData(CostControlState.Normal, 1, false, 1)]
    [InlineData(CostControlState.Throttled, 2, false, 2)]
    [InlineData(CostControlState.Halted, 0, true, 0)]
    public async Task T_10_1696_送り手の型の判定を受け手が同じ値で読む(
        CostControlState state, int multiplier, bool expectedHalted, int expectedMultiplier)
    {
        var gate = await ReadGateAsync(CostStateWireMapping.ToProto(new CostControlDecision(state, multiplier)));

        gate.Should().Be(new CostControlGate(expectedHalted, expectedMultiplier));
    }

    [Fact]
    public async Task T_10_1696_送り手の型の監視銘柄を受け手が同じ値で読む()
    {
        MonitoredSymbol[] sender = [new("7203", Market.Japan), new("AAPL", Market.UnitedStates)];

        var read = await ReadWatchlistAsync([.. sender.Select(WatchlistWireMapping.ToProto)]);

        read.Should().Equal(sender.Select(s => new WatchedSymbol(s.Symbol, s.Market)), "日本（C# の 0）が線上で未指定に化けない");
    }

    // ---- T-10-1698: timeout / retry（既定の deadline は REST の HttpClient と同じ 5 秒） ----

    [Fact]
    public async Task T_10_1698_提供側が黙れば構成した_deadline_で_Normal_へ倒れ再試行しない()
    {
        await using var host = await Stage4ReadStubHost.StartAsync(new Stage4ReadStubBehavior
        {
            Cost = Stage4ReadStubBehavior.RespondsOnceThenHangs(new CostProto.GetCostStateResponse { IsHalted = true, IntervalMultiplier = "0" }),
        });
        await using var sp = Compose(host.Address, new() { ["CostControl:GrpcTimeoutSeconds"] = "2" });
        var gate = Gate(sp);
        (await gate.GetAsync()).Halted.Should().BeTrue("暖機");

        var elapsed = System.Diagnostics.Stopwatch.StartNew();
        var second = await gate.GetAsync();
        elapsed.Stop();

        second.Should().Be(CostControlGate.Normal);
        host.Behavior.CostCalls.Should().Be(2, "既定は再試行しない");
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

        (await Reader(sp1).ReadAsync()).Should().ContainSingle();
        (await Reader(sp2).ReadAsync()).Should().BeNull();
        transient.Behavior.WatchlistCalls.Should().Be(2);
        permanent.Behavior.WatchlistCalls.Should().Be(1);
    }
}

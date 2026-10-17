extern alias RiskManagementWorker;

using AiStockTrading.Shared.Contracts.Trading;
using AwesomeAssertions;
using Grpc.Core;
using InformationCollectionService.Features.InformationCollection;
using InformationCollectionService.Infrastructure.ExternalServices;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using OpenPositionView = RiskManagementWorker::RiskManagementService.Features.RiskManagement.GetOpenPositions.OpenPositionView;
using RiskProto = AiStockTrading.Shared.Grpc.RiskManagement.V1;
using RiskReadWireMapping = RiskManagementWorker::RiskManagementService.Features.RiskManagement.RiskReadWireMapping;

namespace InformationCollectionService.Tests;

// T-10-2763, NFR, FR-01, FR-02, IADR-0547 決定 1, IADR-0557 決定 2・4, #1388 (#753):
// 情報収集が gRPC（`RiskControlsRead/GetOpenPositions`）で読む保有銘柄の**原則 A**・**契約**・**timeout / retry**。
// 🔴 解釈は REST（HttpRiskManagementHeldSymbolsProvider.Interpret）と同じ 1 つ。実 Kestrel の h2c（127.0.0.1）で本当に往復させる。
public class GrpcHeldSymbolsTests
{
    private static ServiceProvider Compose(string address, Dictionary<string, string?>? extra = null)
    {
        var values = new Dictionary<string, string?> { ["RiskManagement:Grpc"] = address };
        foreach (var (k, v) in extra ?? [])
            values[k] = v;
        var config = new ConfigurationBuilder().AddInMemoryCollection(values).Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddAiStockTradingRiskManagementGrpc(config);
        return services.BuildServiceProvider();
    }

    private static GrpcRiskManagementHeldSymbolsProvider Provider(ServiceProvider sp) =>
        new(sp.GetRequiredService<RiskManagementGrpcTransport>(), NullLogger<GrpcRiskManagementHeldSymbolsProvider>.Instance);

    private static RiskProto.OpenPositionRow Row(
        string? symbol, RiskProto.Market market, RiskProto.TradeSide side, int? quantity)
    {
        var row = new RiskProto.OpenPositionRow { Market = market, Side = side };
        if (symbol is not null)
            row.Symbol = symbol;
        if (quantity is { } q)
            row.Quantity = q;
        return row;
    }

    private static RiskProto.GetOpenPositionsResponse Positions(params RiskProto.OpenPositionRow[] rows)
    {
        var r = new RiskProto.GetOpenPositionsResponse();
        r.Positions.AddRange(rows);
        return r;
    }

    private static async Task<IReadOnlyList<WatchedSymbol>?> ReadAsync(
        Func<int, CancellationToken, Task<RiskProto.GetOpenPositionsResponse>> behavior)
    {
        await using var host = await Stage4ReadStubHost.StartAsync(new Stage4ReadStubBehavior { OpenPositions = behavior });
        await using var sp = Compose(host.Address);
        return await Provider(sp).GetHeldSymbolsAsync();
    }

    // 契約: 送り手の本物の型（OpenPositionView）を送り手の写し（RiskReadWireMapping）で線上へ出したものを、REST と同じ値で読む。
    [Fact]
    public async Task T_10_2763_送り手の型の保有を受け手が_REST_と同じ値で読む()
    {
        OpenPositionView[] views =
        [
            new("AAPL", Market.UnitedStates, TradeSide.Buy, 10, 180m, 170m),
            new("7203", Market.Japan, TradeSide.Buy, 100, 2500m, 2400m),
            new("GOOGL", Market.UnitedStates, TradeSide.Sell, 5, 150m, 160m),
        ];

        var held = await ReadAsync(Stage4ReadStubBehavior.Returns(Positions([.. views.Select(RiskReadWireMapping.ToProto)])));

        held.Should().Equal(
            new WatchedSymbol("AAPL", Market.UnitedStates),
            new WatchedSymbol("7203", Market.Japan),
            new WatchedSymbol("GOOGL", Market.UnitedStates));
    }

    // 空は「保有なし」。符号付きの合計が 0 の銘柄は数えない（REST と同じ）。
    [Fact]
    public async Task T_10_2763_空は保有なし_相殺した銘柄は数えない()
    {
        (await ReadAsync(Stage4ReadStubBehavior.Returns(Positions()))).Should().NotBeNull().And.BeEmpty();

        var held = await ReadAsync(Stage4ReadStubBehavior.Returns(Positions(
            Row("AAPL", RiskProto.Market.UnitedStates, RiskProto.TradeSide.Buy, 10),
            Row("AAPL", RiskProto.Market.UnitedStates, RiskProto.TradeSide.Sell, 10),
            Row("MSFT", RiskProto.Market.UnitedStates, RiskProto.TradeSide.Buy, 3))));

        held.Should().Equal(new WatchedSymbol("MSFT", Market.UnitedStates));
    }

    // 🔴 原則 A: 欠けた行（銘柄・数量の欠落、市場・方向の未指定）・数量が正でない行が 1 つでもあれば一覧ごと不明。
    // 🔴 線上の 0（未指定）を C# の 0（日本・買い）と読まない。
    [Theory]
    [InlineData("銘柄なし")]
    [InlineData("市場の未指定")]
    [InlineData("方向の未指定")]
    [InlineData("数量なし")]
    [InlineData("数量 0")]
    public async Task T_10_2763_欠けた行が1つでもあれば一覧ごと不明(string how)
    {
        var broken = how switch
        {
            "銘柄なし" => Row(null, RiskProto.Market.UnitedStates, RiskProto.TradeSide.Buy, 3),
            "市場の未指定" => Row("MSFT", RiskProto.Market.Unspecified, RiskProto.TradeSide.Buy, 3),
            "方向の未指定" => Row("MSFT", RiskProto.Market.UnitedStates, RiskProto.TradeSide.Unspecified, 3),
            "数量なし" => Row("MSFT", RiskProto.Market.UnitedStates, RiskProto.TradeSide.Buy, null),
            "数量 0" => Row("MSFT", RiskProto.Market.UnitedStates, RiskProto.TradeSide.Buy, 0),
            _ => throw new ArgumentOutOfRangeException(nameof(how)),
        };

        var held = await ReadAsync(Stage4ReadStubBehavior.Returns(Positions(
            Row("AAPL", RiskProto.Market.UnitedStates, RiskProto.TradeSide.Buy, 10), broken)));

        held.Should().BeNull();
    }

    // 🔴 照会の失敗は不明（空の一覧へ倒さない）。
    [Theory]
    [InlineData(StatusCode.Unauthenticated)]
    [InlineData(StatusCode.PermissionDenied)]
    [InlineData(StatusCode.Unavailable)]
    [InlineData(StatusCode.Internal)]
    public async Task T_10_2763_照会に失敗すれば不明(StatusCode status)
    {
        var held = await ReadAsync(Stage4ReadStubBehavior.Fails<RiskProto.GetOpenPositionsResponse>(status));

        held.Should().BeNull();
    }

    // 構成した deadline で打ち切って不明へ倒し、既定では再試行しない（REST の "risk" の 5 秒と同じ規則・秒数だけ縮める）。
    [Fact]
    public async Task T_10_2763_提供側が黙れば構成した_deadline_で不明へ倒れ再試行しない()
    {
        var behavior = new Stage4ReadStubBehavior
        {
            OpenPositions = Stage4ReadStubBehavior.RespondsOnceThenHangs(
                Positions(Row("AAPL", RiskProto.Market.UnitedStates, RiskProto.TradeSide.Buy, 1))),
        };
        await using var host = await Stage4ReadStubHost.StartAsync(behavior);
        await using var sp = Compose(host.Address, new() { ["RiskManagement:GrpcTimeoutSeconds"] = "1" });
        var provider = Provider(sp);
        (await provider.GetHeldSymbolsAsync()).Should().NotBeNull("暖機（1 回目は応答する）");

        var started = System.Diagnostics.Stopwatch.StartNew();
        var held = await provider.GetHeldSymbolsAsync();
        started.Stop();

        held.Should().BeNull();
        started.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(4), "構成した 1 秒で打ち切る（既定の 5 秒を待たない）");
        behavior.OpenPositionsCalls.Should().Be(2, "既定は再試行しない");
    }

    [Fact]
    public async Task T_10_2763_一時的な_UNAVAILABLE_は構成した回数だけ再試行する()
    {
        var behavior = new Stage4ReadStubBehavior
        {
            OpenPositions = Stage4ReadStubBehavior.FailsThenSucceeds(
                StatusCode.Unavailable, 1, Positions(Row("AAPL", RiskProto.Market.UnitedStates, RiskProto.TradeSide.Buy, 1))),
        };
        await using var host = await Stage4ReadStubHost.StartAsync(behavior);
        await using var sp = Compose(host.Address, new() { ["RiskManagement:GrpcMaxAttempts"] = "2" });

        (await Provider(sp).GetHeldSymbolsAsync()).Should().Equal(new WatchedSymbol("AAPL", Market.UnitedStates));
        behavior.OpenPositionsCalls.Should().Be(2);
    }
}

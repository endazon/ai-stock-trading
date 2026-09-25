extern alias RiskManagementWorker;

using AiStockTrading.Shared.Contracts.Observability;
using AiStockTrading.Shared.Contracts.Trading;
using AiStockTrading.TestSupport.Metrics;
using AwesomeAssertions;
using Grpc.Core;
using MarketMonitorService.Domain;
using MarketMonitorService.Features.MarketMonitor;
using MarketMonitorService.Infrastructure.ExternalServices;
using MarketMonitorService.Infrastructure.Persistence;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RiskManagementWorker::RiskManagementService.Features.RiskManagement.GetOpenPositions;
using Wolverine;
using Xunit;
using Proto = AiStockTrading.Shared.Grpc.RiskManagement.V1;
using RiskReadWireMapping = RiskManagementWorker::RiskManagementService.Features.RiskManagement.RiskReadWireMapping;
using RiskTradingDefaults = RiskManagementWorker::RiskManagementService.Domain.TradingDefaults;

namespace MarketMonitorService.Tests;

// T-10-1052, T-10-1056, T-10-1057, T-10-1058, NFR, FR-03, FR-10, MSP:ADR-0029, IADR-0284 決定 5（段 2）, IADR-0399, IADR-0427,
// #997 (#753): 損切り検知が gRPC で読む保有ポジション（`RiskControlsRead/GetOpenPositions`）。
//
// 🔴 **実 Kestrel の h2c で本当に往復させる**（RiskReadStubHost）。欠落は proto3 の既定値（0・""・列挙の 0）で線に乗るため、
// 受け手の写しが「無い」を「不明」として #957 の行ごとの扱い（HttpPositionStore.Classify と共有）へ渡していることは、
// 実際に符号化・復号された message でしか確かめられない。
public class RiskManagementGrpcTests
{
    private static Proto.OpenPositionRow Row(string symbol, Action<Proto.OpenPositionRow>? tweak = null)
    {
        var row = new Proto.OpenPositionRow
        {
            Symbol = symbol,
            Market = Proto.Market.UnitedStates,
            Side = Proto.TradeSide.Buy,
            Quantity = 10,
            EntryPrice = "400",
            StopLossPrice = "388",
        };
        tweak?.Invoke(row);
        return row;
    }

    private static (ServiceProvider Sp, RiskManagementGrpcTransport Transport) Compose(
        string address, Dictionary<string, string?>? extra = null)
    {
        var values = new Dictionary<string, string?> { ["RiskManagement:Grpc"] = address };
        foreach (var (k, v) in extra ?? [])
            values[k] = v;

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddAiStockTradingRiskManagementGrpc(new ConfigurationBuilder().AddInMemoryCollection(values).Build());
        var sp = services.BuildServiceProvider();
        return (sp, sp.GetRequiredService<RiskManagementGrpcTransport>());
    }

    private static async Task<(IReadOnlyCollection<HeldPosition> Positions, MeterCapture Capture,
        StopLossLivenessReporterTests.RecordingLogger<GrpcPositionStore> Log)> ReadAsync(
        RiskReadStubBehavior behavior, [System.Runtime.CompilerServices.CallerMemberName] string? caller = null)
    {
        await using var host = await RiskReadStubHost.StartAsync(behavior);
        var (sp, transport) = Compose(host.Address);
        await using (sp)
        {
            var meterName = MeterCapture.NewIsolatedMeterName(caller);
            var capture = new MeterCapture(meterName);
            using var metrics = BusinessMetrics.WithMeterName(meterName);
            var log = new StopLossLivenessReporterTests.RecordingLogger<GrpcPositionStore>();
            var positions = await new GrpcPositionStore(transport, metrics, log).GetOpenPositionsAsync();
            return (positions, capture, log);
        }
    }

    // ---- T-10-1052: 原則 A（行ごとの扱いは REST と同じ） ----

    // 🔴 識別できない行（銘柄なし・市場／方向が未指定・数量なし）は評価に渡さず、**他の行はそのまま返る**。
    // 列挙の 0 を日本・買いと読むと、別の銘柄の建玉として評価してしまう。
    [Theory]
    [InlineData("銘柄なし")]
    [InlineData("市場が未指定")]
    [InlineData("方向が未指定")]
    [InlineData("数量なし")]
    public async Task T_10_1052_識別できない行は評価に渡さず他の行はそのまま返りCriticalと計器で出す(string how)
    {
        var broken = Row("MSFT", r =>
        {
            switch (how)
            {
                case "銘柄なし": r.ClearSymbol(); break;
                case "市場が未指定": r.Market = Proto.Market.Unspecified; break;
                case "方向が未指定": r.Side = Proto.TradeSide.Unspecified; break;
                default: r.ClearQuantity(); break;
            }
        });

        var (positions, capture, log) = await ReadAsync(RiskReadStubBehavior.Returns(Row("AAPL"), broken));
        using (capture)
        {
            positions.Should().ContainSingle().Which.Symbol.Should().Be("AAPL");
            capture.TagValuesOf(BusinessMetricNames.MarketMonitorPositionRowsDegraded, BusinessMetricNames.TagReason)
                .Should().Equal("identity-missing");
            log.Entries.Where(e => e.Level == LogLevel.Critical).Should().ContainSingle()
                .Which.Message.Should().Contain("gRPC RiskControlsRead/GetOpenPositions").And.Contain("#1 identity-missing");
        }
    }

    // 🔴 損切りラインの欠落は **0 で評価しない**（ロングは発火せず、含み益のショートは毎巡回発火する）。送り手と同じ近似で評価する。
    [Fact]
    public async Task T_10_1052_損切りラインの欠落は_0_ではなく送り手と同じ近似で評価する()
    {
        var (positions, capture, _) = await ReadAsync(RiskReadStubBehavior.Returns(Row("MSFT", r => r.ClearStopLossPrice())));
        using (capture)
        {
            var msft = positions.Should().ContainSingle().Subject;
            msft.StopLossPrice.Should().Be(400m * (1m - RiskTradingDefaults.DefaultStopLossRatio));
            msft.StopLossApproximated.Should().BeTrue();
        }
    }

    [Fact]
    public async Task T_10_1052_照会できなければ空列_読めない応答は空列のまま_Critical_で出す()
    {
        var (failed, c1, failLog) = await ReadAsync(RiskReadStubBehavior.Fails(StatusCode.Unavailable));
        var (unreadable, c2, unreadableLog) = await ReadAsync(
            RiskReadStubBehavior.Returns(Row("AAPL", r => r.EntryPrice = "not-a-decimal")));
        using (c1)
        using (c2)
        {
            failed.Should().BeEmpty();
            failLog.Entries.Should().NotContain(e => e.Level == LogLevel.Critical, "一過性の障害は Warning（契約の食い違いではない）");

            unreadable.Should().BeEmpty();
            c2.TagValuesOf(BusinessMetricNames.MarketMonitorPositionRowsDegraded, BusinessMetricNames.TagReason)
                .Should().Equal("response-unreadable");
            unreadableLog.Entries.Where(e => e.Level == LogLevel.Critical).Should().ContainSingle()
                .Which.Message.Should().Contain("保有の一覧として読めません");
        }
    }

    // ---- T-10-1056: 契約（送り手の本物の型 → 提供側の写し → 線 → 受け手） ----

    [Fact]
    public async Task T_10_1056_送り手の型の建玉を受け手が同じ値で読む()
    {
        var sender = new[]
        {
            new OpenPositionView("AAPL", Market.UnitedStates, TradeSide.Buy, 3378, 337.63m, 320.75m),
            new OpenPositionView("7203", Market.Japan, TradeSide.Sell, 100, 2500m, 2575m),
        };
        var (positions, capture, log) = await ReadAsync(new RiskReadStubBehavior((_, _) =>
        {
            var r = new Proto.GetOpenPositionsResponse();
            r.Positions.AddRange(sender.Select(RiskReadWireMapping.ToProto));
            return Task.FromResult(r);
        }));
        using (capture)
        {
            positions.Should().BeEquivalentTo(
                [
                    new HeldPosition("AAPL", Market.UnitedStates, TradeSide.Buy, 3378, 337.63m, 320.75m),
                    new HeldPosition("7203", Market.Japan, TradeSide.Sell, 100, 2500m, 2575m),
                ],
                o => o.WithStrictOrdering(),
                "日本・買い（C# の 0）が線上で未指定に化けない");
            log.Entries.Should().NotContain(e => e.Level >= LogLevel.Warning);
        }
    }

    // ---- T-10-1058: timeout / retry（実 h2c） ----

    [Fact]
    public async Task T_10_1058_提供側が黙れば構成した_deadline_で空列へ倒れ再試行しない()
    {
        await using var host = await RiskReadStubHost.StartAsync(RiskReadStubBehavior.RespondsOnceThenHangs(Row("AAPL")));
        var (sp, transport) = Compose(host.Address, new() { ["RiskManagement:GrpcTimeoutSeconds"] = "3" });
        await using (sp)
        {
            var store = new GrpcPositionStore(
                transport, new BusinessMetrics(), sp.GetRequiredService<ILogger<GrpcPositionStore>>());
            (await store.GetOpenPositionsAsync()).Should().ContainSingle("暖機（接続確立を deadline に含めない。#885）");

            var elapsed = System.Diagnostics.Stopwatch.StartNew();
            var positions = await store.GetOpenPositionsAsync();
            elapsed.Stop();

            positions.Should().BeEmpty();
            host.Behavior.Calls.Should().Be(2, "既定は再試行しない");
            elapsed.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(10), "構成した 3 秒の deadline で打ち切られること");
        }
    }

    [Fact]
    public async Task T_10_1058_一時的な_UNAVAILABLE_は構成した回数だけ再試行する()
    {
        await using var retried = await RiskReadStubHost.StartAsync(
            RiskReadStubBehavior.FailsThenSucceeds(StatusCode.Unavailable, 1, Row("AAPL")));
        await using var notRetried = await RiskReadStubHost.StartAsync(
            RiskReadStubBehavior.FailsThenSucceeds(StatusCode.Unavailable, 1, Row("AAPL")));
        var (sp1, t1) = Compose(retried.Address, new() { ["RiskManagement:GrpcMaxAttempts"] = "2" });
        var (sp2, t2) = Compose(notRetried.Address);
        await using (sp1)
        await using (sp2)
        {
            var log = sp1.GetRequiredService<ILogger<GrpcPositionStore>>();
            (await new GrpcPositionStore(t1, new BusinessMetrics(), log).GetOpenPositionsAsync()).Should().ContainSingle();
            (await new GrpcPositionStore(t2, new BusinessMetrics(), log).GetOpenPositionsAsync()).Should().BeEmpty();
            retried.Behavior.Calls.Should().Be(2);
            notRetried.Behavior.Calls.Should().Be(1, "既定は再試行しない（REST と同じ）");
        }
    }

    // ---- T-10-1057: 本番の Program.cs の組み立て ----

    // 🔴 型を見るだけでなく、組み立てた実装で実際に呼ぶ（「配線を外しても全テストが緑」＝#947 の形を塞ぐ）。
    [Fact]
    public async Task T_10_1057_Grpc_を宣言すれば本番の組み立てが_gRPC_実装を選び実際に提供側を呼ぶ()
    {
        await using var host = await RiskReadStubHost.StartAsync(RiskReadStubBehavior.Returns(Row("AAPL")));
        await using var factory = new Factory(grpc: host.Address, riskBaseUrl: "http://risk-rest-must-not-be-used");
        _ = factory.CreateClient();

        using var scope = factory.Services.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<IPositionStore>();

        store.Should().BeOfType<GrpcPositionStore>("宣言があれば BaseUrl より gRPC を優先する");
        (await store.GetOpenPositionsAsync()).Should().ContainSingle().Which.Symbol.Should().Be("AAPL");
        host.Behavior.Calls.Should().Be(1);
    }

    [Fact]
    public async Task T_10_1057_宣言が無ければ_REST_のまま()
    {
        await using var factory = new Factory(grpc: null, riskBaseUrl: "http://risk");
        _ = factory.CreateClient();

        using var scope = factory.Services.CreateScope();
        scope.ServiceProvider.GetService<RiskManagementGrpcTransport>().Should().BeNull();
        scope.ServiceProvider.GetRequiredService<IPositionStore>().Should().BeOfType<HttpPositionStore>();
    }

    [Theory]
    [InlineData("https://risk-management-service:8081")]
    [InlineData("risk-management-service:8081")]
    public async Task T_10_1057_使えない宛先は起動時に落とす(string address)
    {
        await using var factory = new Factory(grpc: address, riskBaseUrl: "http://risk");

        var act = () => factory.CreateClient();

        act.Should().Throw<InvalidOperationException>().WithMessage("*RiskManagement:Grpc*");
    }

    private sealed class Factory(string? grpc, string? riskBaseUrl) : WebApplicationFactory<Program>
    {
        private readonly string _dbName = Guid.NewGuid().ToString();

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Testing");
            if (grpc is not null)
                builder.UseSetting("RiskManagement:Grpc", grpc);
            builder.ConfigureAppConfiguration((_, cfg) =>
            {
                var settings = new Dictionary<string, string?>
                {
                    ["RabbitMq:ConnectionString"] = "amqp://localhost",
                    ["Otlp:Endpoint"] = "http://localhost:4317",
                    ["Auth:Authority"] = "https://localhost/realms/test",
                };
                if (riskBaseUrl is not null)
                    settings["RiskManagement:BaseUrl"] = riskBaseUrl;
                cfg.AddInMemoryCollection(settings);
            });

            builder.ConfigureServices(services =>
            {
                var toRemove = services
                    .Where(d => d.ServiceType == typeof(DbContextOptions<MarketMonitorDbContext>)
                             || (d.ServiceType.IsGenericType
                                 && d.ServiceType.GetGenericTypeDefinition().FullName?
                                     .Contains("IDbContextOptionsConfiguration") == true
                                 && d.ServiceType.GenericTypeArguments.Length == 1
                                 && d.ServiceType.GenericTypeArguments[0] == typeof(MarketMonitorDbContext)))
                    .ToList();
                foreach (var d in toRemove) services.Remove(d);
                services.AddDbContext<MarketMonitorDbContext>(opt => opt.UseInMemoryDatabase(_dbName));
                services.DisableAllExternalWolverineTransports();
                services.AddAuthentication(TestAuthHandler.SchemeName)
                    .AddScheme<AuthenticationSchemeOptions, TestAuthHandler>(TestAuthHandler.SchemeName, _ => { });
            });
        }
    }
}

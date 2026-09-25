using AiStockTrading.Shared.Contracts.Trading;
using AwesomeAssertions;
using Grpc.Core;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using TradeDecisionService.Features.TradeDecision;
using TradeDecisionService.Infrastructure.ExternalServices;
using Xunit;
using Proto = AiStockTrading.Shared.Grpc.RiskManagement.V1;

namespace TradeDecisionService.Tests;

// T-10-1058, NFR, FR-04, FR-10, IADR-0331 決定 3・6, IADR-0427 決定 5, #997 (#753):
// **呼び出し元ごとの timeout / retry が実際に効くこと**の結合試験（段 1 の GrpcAssumptionsClientIntegrationTests と同型）。
// 実 Kestrel の h2c へ本当に往復させ、構成（`RiskManagement:GrpcTimeoutSeconds` / `GrpcMaxAttempts`）から組む。
// 🔴 Testcontainers を使わない（Docker 不要。既定 CI の PR でそのまま走る）。
public class RiskManagementGrpcIntegrationTests
{
    // 陰性対照の deadline（#885 と同じく 1 秒ではなく 3 秒。上界 10 秒は比例させない）。
    private const string NegativeControlTimeoutSeconds = "3";

    private static readonly Proto.GetOpenPositionsResponse OnePosition = Build();

    private static Proto.GetOpenPositionsResponse Build()
    {
        var r = new Proto.GetOpenPositionsResponse();
        r.Positions.Add(new Proto.OpenPositionRow
        {
            Symbol = "AAPL",
            Market = Proto.Market.UnitedStates,
            Side = Proto.TradeSide.Buy,
            Quantity = 7,
            EntryPrice = "200",
            StopLossPrice = "190",
        });
        return r;
    }

    private static (ServiceProvider Sp, GrpcHeldPositionProvider Held) Compose(
        string address, Dictionary<string, string?> extra)
    {
        var values = new Dictionary<string, string?> { ["RiskManagement:Grpc"] = address };
        foreach (var (k, v) in extra)
            values[k] = v;

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddAiStockTradingRiskManagementGrpc(new ConfigurationBuilder().AddInMemoryCollection(values).Build());
        var sp = services.BuildServiceProvider();
        return (sp, new GrpcHeldPositionProvider(
            sp.GetRequiredService<RiskManagementGrpcTransport>(), sp.GetRequiredService<ILogger<GrpcHeldPositionProvider>>()));
    }

    [Fact]
    public async Task T_10_1058_提供側が応答すれば解決する()
    {
        await using var host = await RiskReadStubHost.StartAsync(new RiskReadStubBehavior
        {
            OpenPositions = (_, _) => Task.FromResult(OnePosition),
        });
        var (sp, held) = Compose(host.Address, []);
        await using (sp)
        {
            (await held.GetSignedQuantityAsync("AAPL", Market.UnitedStates)).Should().Be(7);
            host.Behavior.Calls.Should().Be(1);
        }
    }

    // 🔴 陰性対照: 提供側が黙れば**構成した deadline で**打ち切って不明へ倒れる（経過時間まで測る）。
    [Fact]
    public async Task T_10_1058_提供側が黙れば構成した_deadline_で不明へ倒れる()
    {
        await using var host = await RiskReadStubHost.StartAsync(new RiskReadStubBehavior
        {
            OpenPositions = RiskReadStubBehavior.RespondsOnceThenHangs(OnePosition),
        });
        var (sp, held) = Compose(host.Address, new() { ["RiskManagement:GrpcTimeoutSeconds"] = NegativeControlTimeoutSeconds });
        await using (sp)
        {
            // 暖機（接続確立を deadline の予算に含めない。#885）。1 回目は応答する。
            (await held.GetSignedQuantityAsync("AAPL", Market.UnitedStates)).Should().Be(7);

            var elapsed = System.Diagnostics.Stopwatch.StartNew();
            var result = await held.GetPositionAsync("AAPL", Market.UnitedStates);
            elapsed.Stop();

            result.Should().BeNull("黙る提供側は不明（保有なしではない）");
            host.Behavior.Calls.Should().Be(2, "既定は再試行しない");
            elapsed.Elapsed.Should().BeLessThan(
                TimeSpan.FromSeconds(10), $"構成した {NegativeControlTimeoutSeconds} 秒の deadline で打ち切られること");
        }
    }

    // 陽性対照: 一時的な UNAVAILABLE は MaxAttempts=2 で 2 回目に成功する（呼ばれた回数を数える）。
    [Fact]
    public async Task T_10_1058_一時的な_UNAVAILABLE_は再試行して成功する()
    {
        await using var host = await RiskReadStubHost.StartAsync(new RiskReadStubBehavior
        {
            OpenPositions = RiskReadStubBehavior.FailsThenSucceeds(StatusCode.Unavailable, 1, OnePosition),
        });
        var (sp, held) = Compose(host.Address, new() { ["RiskManagement:GrpcMaxAttempts"] = "2" });
        await using (sp)
        {
            (await held.GetSignedQuantityAsync("AAPL", Market.UnitedStates)).Should().Be(7);
            host.Behavior.Calls.Should().Be(2);
        }
    }

    // 🔴 陰性対照: 既定は再試行しない（REST と同じ振る舞い）。
    [Fact]
    public async Task T_10_1058_既定では再試行しない()
    {
        await using var host = await RiskReadStubHost.StartAsync(new RiskReadStubBehavior
        {
            OpenPositions = RiskReadStubBehavior.FailsThenSucceeds(StatusCode.Unavailable, 1, OnePosition),
        });
        var (sp, held) = Compose(host.Address, []);
        await using (sp)
        {
            (await held.GetSignedQuantityAsync("AAPL", Market.UnitedStates)).Should().BeNull();
            host.Behavior.Calls.Should().Be(1);
        }
    }

    // 🔴 陰性対照: 待っても変わらない status は MaxAttempts=3 でも 1 回しか呼ばない。
    [Theory]
    [InlineData("PermissionDenied")]
    [InlineData("Unauthenticated")]
    [InlineData("InvalidArgument")]
    public async Task T_10_1058_恒久的な失敗は再試行しない(string statusName)
    {
        await using var host = await RiskReadStubHost.StartAsync(new RiskReadStubBehavior
        {
            OpenPositions = RiskReadStubBehavior.Fails<Proto.GetOpenPositionsResponse>(Enum.Parse<StatusCode>(statusName)),
        });
        var (sp, held) = Compose(host.Address, new() { ["RiskManagement:GrpcMaxAttempts"] = "3" });
        await using (sp)
        {
            (await held.GetSignedQuantityAsync("AAPL", Market.UnitedStates)).Should().BeNull();
            host.Behavior.Calls.Should().Be(1);
        }
    }

    // 🔴 陰性対照: 再試行し切っても例外を出さない（判断サイクルを止めない）。
    [Fact]
    public async Task T_10_1058_再試行し切っても例外を出さない()
    {
        await using var host = await RiskReadStubHost.StartAsync(new RiskReadStubBehavior
        {
            OpenPositions = RiskReadStubBehavior.Fails<Proto.GetOpenPositionsResponse>(StatusCode.Unavailable),
        });
        var (sp, held) = Compose(host.Address, new() { ["RiskManagement:GrpcMaxAttempts"] = "3" });
        await using (sp)
        {
            (await held.GetPositionAsync("AAPL", Market.UnitedStates)).Should().BeNull();
            host.Behavior.Calls.Should().Be(3);
        }
    }

    // s2s: トークン供給元が未整備なら authorization を付けずに送る（→ 提供側が UNAUTHENTICATED → 不明）。REST と同じ向き。
    [Fact]
    public async Task T_10_1058_資格情報が未整備なら_authorization_を付けない()
    {
        await using var host = await RiskReadStubHost.StartAsync(new RiskReadStubBehavior
        {
            OpenPositions = (_, _) => Task.FromResult(OnePosition),
        });
        var (sp, held) = Compose(host.Address, []);
        await using (sp)
        {
            await held.GetSignedQuantityAsync("AAPL", Market.UnitedStates);
            host.Behavior.LastAuthorization.Should().BeNull();
        }
    }
}

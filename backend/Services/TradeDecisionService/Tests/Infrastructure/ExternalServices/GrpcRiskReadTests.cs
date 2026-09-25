extern alias RiskManagementWorker;

using AiStockTrading.Shared.Contracts.Trading;
using AwesomeAssertions;
using Grpc.Core;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RiskManagementWorker::RiskManagementService.Domain;
using RiskReadWireMapping = RiskManagementWorker::RiskManagementService.Features.RiskManagement.RiskReadWireMapping;
using RiskManagementWorker::RiskManagementService.Features.RiskManagement.GetOpenPositions;
using RiskManagementWorker::RiskManagementService.Features.RiskManagement.GetSizingContext;
using RiskManagementWorker::RiskManagementService.Features.RiskManagement.GetWorkingEntryOrders;
using TradeDecisionService.Features.TradeDecision;
using TradeDecisionService.Infrastructure.ExternalServices;
using Xunit;
using Proto = AiStockTrading.Shared.Grpc.RiskManagement.V1;

namespace TradeDecisionService.Tests.Infrastructure.ExternalServices;

// T-10-1052, T-10-1053, T-10-1054, T-10-1056, NFR, FR-04, FR-10, IADR-0427 決定 3・5, #997 (#753):
// 判断サービスが gRPC で読むリスク管理の 3 つの読み取り（保有建玉・未約定の新規建て注文・サイジング文脈）の**原則 A**と**契約**。
//
// 🔴 **実 Kestrel の h2c で本当に往復させる**（RiskReadStubHost）。欠落は proto3 の既定値（0・""・列挙の 0）として線に乗るので、
// 受け手の写し（`Has*` を見て null へ写す）が効いているかは、**実際に符号化・復号された message** でしか確かめられない。
// 輸送は本番と同じ `AddAiStockTradingRiskManagementGrpc(config)` から組む。
public class GrpcRiskReadTests
{
    private static async Task<(ServiceProvider Sp, RiskManagementGrpcTransport Transport)> TransportAsync(string address)
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["RiskManagement:Grpc"] = address })
            .Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddAiStockTradingRiskManagementGrpc(config);
        var sp = services.BuildServiceProvider();
        await Task.CompletedTask;
        return (sp, sp.GetRequiredService<RiskManagementGrpcTransport>());
    }

    private static GrpcHeldPositionProvider Held(RiskManagementGrpcTransport t, ServiceProvider sp) =>
        new(t, sp.GetRequiredService<ILogger<GrpcHeldPositionProvider>>());

    private static GrpcSizingContextProvider Sizing(RiskManagementGrpcTransport t, ServiceProvider sp) =>
        new(t, sp.GetRequiredService<ILogger<GrpcSizingContextProvider>>());

    private static Proto.OpenPositionRow Row(Action<Proto.OpenPositionRow>? tweak = null)
    {
        var row = new Proto.OpenPositionRow
        {
            Symbol = "AAPL",
            Market = Proto.Market.UnitedStates,
            Side = Proto.TradeSide.Buy,
            Quantity = 10,
            EntryPrice = "200",
            StopLossPrice = "190",
        };
        tweak?.Invoke(row);
        return row;
    }

    private static Func<int, CancellationToken, Task<Proto.GetOpenPositionsResponse>> Positions(params Proto.OpenPositionRow[] rows) =>
        (_, _) =>
        {
            var response = new Proto.GetOpenPositionsResponse();
            response.Positions.AddRange(rows);
            return Task.FromResult(response);
        };

    // ---- T-10-1052: 保有建玉（判断） ----

    [Fact]
    public async Task T_10_1052_空の一覧は保有なし_失敗は不明()
    {
        await using var ok = await RiskReadStubHost.StartAsync(new RiskReadStubBehavior { OpenPositions = Positions() });
        await using var failing = await RiskReadStubHost.StartAsync(new RiskReadStubBehavior
        {
            OpenPositions = RiskReadStubBehavior.Fails<Proto.GetOpenPositionsResponse>(StatusCode.PermissionDenied),
        });
        var (sp1, t1) = await TransportAsync(ok.Address);
        var (sp2, t2) = await TransportAsync(failing.Address);
        await using (sp1)
        await using (sp2)
        {
            (await Held(t1, sp1).GetPositionAsync("AAPL", Market.UnitedStates)).Should().Be(HeldPosition.None);
            (await Held(t2, sp2).GetPositionAsync("AAPL", Market.UnitedStates)).Should().BeNull("照会できないのは不明（保有なしではない）");
        }
    }

    // 🔴 欠落を既定値で読むと「一致しない」＝「保有なし」へ黙って倒れる（#943 の形）。gRPC でも不明（null）でなければならない。
    [Theory]
    [InlineData("銘柄なし")]
    [InlineData("市場が未指定")]
    [InlineData("方向が未指定")]
    [InlineData("数量なし")]
    [InlineData("数量 0")]
    public async Task T_10_1052_識別と数量の欠落は保有なしではなく不明(string how)
    {
        var row = how switch
        {
            "銘柄なし" => Row(r => r.ClearSymbol()),
            "市場が未指定" => Row(r => r.Market = Proto.Market.Unspecified),
            "方向が未指定" => Row(r => r.Side = Proto.TradeSide.Unspecified),
            "数量なし" => Row(r => r.ClearQuantity()),
            _ => Row(r => r.Quantity = 0),
        };
        await using var host = await RiskReadStubHost.StartAsync(new RiskReadStubBehavior { OpenPositions = Positions(row) });
        var (sp, t) = await TransportAsync(host.Address);
        await using (sp)
        {
            (await Held(t, sp).GetPositionAsync("AAPL", Market.UnitedStates)).Should().BeNull(how);
        }
    }

    // 🔴 価格の欠落は 0 ではなく不明（含み損益・損切り判定を偽の値にしない）。
    [Fact]
    public async Task T_10_1052_価格の欠落は_0_ではなく不明()
    {
        await using var host = await RiskReadStubHost.StartAsync(new RiskReadStubBehavior
        {
            OpenPositions = Positions(Row(r => { r.ClearEntryPrice(); r.ClearStopLossPrice(); })),
        });
        var (sp, t) = await TransportAsync(host.Address);
        await using (sp)
        {
            var held = await Held(t, sp).GetPositionAsync("AAPL", Market.UnitedStates);

            held.Should().NotBeNull();
            held!.SignedQuantity.Should().Be(10);
            held.AverageEntryPrice.Should().BeNull();
            held.StopLossPrice.Should().BeNull();
        }
    }

    [Fact]
    public async Task T_10_1052_読めない_10_進は不明()
    {
        await using var host = await RiskReadStubHost.StartAsync(new RiskReadStubBehavior
        {
            OpenPositions = Positions(Row(r => r.EntryPrice = "not-a-decimal")),
        });
        var (sp, t) = await TransportAsync(host.Address);
        await using (sp)
        {
            (await Held(t, sp).GetPositionAsync("AAPL", Market.UnitedStates)).Should().BeNull();
        }
    }

    // ---- T-10-1054: 未約定の新規建て注文 ----

    [Theory]
    [InlineData("承認時刻なし")]
    [InlineData("価格なし")]
    [InlineData("方向が未指定")]
    [InlineData("残数量なし")]
    [InlineData("市場が未指定")]
    public async Task T_10_1054_未約定の欠落は無いではなく不明(string how)
    {
        var order = new Proto.WorkingEntryOrderRow
        {
            Symbol = "MSFT",
            Market = Proto.Market.UnitedStates,
            Side = Proto.TradeSide.Buy,
            RemainingQuantity = 3,
            Price = "400",
            ApprovedAt = DateTimeOffset.UtcNow.ToString("O"),
        };
        switch (how)
        {
            case "承認時刻なし": order.ClearApprovedAt(); break;
            case "価格なし": order.ClearPrice(); break;
            case "方向が未指定": order.Side = Proto.TradeSide.Unspecified; break;
            case "残数量なし": order.ClearRemainingQuantity(); break;
            default: order.Market = Proto.Market.Unspecified; break;
        }

        await using var host = await RiskReadStubHost.StartAsync(new RiskReadStubBehavior
        {
            WorkingEntryOrders = (_, _) =>
            {
                var r = new Proto.GetWorkingEntryOrdersResponse();
                r.Orders.Add(order);
                return Task.FromResult(r);
            },
        });
        var (sp, t) = await TransportAsync(host.Address);
        await using (sp)
        {
            (await Held(t, sp).GetWorkingEntryOrdersAsync("MSFT", Market.UnitedStates)).Should().BeNull(how);
        }
    }

    [Fact]
    public async Task T_10_1054_空の一覧は無い_失敗は不明()
    {
        await using var ok = await RiskReadStubHost.StartAsync(new RiskReadStubBehavior());
        await using var failing = await RiskReadStubHost.StartAsync(new RiskReadStubBehavior
        {
            WorkingEntryOrders = RiskReadStubBehavior.Fails<Proto.GetWorkingEntryOrdersResponse>(StatusCode.Internal),
        });
        var (sp1, t1) = await TransportAsync(ok.Address);
        var (sp2, t2) = await TransportAsync(failing.Address);
        await using (sp1)
        await using (sp2)
        {
            (await Held(t1, sp1).GetWorkingEntryOrdersAsync("MSFT", Market.UnitedStates)).Should().Be(WorkingEntryOrders.None);
            (await Held(t2, sp2).GetWorkingEntryOrdersAsync("MSFT", Market.UnitedStates)).Should().BeNull();
        }
    }

    // ---- T-10-1053: サイジング文脈 ----

    private static Proto.GetSizingContextResponse SizingProto(Action<Proto.GetSizingContextResponse>? tweak = null)
    {
        var r = RiskReadWireMapping.ToProto(new SizingContextView(
            10000m, 2000m, 3000m, 1, 0.05m, BrokerProvider.MoomooSimulate, TradingDefaults.CreateRiskLimits(),
            StopLossExecutionMethod.SoftwareStop));
        tweak?.Invoke(r);
        return r;
    }

    private static async Task<SizingContext> ReadSizingAsync(Proto.GetSizingContextResponse response)
    {
        await using var host = await RiskReadStubHost.StartAsync(new RiskReadStubBehavior
        {
            SizingContext = (_, _) => Task.FromResult(response),
        });
        var (sp, t) = await TransportAsync(host.Address);
        await using (sp)
            return await Sizing(t, sp).GetContextAsync();
    }

    // 🔴 口座を照会できていない（資金・残枠が無い）は**不明のまま**運ぶ。0 と読むと「枠を使い切った」になる。
    [Fact]
    public async Task T_10_1053_資金と残枠の欠落は_0_ではなく不明のまま()
    {
        var context = await ReadSizingAsync(SizingProto(r =>
        {
            r.ClearCapital();
            r.ClearStageCapitalRemaining();
            r.ClearDailyOrderRemaining();
        }));

        context.Capital.Should().BeNull();
        context.StageCapitalRemaining.Should().BeNull();
        context.DailyOrderRemaining.Should().BeNull();
        context.ConsecutiveLosses.Should().Be(1, "他の項目は読めている（安全既定へ倒したのではない）");
        context.Mode.Should().Be(BrokerProvider.MoomooSimulate);
    }

    // 🔴 連敗数・DD・動作モード・上限の欠落は、REST と同じく契約の食い違いとして**残枠 0 の安全既定**へ倒す
    // （0 と読むと縮小係数が外れる＝上限側への fail-open）。
    [Theory]
    [InlineData("連敗数なし")]
    [InlineData("DD なし")]
    [InlineData("モード未指定")]
    [InlineData("上限なし")]
    [InlineData("上限の 1 項目なし")]
    public async Task T_10_1053_必須の欠落は残枠_0_の安全既定(string how)
    {
        var context = await ReadSizingAsync(SizingProto(r =>
        {
            switch (how)
            {
                case "連敗数なし": r.ClearConsecutiveLosses(); break;
                case "DD なし": r.ClearDrawdownRatio(); break;
                case "モード未指定": r.Mode = Proto.BrokerProvider.Unspecified; break;
                case "上限なし": r.Limits = null; break;
                default: r.Limits.ClearLosingStreakSizeFactor(); break;
            }
        }));

        context.StageCapitalRemaining.Should().Be(0m, how);
        context.DailyOrderRemaining.Should().Be(0m, how);
        context.Capital.Should().BeNull(how);
    }

    [Fact]
    public async Task T_10_1053_損切りの実行機構の未指定は_S0_ではなく不明()
    {
        var context = await ReadSizingAsync(SizingProto(r => r.StopLossMethod = Proto.StopLossExecutionMethod.Unspecified));

        context.StopLossMethod.Should().BeNull();
        context.StageCapitalRemaining.Should().Be(2000m, "未指定は不明であり、応答全体を捨てる理由ではない");
    }

    // ---- T-10-1056: 契約（送り手の本物の型 → 提供側の写し → 線 → 受け手の写し） ----

    // 🔴 送り手の本物の型（OpenPositionView・WorkingEntryOrderView・SizingContextView）を**提供側の本物の写し**
    // （RiskReadWireMapping）で proto にし、実 h2c を通して受け手に読ませる。どちらかの写しで項目を取り違えれば赤になる
    // （REST の T-10-800 / T-10-744 / T-10-802 の gRPC 版）。
    [Fact]
    public async Task T_10_1056_送り手の型の建玉と未約定を受け手が同じ値で読む()
    {
        var approvedAt = new DateTimeOffset(2026, 9, 25, 13, 30, 0, TimeSpan.FromHours(9));
        await using var host = await RiskReadStubHost.StartAsync(new RiskReadStubBehavior
        {
            OpenPositions = (_, _) =>
            {
                var r = new Proto.GetOpenPositionsResponse();
                r.Positions.Add(RiskReadWireMapping.ToProto(
                    new OpenPositionView("AAPL", Market.UnitedStates, TradeSide.Buy, 3378, 187.25m, 170.5m)));
                r.Positions.Add(RiskReadWireMapping.ToProto(
                    new OpenPositionView("7203", Market.Japan, TradeSide.Sell, 100, 2500m, 2750m)));
                return Task.FromResult(r);
            },
            WorkingEntryOrders = (_, _) =>
            {
                var r = new Proto.GetWorkingEntryOrdersResponse();
                r.Orders.Add(RiskReadWireMapping.ToProto(new WorkingEntryOrderView(
                    Guid.NewGuid(), "MSFT", Market.UnitedStates, TradeSide.Buy, 3, 400.125m, approvedAt)));
                return Task.FromResult(r);
            },
        });
        var (sp, t) = await TransportAsync(host.Address);
        await using (sp)
        {
            var held = Held(t, sp);

            (await held.GetPositionAsync("AAPL", Market.UnitedStates))
                .Should().Be(new HeldPosition(3378, 187.25m, 170.5m));
            (await held.GetPositionAsync("7203", Market.Japan))
                .Should().Be(new HeldPosition(-100, 2500m, 2750m), "日本（C# の 0）が線上で未指定に化けない");
            (await held.GetPositionAsync("MSFT", Market.UnitedStates)).Should().Be(HeldPosition.None);

            var working = await held.GetWorkingEntryOrdersAsync("MSFT", Market.UnitedStates);
            working!.Orders.Should().ContainSingle()
                .Which.Should().Be(new WorkingEntryOrder(TradeSide.Buy, 3, 400.125m, approvedAt));
            working.Orders.Single().ApprovedAt.Offset.Should().Be(TimeSpan.FromHours(9), "時刻はオフセットごと運ぶ");
        }
    }

    [Fact]
    public async Task T_10_1056_送り手の型のサイジング文脈を受け手が同じ値で読む()
    {
        var limits = TradingDefaults.CreateRiskLimits();
        var context = await ReadSizingAsync(RiskReadWireMapping.ToProto(new SizingContextView(
            12345.67m, 0m, 1500.5m, 2, 0.0725m, BrokerProvider.InternalPaper, limits,
            StopLossExecutionMethod.BrokerStopOrder)));

        context.Capital.Should().Be(12345.67m);
        context.StageCapitalRemaining.Should().Be(0m, "在る 0 は 0（枠を使い切った）として届く");
        context.DailyOrderRemaining.Should().Be(1500.5m);
        context.ConsecutiveLosses.Should().Be(2);
        context.DrawdownRatio.Should().Be(0.0725m);
        context.Mode.Should().Be(BrokerProvider.InternalPaper, "C# の 0（内蔵 paper）が未指定＝安全既定に化けない");
        context.Limits.Should().Be(limits);
        context.StopLossMethod.Should().Be(StopLossExecutionMethod.BrokerStopOrder, "C# の 0（S0）が不明に化けない");
    }
}

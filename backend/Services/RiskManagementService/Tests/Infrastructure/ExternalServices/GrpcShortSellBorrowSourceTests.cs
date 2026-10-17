extern alias OrderExecutionWorker;

using System.Net;
using System.Text.Json;
using AiStockTrading.Shared.Contracts.Trading;
using AwesomeAssertions;
using Grpc.Core;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using RiskManagementService.Features.RiskManagement;
using RiskManagementService.Infrastructure.ExternalServices;
using Xunit;
using OrderExecutionReadWireMapping = OrderExecutionWorker::OrderExecutionService.Features.OrderExecution.OrderExecutionReadWireMapping;
using Proto = AiStockTrading.Shared.Grpc.OrderExecution.V1;
using ShortPermitStatus = OrderExecutionWorker::OrderExecutionService.Features.OrderExecution.QueryShortPermit.ShortPermitStatus;
using ShortPermitUnknownReasons = OrderExecutionWorker::OrderExecutionService.Features.OrderExecution.QueryShortPermit.ShortPermitUnknownReasons;
using ShortPermitView = OrderExecutionWorker::OrderExecutionService.Features.OrderExecution.QueryShortPermit.ShortPermitView;

namespace RiskManagementService.Tests;

// T-10-2905, NFR, FR-10, UC-06, ADR-0016 決定3, MSP:ADR-0029, IADR-0425 決定4, IADR-0559 決定 2〜4, #1391 (#753):
// リスク管理が gRPC（`OrderExecutionRead/GetShortPermit`）で読む借株可否の**契約**・**失敗の意味**・**再試行**。
// 🔴 解釈は REST（HttpShortSellBorrowSource.Interpret）と同じ 1 つ。送り手の本物の型（ShortPermitView）を、REST へは送り手の web 既定で、
// gRPC へは送り手の写し（OrderExecutionReadWireMapping）で出し、**両方の受け手が同じ観測を返す**ことを実 Kestrel の h2c で往復させて見る。
public class GrpcShortSellBorrowSourceTests
{
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);
    private static readonly DateTimeOffset ObservedAt = new(2026, 10, 17, 14, 0, 0, TimeSpan.Zero);

    private static ServiceProvider Compose(string address, Dictionary<string, string?>? extra = null)
    {
        var values = new Dictionary<string, string?> { ["OrderExecution:Grpc"] = address };
        foreach (var (k, v) in extra ?? [])
            values[k] = v;
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddAiStockTradingOrderExecutionGrpc(new ConfigurationBuilder().AddInMemoryCollection(values).Build());
        return services.BuildServiceProvider();
    }

    private static async Task<(ShortSellBorrowObservation Observation, OrderExecutionReadStubBehavior Behavior)> ReadGrpcAsync(
        Func<int, CancellationToken, Task<Proto.GetShortPermitResponse>> respond, Dictionary<string, string?>? extra = null)
    {
        var behavior = new OrderExecutionReadStubBehavior { ShortPermit = respond };
        await using var host = await OrderExecutionReadStubHost.StartAsync(behavior);
        await using var sp = Compose(host.Address, extra);
        var source = new GrpcShortSellBorrowSource(
            sp.GetRequiredService<OrderExecutionGrpcTransport>(), NullLogger<GrpcShortSellBorrowSource>.Instance);
        return (await source.GetAsync("AAPL", Market.UnitedStates, TestContext.Current.CancellationToken), behavior);
    }

    private static Task<ShortSellBorrowObservation> ReadRestAsync(ShortPermitView view) =>
        new HttpShortSellBorrowSource(
                new HttpClient(new HttpShortSellBorrowSourceTests.Stub(HttpStatusCode.OK, JsonSerializer.Serialize(view, Web)))
                {
                    BaseAddress = new Uri("http://order-execution"),
                },
                NullLogger<HttpShortSellBorrowSource>.Instance)
            .GetAsync("AAPL", Market.UnitedStates, TestContext.Current.CancellationToken);

    // 契約: 送り手の 3 つの状態（と理由）を、REST と gRPC の受け手が同じ観測として読む。要求は銘柄と市場を名前で運ぶ。
    [Theory]
    [InlineData(ShortPermitStatus.Permitted, true)]
    [InlineData(ShortPermitStatus.NotPermitted, false)]
    [InlineData(ShortPermitStatus.Unknown, null)]
    public async Task T_10_2905_送り手の型の借株可否を_REST_と同じ観測で読む(ShortPermitStatus status, bool? expected)
    {
        var view = new ShortPermitView(
            "AAPL", Market.UnitedStates, status,
            status == ShortPermitStatus.Unknown ? ShortPermitUnknownReasons.QueryFailed : null,
            status == ShortPermitStatus.Unknown ? null : ObservedAt);

        var (grpc, behavior) = await ReadGrpcAsync(OrderExecutionReadStubBehavior.Returns(OrderExecutionReadWireMapping.ToProto(view)));
        var rest = await ReadRestAsync(view);

        grpc.ShortPermit.Should().Be(expected);
        grpc.Should().Be(rest, "解釈は REST と同じ 1 つ（理由も同じ）");
        behavior.LastShortPermit!.Symbol.Should().Be("AAPL");
        behavior.LastShortPermit.Market.Should().Be(Proto.Market.UnitedStates, "C# の UnitedStates（1）を線上の名前で運ぶ");
    }

    // 🔴 「分からない」（UNKNOWN）と「状態が無い」（UNSPECIFIED＝契約の食い違い）を分ける。どちらも許可へ倒さない。
    [Fact]
    public async Task T_10_2905_状態の未指定と要求と別の答えは分からない()
    {
        var unspecified = new Proto.GetShortPermitResponse { Symbol = "AAPL", Market = Proto.Market.UnitedStates };
        var (missing, _) = await ReadGrpcAsync(OrderExecutionReadStubBehavior.Returns(unspecified));
        missing.ShortPermit.Should().BeNull();
        missing.UnknownReason.Should().Be("contract-mismatch", "REST で状態の項目が欠けたときと同じ");

        var other = new Proto.GetShortPermitResponse
        {
            Symbol = "MSFT",
            Market = Proto.Market.UnitedStates,
            Status = Proto.ShortPermitState.Permitted,
        };
        var (mismatch, _) = await ReadGrpcAsync(OrderExecutionReadStubBehavior.Returns(other));
        mismatch.ShortPermit.Should().BeNull();
        mismatch.UnknownReason.Should().Be("response-mismatch");

        var marketUnspecified = new Proto.GetShortPermitResponse { Symbol = "AAPL", Status = Proto.ShortPermitState.Permitted };
        (await ReadGrpcAsync(OrderExecutionReadStubBehavior.Returns(marketUnspecified))).Observation.ShortPermit
            .Should().BeNull("市場の未指定を C# の 0（日本）と読まない");
    }

    // 失敗の意味: 照会できない（不達・時間切れ・門で拒否）は「分からない」。許可・不許可のどちらにも倒さない。
    [Theory]
    [InlineData(StatusCode.Unavailable)]
    [InlineData(StatusCode.DeadlineExceeded)]
    [InlineData(StatusCode.Unauthenticated)]
    [InlineData(StatusCode.PermissionDenied)]
    [InlineData(StatusCode.InvalidArgument)]
    public async Task T_10_2905_照会の失敗は分からない(StatusCode status)
    {
        var (observation, behavior) = await ReadGrpcAsync(OrderExecutionReadStubBehavior.Fails<Proto.GetShortPermitResponse>(status));

        observation.ShortPermit.Should().BeNull();
        observation.UnknownReason.Should().Be("request-failed");
        behavior.ShortPermitCalls.Should().Be(1, "既定は再試行しない（REST と同じ 1 回）");
    }

    // 再試行は宣言したときだけ・UNAVAILABLE だけ（門の拒否は撃ち直さない）。
    [Fact]
    public async Task T_10_2905_再試行は宣言したときだけ不達に効く()
    {
        var permitted = OrderExecutionReadWireMapping.ToProto(
            new ShortPermitView("AAPL", Market.UnitedStates, ShortPermitStatus.Permitted, null, ObservedAt));
        var (retried, behavior) = await ReadGrpcAsync(
            OrderExecutionReadStubBehavior.FailsThenSucceeds(StatusCode.Unavailable, 1, permitted),
            new() { ["OrderExecution:GrpcMaxAttempts"] = "2" });
        retried.ShortPermit.Should().BeTrue();
        behavior.ShortPermitCalls.Should().Be(2);

        var (denied, deniedBehavior) = await ReadGrpcAsync(
            OrderExecutionReadStubBehavior.Fails<Proto.GetShortPermitResponse>(StatusCode.PermissionDenied),
            new() { ["OrderExecution:GrpcMaxAttempts"] = "3" });
        denied.ShortPermit.Should().BeNull();
        deniedBehavior.ShortPermitCalls.Should().Be(1);
    }
}

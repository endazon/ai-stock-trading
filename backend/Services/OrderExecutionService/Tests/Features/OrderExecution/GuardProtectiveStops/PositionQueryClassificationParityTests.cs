using AiStockTrading.Shared.Contracts.Events;
using AiStockTrading.Shared.Contracts.Ports;
using AiStockTrading.Shared.Contracts.Trading;
using AiStockTrading.TestSupport.Messaging;
using AiStockTrading.TestSupport.PlatformShim.Foundation.Extensions;
using AwesomeAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using OrderExecutionService.Common.Abstractions;
using OrderExecutionService.Domain;
using OrderExecutionService.Features.OrderExecution.GuardProtectiveStops;
using OrderExecutionService.Features.OrderExecution.ObserveBrokerAvailability;
using OrderExecutionService.Features.OrderExecution.ObserveBrokerPositions;
using OrderExecutionService.Hosted;
using OrderExecutionService.Infrastructure.ExternalServices;
using OrderExecutionService.Infrastructure.Persistence;
using Wolverine;
using Wolverine.Runtime;
using Xunit;

namespace OrderExecutionService.Tests;

// 🔴 T-10-2210〜T-10-2212・T-10-2218・T-10-2219, FR-10, NFR, #1164, IADR-0487:
// **同じ建玉照会の失敗は、どの経路でも同じ種類として台帳へ載る。** 一過性の失敗なら、ガードは 1 回だけ照会し直す。
//
// PoC（2026-10-03 00:28〜00:29 JST）で、同じ時刻の失敗が観測の常駐では Transient、ガードでは Other と読まれた。
// 分類器は moomoo のアダプタの 1 か所だけだが、観測の常駐は種類を捨てて台帳へ「不明」で報告し、
// ガードは照会し直しの最終結果だけを載せていた（比べられる値になっていなかった）。
// 本クラスは偽物の分類ではなく、**本物のアダプタ（MoomooBrokerAdapter）＋本物の分類器**に、偽物のクライアントから
// 同じ形の例外を与え、経路ごとの報告を突き合わせる。
public class PositionQueryClassificationParityTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 3, 0, 28, 0, TimeSpan.Zero);

    private const string ServiceName = "ai-stock-trading.order-execution-service";

    private sealed class FakeClock : IClock
    {
        public DateTimeOffset UtcNow => Now;
    }

    private sealed class FixedTimeProvider : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => Now;
    }

    // 失敗の形ごとの分類（分類器の表。分からないものは Other のまま）。
    public static TheoryData<string, Func<Exception>, PositionQueryFailure> FailureShapes() => new()
    {
        { "返信待ちの打ち切り", () => new TimeoutException("返信待ち（試験）"), PositionQueryFailure.Transient },
        { "接続の確立の失敗", () => new BrokerUnavailableException("OpenD 不達（試験）"), PositionQueryFailure.Transient },
        { "-100 打ち切り", () => new MoomooTradeRequestException("GetPositionList", MoomooRetType.TimeOut, ""), PositionQueryFailure.Transient },
        { "-200 切断", () => new MoomooTradeRequestException("GetPositionList", MoomooRetType.DisConnect, ""), PositionQueryFailure.Transient },
        { "-400 不明", () => new MoomooTradeRequestException("GetPositionList", MoomooRetType.Unknown, ""), PositionQueryFailure.Transient },
        { "-1 頻度制限", () => new MoomooTradeRequestException("GetPositionList", MoomooRetType.Failed, "Maximum 10 times per 30 seconds"), PositionQueryFailure.RateLimited },
        { "-1 業務上の失敗", () => new MoomooTradeRequestException("GetPositionList", MoomooRetType.Failed, "account not found"), PositionQueryFailure.Other },
        { "-500 応答の読み損ね", () => new MoomooTradeRequestException("GetPositionList", MoomooRetType.Invalid, ""), PositionQueryFailure.Other },
        { "分類できない例外", () => new InvalidOperationException("想定外（試験）"), PositionQueryFailure.Other },
    };

    private static bool IsRetryable(PositionQueryFailure failure) =>
        failure is PositionQueryFailure.Transient or PositionQueryFailure.RateLimited;

    private static MoomooBrokerAdapter Adapter(ScriptedPositionsMoomooClient client) =>
        new(client, BrokerProvider.MoomooSimulate);

    private static ProtectiveStopOrder ActiveStop() =>
        new(Guid.NewGuid(), Guid.NewGuid(), "stop-1", "AAPL", Market.UnitedStates, TradeSide.Buy,
            ProductType.Cash, BrokerProvider.MoomooSimulate, 10, 950m, 1m, Attempt: 1,
            ProtectiveStopState.Active, Now.AddMinutes(-5), Now.AddMinutes(-5));

    // 本番の組み立て（Program.cs）と同じく、ガードの発注口と建玉照会の口に同じアダプタを渡し、照会し直しも渡す。
    private static async Task<ProtectiveStopGuardResult> RunGuardAsync(
        MoomooBrokerAdapter adapter, RecordingPositionQueryHealth health)
    {
        var stops = new InMemoryProtectiveStopOrderStore();
        stops.Save(ActiveStop());
        var guard = new ProtectiveStopGuard(
            adapter, adapter, stops, new InMemoryExecutedOrderStore(), new InMemoryOrderReservationStore(), new FakeClock(),
            positionQueryRetry: new PositionQueryRetry(
                new ProtectiveStopGuardOptions(), (_, _) => Task.CompletedTask, () => 0.5),
            positionQueryHealth: health);
        return await guard.RunOnceAsync(10);
    }

    private static Task<IHost> NewHostAsync() =>
        Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.UseAiStockTradingRabbitMq(ServiceName, "amqp://guest:guest@localhost:5672");
                opts.StubAllExternalTransports();
            })
            .StartAsync();

    private static async Task<bool> RunSnapshotAsync(IBrokerPositionSource source, RecordingPositionQueryHealth health)
    {
        using var host = await NewHostAsync();
        var service = new BrokerPositionSnapshotService(
            source,
            host.Services.GetRequiredService<IWolverineRuntime>(),
            new FixedTimeProvider(),
            Options.Create(new PositionReconciliationOptions { Enabled = true }),
            NullLogger<BrokerPositionSnapshotService>.Instance,
            BrokerPositionSnapshotUnattributedDetectionTests.DetectorScopes(
                new InMemoryProtectiveStopOrderStore(), new InMemoryExecutedOrderStore(), () => Now),
            health);
        var published = await service.PublishOnceAsync(CancellationToken.None);
        await host.StopAsync();
        return published;
    }

    // ---- T-10-2210: 同じ失敗は、観測の常駐とガードで同じ種類になる ----

    [Theory]
    [MemberData(nameof(FailureShapes))]
    public async Task T_10_2210_同じ失敗は観測の常駐とガードで同じ種類として台帳へ載る(
        string label, Func<Exception> failure, PositionQueryFailure expected)
    {
        // 同じ形の失敗を、同じアダプタのインスタンス越しに 2 つの経路へ与える（本番も 1 つのアダプタを共有する）。
        var adapter = Adapter(new ScriptedPositionsMoomooClient(failure));
        var health = new RecordingPositionQueryHealth();

        var published = await RunSnapshotAsync(adapter, health);
        await RunGuardAsync(adapter, health);

        published.Should().BeFalse(label);
        var kind = expected.ToString();
        health.Reports.Should().Equal(
            [
                (PositionQuerySource.BrokerPositionSnapshot, false, kind),
                // 照会し直した巡回だけ「最初→最後」。最初の種類は観測の常駐と同じ。
                (PositionQuerySource.ProtectiveStopGuard, false, IsRetryable(expected) ? $"{kind}→{kind}" : kind),
            ],
            label);
    }

    // ---- T-10-2211: 一過性の失敗なら、ガードは本物のアダプタ越しに 1 回だけ照会し直す ----

    [Theory]
    [MemberData(nameof(FailureShapes))]
    public async Task T_10_2211_ガードは一過性の失敗なら1回だけ照会し直し_それ以外は照会し直さない(
        string label, Func<Exception> failure, PositionQueryFailure expected)
    {
        var client = new ScriptedPositionsMoomooClient(failure);
        var health = new RecordingPositionQueryHealth();

        var result = await RunGuardAsync(Adapter(client), health);

        client.PositionCalls.Should().Be(IsRetryable(expected) ? 2 : 1, label);
        result.Unknown.Should().Be(1, "照会できなければ巡回ごと据え置く（fail-safe）");
    }

    [Fact]
    public async Task T_10_2211_一過性の失敗の後に照会し直して成功すれば_据え置かず成功だけを報告する()
    {
        // 事故の形: 返信待ちの打ち切り（Transient）の直後に照会し直すと通る。30 秒の保護の穴を作らない。
        var client = new ScriptedPositionsMoomooClient(() => new TimeoutException("返信待ち（試験）"), null);
        var health = new RecordingPositionQueryHealth();

        await RunGuardAsync(Adapter(client), health);

        // 巡回の先頭の照会は 2 回目で通った（その後の注文照会の扱いは本試験の対象外）。
        client.PositionCalls.Should().Be(2);
        health.Reports.Should().Equal([(PositionQuerySource.ProtectiveStopGuard, true, (string?)null)]);
    }

    [Fact]
    public async Task T_10_2211_一過性の失敗の後の照会し直しが別の失敗なら_台帳は最初と最後の両方を載せる()
    {
        // #1164 で取り違えた形: 最初は Transient で照会し直したが、2 回目は Other で終わった。最終結果だけだと「Other」に見える。
        var client = new ScriptedPositionsMoomooClient(
            () => new TimeoutException("返信待ち（試験）"),
            () => new MoomooTradeRequestException("GetPositionList", MoomooRetType.Failed, "account not found"));
        var health = new RecordingPositionQueryHealth();

        await RunGuardAsync(Adapter(client), health);

        client.PositionCalls.Should().Be(2);
        health.Reports.Should().Equal([(PositionQuerySource.ProtectiveStopGuard, false, "Transient→Other")]);
    }

    // ---- T-10-2212: 共有の入口 ----

    private sealed class UnclassifiedSource(IReadOnlyList<BrokerPositionSnapshot>? positions) : IBrokerPositionSource
    {
        public int Calls { get; private set; }

        public Task<IReadOnlyList<BrokerPositionSnapshot>?> GetPositionsAsync(CancellationToken cancellationToken = default)
        {
            Calls++;
            return Task.FromResult(positions);
        }
    }

    [Fact]
    public async Task T_10_2212_共有の入口は分類つきの口を持つ供給元ではその種類を返す()
    {
        var client = new ScriptedPositionsMoomooClient(
            () => new MoomooTradeRequestException("GetPositionList", MoomooRetType.Failed, "请求频率太高"));

        var result = await PositionQueries.QueryAsync(Adapter(client));

        result.Positions.Should().BeNull();
        result.Failure.Should().Be(PositionQueryFailure.RateLimited);
        client.PositionCalls.Should().Be(1, "照会し直しは入口ではしない（ガードの照会し直しだけが行う）");
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task T_10_2212_共有の入口は分類つきの口を持たない供給元では種類を不明とする(bool succeeded)
    {
        var source = new UnclassifiedSource(succeeded ? [] : null);

        var result = await PositionQueries.QueryAsync(source);

        source.Calls.Should().Be(1);
        (result.Positions is not null).Should().Be(succeeded);
        result.Failure.Should().Be(PositionQueryFailure.None);
        result.ReportedFailureKind.Should().BeNull();
    }

    // ---- T-10-2218: 稼働 probe（moomoo では建玉照会）も同じ種類を報告する ----

    private sealed class FakeBroker : IBrokerAdapter
    {
        public BrokerProvider Provider => BrokerProvider.MoomooSimulate;

        public Task<BrokerOrder> PlaceOrderAsync(OrderIntent intent, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<BrokerOrder?> GetOrderAsync(string orderId, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task CancelOrderAsync(string orderId, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    private sealed class UnclassifiedProbe(bool operational) : IBrokerAvailabilityProbe
    {
        public Task<bool> IsOperationalAsync(CancellationToken cancellationToken = default) => Task.FromResult(operational);
    }

    private static async Task<bool> RunProbeAsync(IBrokerAvailabilityProbe probe, RecordingPositionQueryHealth health)
    {
        using var host = await NewHostAsync();
        var service = new BrokerAvailabilityProbeService(
            probe, new FakeBroker(),
            host.Services.GetRequiredService<IWolverineRuntime>(),
            new FixedTimeProvider(),
            Options.Create(new BrokerAvailabilityProbeOptions { Enabled = true }),
            NullLogger<BrokerAvailabilityProbeService>.Instance,
            accountSource: null,
            health);
        var published = false;
        Func<IMessageContext, Task> probeOnce = async _ =>
            published = await service.ProbeOnceAsync(CancellationToken.None);
        await host.TrackActivityForTest().ExecuteAndWaitAsync(probeOnce);
        await host.StopAsync();
        return published;
    }

    [Theory]
    [MemberData(nameof(FailureShapes))]
    public async Task T_10_2218_稼働probeは観測の常駐と同じ種類を報告する(
        string label, Func<Exception> failure, PositionQueryFailure expected)
    {
        var client = new ScriptedPositionsMoomooClient(failure);
        var health = new RecordingPositionQueryHealth();

        var published = await RunProbeAsync(Adapter(client), health);

        published.Should().BeFalse(label);
        client.PositionCalls.Should().Be(1, "到達の判定は建玉照会 1 回（照会し直さない）");
        health.Reports.Should().Equal([(PositionQuerySource.BrokerAvailabilityProbe, false, expected.ToString())], label);
    }

    [Fact]
    public async Task T_10_2218_稼働probeは建玉照会が通れば到達として発行する()
    {
        var client = new ScriptedPositionsMoomooClient((Func<Exception>?)null);
        var health = new RecordingPositionQueryHealth();

        var published = await RunProbeAsync(Adapter(client), health);

        published.Should().BeTrue();
        health.Reports.Should().Equal([(PositionQuerySource.BrokerAvailabilityProbe, true, (string?)null)]);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task T_10_2218_分類つきの口を持たないprobeは従来どおり種類なしで報告する(bool operational)
    {
        var health = new RecordingPositionQueryHealth();

        await RunProbeAsync(new UnclassifiedProbe(operational), health);

        health.Reports.Should().Equal([(PositionQuerySource.BrokerAvailabilityProbe, operational, (string?)null)]);
    }

    // ---- T-10-2219: 台帳へ載せる種類の書き方（1 か所） ----

    [Fact]
    public void T_10_2219_台帳へ載せる種類の書き方()
    {
        PositionQueryResult.Success([]).ReportedFailureKind.Should().BeNull("成功");
        new PositionQueryResult(null, PositionQueryFailure.None).ReportedFailureKind.Should().BeNull("分類の無い失敗は不明");
        PositionQueryResult.Failed(PositionQueryFailure.Other).ReportedFailureKind.Should().Be("Other");
        (PositionQueryResult.Failed(PositionQueryFailure.Other) with
        { Retries = 1, FirstFailure = PositionQueryFailure.Transient })
            .ReportedFailureKind.Should().Be("Transient→Other");
        (PositionQueryResult.Success([]) with { Retries = 1, FirstFailure = PositionQueryFailure.Transient })
            .ReportedFailureKind.Should().BeNull("照会し直して成功したら失敗の種類は載せない");
    }
}

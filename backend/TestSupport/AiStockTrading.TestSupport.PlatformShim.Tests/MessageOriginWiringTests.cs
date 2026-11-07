using System.Collections.Concurrent;
using System.Security.Cryptography;
using AiStockTrading.Shared.Contracts.Events;
using AiStockTrading.TestSupport.Messaging;
using AiStockTrading.TestSupport.PlatformShim.Foundation.Extensions;
using AiStockTrading.TestSupport.PlatformShim.Foundation.Messaging;
using AwesomeAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Wolverine;
using Wolverine.Tracking;
using Xunit;

namespace AiStockTrading.TestSupport.PlatformShim.Tests;

// 🔴 NFR, FR-10, IADR-0627 決定 2・3・4: Wolverine の配線を通して確かめる（送る側の印・受け手のハンドラの前の確かめ・起動の止め）。
// 受け手のハンドラは本アセンブリの試験用（MessageOriginProbeHandler）。実ブローカへは繋がない。
[Collection(nameof(MessageOriginProbeCollection))]
public class MessageOriginWiringTests
{
    private const string OrderExecution = "ai-stock-trading.order-execution-service";
    private const string MarketMonitor = "ai-stock-trading.market-monitor-service";

    private static readonly string MarketMonitorKey = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
    private static readonly string RiskManagementKey = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
    private static readonly string OrderExecutionKey = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));

    private static IConfiguration Config(string mode, params (string Issuer, string Key)[] keys)
    {
        var values = new Dictionary<string, string?> { ["MessageOrigin:Mode"] = mode };
        foreach (var (issuer, key) in keys)
            values[$"MessageOrigin:Keys:{issuer}"] = key;
        return new ConfigurationBuilder().AddInMemoryCollection(values).Build();
    }

    private static IConfiguration ReceiverConfig(string mode) => Config(
        mode,
        (MessageOriginCatalog.MarketMonitor, MarketMonitorKey),
        (MessageOriginCatalog.RiskManagement, RiskManagementKey),
        (MessageOriginCatalog.OrderExecution, OrderExecutionKey));

    private static Task<IHost> ReceiverAsync(IConfiguration config) =>
        Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.PinDynamicTypeLoadMode();
                opts.Discovery.IncludeType<MessageOriginProbeHandler>();
                opts.UseAiStockTradingMessageOrigin(OrderExecution, config);
                opts.StubAllExternalTransports();
            })
            .StartAsync();

    private static StopLossTriggered Trigger() =>
        (StopLossTriggered)MessageOriginSamples.Create(typeof(StopLossTriggered));

    // 損切り到達（発行元＝市場監視）の印つきの見出し。
    private static DeliveryOptions SignedBy(string issuer, string key, object message)
    {
        var envelope = new Envelope(message);
        new MessageOriginSigningRule(
            issuer, MessageOriginSignature.Create(new Dictionary<string, string?> { [issuer] = key }), TimeProvider.System)
            .Modify(envelope);
        var options = new DeliveryOptions();
        foreach (var (k, v) in envelope.Headers)
            options.WithHeader(k, v!);
        return options;
    }

    private static async Task InvokeAsync(IHost host, object message, DeliveryOptions? options = null)
    {
        using var scope = host.Services.CreateScope();
        var bus = scope.ServiceProvider.GetRequiredService<IMessageBus>();
        if (options is null)
            await bus.InvokeAsync(message);
        else
            await bus.InvokeAsync(message, options);
    }

    [Fact]
    public async Task 段2の受け手は印の無いメッセージをハンドラへ渡さない()
    {
        MessageOriginProbeHandler.Reset();
        using var host = await ReceiverAsync(ReceiverConfig("Enforce"));

        await InvokeAsync(host, Trigger());

        MessageOriginProbeHandler.Handled.Should().BeEmpty();
        await host.StopAsync();
    }

    [Fact]
    public async Task 段2の受け手は正しい印のメッセージを処理し同じメッセージの再送は渡さない()
    {
        MessageOriginProbeHandler.Reset();
        using var host = await ReceiverAsync(ReceiverConfig("Enforce"));
        var message = Trigger();
        var signed = SignedBy(MessageOriginCatalog.MarketMonitor, MarketMonitorKey, message);

        await InvokeAsync(host, message, signed);
        await InvokeAsync(host, message, signed);

        MessageOriginProbeHandler.Handled.Should().ContainSingle();
        await host.StopAsync();
    }

    [Fact]
    public async Task 段2の受け手は許されない発行元の正しい印も渡さない()
    {
        // 発注執行の鍵で正しく作った印でも、損切り到達の発行元（市場監視）ではない。
        MessageOriginProbeHandler.Reset();
        using var host = await ReceiverAsync(ReceiverConfig("Enforce"));
        var message = Trigger();

        await InvokeAsync(host, message, SignedBy(MessageOriginCatalog.OrderExecution, OrderExecutionKey, message));

        MessageOriginProbeHandler.Handled.Should().BeEmpty();
        await host.StopAsync();
    }

    [Fact]
    public async Task 段2の受け手は本文を改竄したメッセージを渡さない()
    {
        MessageOriginProbeHandler.Reset();
        using var host = await ReceiverAsync(ReceiverConfig("Enforce"));
        var signed = SignedBy(MessageOriginCatalog.MarketMonitor, MarketMonitorKey, Trigger());

        await InvokeAsync(host, MessageOriginSamples.Tampered(typeof(StopLossTriggered)), signed);

        MessageOriginProbeHandler.Handled.Should().BeEmpty();
        await host.StopAsync();
    }

    [Fact]
    public async Task 段1の受け手は印の無いメッセージも記録だけで処理する()
    {
        MessageOriginProbeHandler.Reset();
        using var host = await ReceiverAsync(Config("Report"));

        await InvokeAsync(host, Trigger());

        MessageOriginProbeHandler.Handled.Should().ContainSingle();
        await host.StopAsync();
    }

    [Fact]
    public async Task 表の外の型には確かめを付けない()
    {
        MessageOriginProbeHandler.Reset();
        using var host = await ReceiverAsync(ReceiverConfig("Enforce"));

        await InvokeAsync(host, new MessageOriginProbeOutside("x"));

        MessageOriginProbeHandler.Handled.Should().ContainSingle();
        await host.StopAsync();
    }

    // 送る側: 共通の配線の送り先（ブローカの exchange）に出る封筒に印が付く。
    [Fact]
    public async Task 発行元のサービスが送る表の型には印が付く()
    {
        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.PinDynamicTypeLoadMode();
                opts.UseAiStockTradingRabbitMq(MarketMonitor, "amqp://guest:guest@localhost:5672");
                opts.UseAiStockTradingMessageOrigin(MarketMonitor, Config(
                    "Enforce",
                    (MessageOriginCatalog.MarketMonitor, MarketMonitorKey),
                    (MessageOriginCatalog.TradeDecision, OrderExecutionKey)));
                opts.StubAllExternalTransports();
            })
            .StartAsync();
        var message = Trigger();

        var session = await host.TrackActivityForTest().PublishMessageAndWaitAsync(message);

        var sent = session.Sent.Envelopes().Where(e => e.Message is StopLossTriggered).ToList();
        sent.Should().NotBeEmpty();
        var verifier = new MessageOriginVerifier(
            MessageOriginMode.Enforce,
            MessageOriginSignature.Create(new Dictionary<string, string?> { [MessageOriginCatalog.MarketMonitor] = MarketMonitorKey }),
            TimeSpan.FromHours(24), TimeSpan.FromMinutes(5), TimeProvider.System,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<MessageOriginVerifier>.Instance);
        sent.Should().AllSatisfy(e =>
        {
            e.Headers[MessageOriginSignature.IssuerHeader].Should().Be(MessageOriginCatalog.MarketMonitor);
            verifier.Check(e).Should().Be(MessageOriginCheck.Valid);
        });
        await host.StopAsync();
    }

    // 🔴 鍵が無いまま段 2 で起動すると止まる（fail-closed）。発行元・受け手のどちらの鍵が欠けても止まる。
    [Theory]
    [MemberData(nameof(Services))]
    public async Task 段2で要る鍵が1つでも無ければ起動で止まる(string serviceName)
    {
        var issuerId = MessageOriginCatalog.IssuerIdFor(serviceName);
        var required = MessageOriginWiringExtensions.RequiredKeys(issuerId);
        required.Should().NotBeEmpty();

        foreach (var absent in required)
        {
            var keys = required.Where(k => k != absent).Select(k => (k, OrderExecutionKey)).ToArray();
            var act = async () =>
            {
                using var host = await Host.CreateDefaultBuilder()
                    .UseWolverine(opts =>
                    {
                        opts.PinDynamicTypeLoadMode();
                        opts.UseAiStockTradingMessageOrigin(serviceName, Config("Enforce", keys));
                        opts.StubAllExternalTransports();
                    })
                    .StartAsync();
            };

            (await act.Should().ThrowAsync<InvalidOperationException>()).Which.Message.Should().Contain(absent);
        }
    }

    [Theory]
    [MemberData(nameof(Services))]
    public async Task 段2で要る鍵が揃えば起動する(string serviceName)
    {
        var keys = MessageOriginWiringExtensions.RequiredKeys(MessageOriginCatalog.IssuerIdFor(serviceName))
            .Select(k => (k, OrderExecutionKey)).ToArray();
        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.PinDynamicTypeLoadMode();
                opts.UseAiStockTradingMessageOrigin(serviceName, Config("Enforce", keys));
                opts.StubAllExternalTransports();
            })
            .StartAsync();
        await host.StopAsync();
    }

    // chart は env の名前をシェルの識別子にするため、発行元の識別子の - を _ で書く（MessageOrigin__Keys__trade_decision）。
    [Fact]
    public async Task 鍵の名前の下線は発行元の識別子のハイフンとして読む()
    {
        var keys = MessageOriginWiringExtensions.RequiredKeys(MessageOriginCatalog.RiskManagement)
            .Select(k => (k.Replace('-', '_'), OrderExecutionKey)).ToArray();
        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.PinDynamicTypeLoadMode();
                opts.UseAiStockTradingMessageOrigin("ai-stock-trading.risk-management-service", Config("Enforce", keys));
                opts.StubAllExternalTransports();
            })
            .StartAsync();
        await host.StopAsync();
    }

    [Fact]
    public async Task 段1は鍵が無くても起動する()
    {
        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.PinDynamicTypeLoadMode();
                opts.UseAiStockTradingMessageOrigin(OrderExecution, Config("Report"));
                opts.StubAllExternalTransports();
            })
            .StartAsync();
        await host.StopAsync();
    }

    [Fact]
    public void 知らない段は起動で止まる()
    {
        var act = () => new WolverineOptions().UseAiStockTradingMessageOrigin(OrderExecution, Config("Strict"));

        act.Should().Throw<InvalidOperationException>();
    }

    // 鍵を持つ（発行元か受け手の）サービス。監査・通知・前提条件は表の型を発行も確かめもしない。
    public static TheoryData<string> Services() =>
    [
        "ai-stock-trading.trade-decision-service",
        "ai-stock-trading.order-execution-service",
        "ai-stock-trading.market-monitor-service",
        "ai-stock-trading.risk-management-service",
        "ai-stock-trading.information-collection-service",
        "ai-stock-trading.backtest-service",
        "ai-stock-trading.report-service",
        "ai-stock-trading.cost-control-service",
    ];

    [Theory]
    [InlineData("ai-stock-trading.audit-service")]
    [InlineData("ai-stock-trading.notification-service")]
    [InlineData("ai-stock-trading.configuration-service")]
    public void 表の型を発行も確かめもしないサービスは鍵が要らない(string serviceName) =>
        MessageOriginWiringExtensions.RequiredKeys(MessageOriginCatalog.IssuerIdFor(serviceName)).Should().BeEmpty();
}

[CollectionDefinition(nameof(MessageOriginProbeCollection), DisableParallelization = true)]
public sealed class MessageOriginProbeCollection;

public sealed record MessageOriginProbeOutside(string Value);

// 試験用の受け手（発注執行の役）。処理したメッセージを記録する。
public sealed class MessageOriginProbeHandler
{
    private static readonly ConcurrentQueue<object> HandledQueue = new();

    public static IReadOnlyCollection<object> Handled => HandledQueue;

    public static void Reset() => HandledQueue.Clear();

    public void Handle(StopLossTriggered message) => HandledQueue.Enqueue(message);

    public void Handle(MessageOriginProbeOutside message) => HandledQueue.Enqueue(message);
}

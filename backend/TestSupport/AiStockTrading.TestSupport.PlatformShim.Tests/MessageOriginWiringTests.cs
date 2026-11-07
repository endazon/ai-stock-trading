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

// 🔴 NFR, FR-10, IADR-0627 決定 2・3・4, IADR-0633 決定 2〜5: Wolverine の配線を通して確かめる（送る側の署名・受け手のハンドラの前の確かめ・起動の止め）。
// 受け手のハンドラは本アセンブリの試験用（MessageOriginProbeHandler）。実ブローカへは繋がない。
[Collection(nameof(MessageOriginProbeCollection))]
public class MessageOriginWiringTests
{
    private const string OrderExecution = "ai-stock-trading.order-execution-service";
    private const string MarketMonitor = "ai-stock-trading.market-monitor-service";

    // 構成: 段・自分の秘密の鍵（発行元のとき）・確かめる発行元の公開の鍵・旧い共有の鍵。
    private static IConfiguration Config(
        string mode, string? signingKey = null, IEnumerable<string>? verify = null, IEnumerable<(string Issuer, string Key)>? legacy = null,
        bool underscore = false)
    {
        var values = new Dictionary<string, string?> { ["MessageOrigin:Mode"] = mode };
        if (signingKey is not null)
            values["MessageOrigin:SigningKey"] = signingKey;
        foreach (var issuer in verify ?? [])
            values[$"MessageOrigin:VerifyKeys:{(underscore ? issuer.Replace('-', '_') : issuer)}"] = MessageOriginTestKeys.For(issuer).Public;
        foreach (var (issuer, key) in legacy ?? [])
            values[$"MessageOrigin:Keys:{issuer}"] = key;
        return new ConfigurationBuilder().AddInMemoryCollection(values).Build();
    }

    // 発注執行の役（自分の秘密の鍵＋確かめる発行元の公開の鍵）。
    private static IConfiguration ReceiverConfig(string mode) => Config(
        mode,
        MessageOriginTestKeys.For(MessageOriginCatalog.OrderExecution).Private,
        MessageOriginWiringExtensions.RequiredVerifyKeys(MessageOriginCatalog.OrderExecution));

    // そのサービスに要る鍵を揃えた構成。
    private static IConfiguration FullConfig(string serviceName, string mode, bool underscore = false)
    {
        var issuer = MessageOriginCatalog.IssuerIdFor(serviceName);
        return Config(
            mode,
            MessageOriginWiringExtensions.RequiresSigningKey(issuer) ? MessageOriginTestKeys.For(issuer).Private : null,
            MessageOriginWiringExtensions.RequiredVerifyKeys(issuer),
            underscore: underscore);
    }

    private static Task<IHost> HostAsync(string serviceName, IConfiguration config) =>
        Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.PinDynamicTypeLoadMode();
                opts.Discovery.IncludeType<MessageOriginProbeHandler>();
                opts.UseAiStockTradingMessageOrigin(serviceName, config);
                opts.StubAllExternalTransports();
            })
            .StartAsync();

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
    private static DeliveryOptions SignedBy(string issuer, MessageOriginTestKeys.Pair pair, object message, MessageOriginLegacyHmac? legacy = null)
    {
        var envelope = new Envelope(message);
        new MessageOriginSigningRule(issuer, MessageOriginTestKeys.Signer(issuer, pair), TimeProvider.System, legacy).Modify(envelope);
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
        var signed = SignedBy(MessageOriginCatalog.MarketMonitor, MessageOriginTestKeys.For(MessageOriginCatalog.MarketMonitor), message);

        await InvokeAsync(host, message, signed);
        await InvokeAsync(host, message, signed);

        MessageOriginProbeHandler.Handled.Should().ContainSingle();
        await host.StopAsync();
    }

    [Fact]
    public async Task 段2の受け手は許されない発行元の正しい印も渡さない()
    {
        // 発注執行の秘密の鍵で正しく作った署名でも、損切り到達の発行元（市場監視）ではない。
        MessageOriginProbeHandler.Reset();
        using var host = await ReceiverAsync(ReceiverConfig("Enforce"));
        var message = Trigger();

        await InvokeAsync(host, message, SignedBy(MessageOriginCatalog.OrderExecution, MessageOriginTestKeys.For(MessageOriginCatalog.OrderExecution), message));

        MessageOriginProbeHandler.Handled.Should().BeEmpty();
        await host.StopAsync();
    }

    [Fact]
    public async Task 段2の受け手は本文を改竄したメッセージを渡さない()
    {
        MessageOriginProbeHandler.Reset();
        using var host = await ReceiverAsync(ReceiverConfig("Enforce"));
        var signed = SignedBy(MessageOriginCatalog.MarketMonitor, MessageOriginTestKeys.For(MessageOriginCatalog.MarketMonitor), Trigger());

        await InvokeAsync(host, MessageOriginSamples.Tampered(typeof(StopLossTriggered)), signed);

        MessageOriginProbeHandler.Handled.Should().BeEmpty();
        await host.StopAsync();
    }

    // 受け手の Pod が持つのは公開の鍵だけ。別の鍵の組（漏れた受け手が作れる最良のもの）で署名しても通らない。
    [Fact]
    public async Task 段2の受け手は許された発行元を名乗る別の鍵の署名を渡さない()
    {
        MessageOriginProbeHandler.Reset();
        using var host = await ReceiverAsync(ReceiverConfig("Enforce"));
        var message = Trigger();

        await InvokeAsync(host, message, SignedBy(MessageOriginCatalog.MarketMonitor, MessageOriginTestKeys.NewPair(), message));

        MessageOriginProbeHandler.Handled.Should().BeEmpty();
        await host.StopAsync();
    }

    // 🔴 IADR-0633（2026-11-07 追記）: 鍵の入れ替えの間は、前の公開の鍵（MessageOrigin__PreviousVerifyKeys__<発行元>。chart の previousVerify）で
    // 入れ替えの前に署名されたメッセージも処理する。前の鍵を外した後は処理しない（段 2）。
    [Fact]
    public async Task 入れ替えの間は前の公開の鍵の署名を処理し外した後は渡さない()
    {
        var oldPair = MessageOriginTestKeys.NewPair();
        var message = Trigger();
        var signedWithOld = SignedBy(MessageOriginCatalog.MarketMonitor, oldPair, message);
        IConfiguration WithPrevious(bool previous)
        {
            var values = new Dictionary<string, string?>
            {
                ["MessageOrigin:Mode"] = "Enforce",
                ["MessageOrigin:SigningKey"] = MessageOriginTestKeys.For(MessageOriginCatalog.OrderExecution).Private,
                // 入れ替えの後の新しい公開の鍵（MessageOriginTestKeys の組）と、chart の env の書き方（- は _）の前の鍵。
                ["MessageOrigin:VerifyKeys:market_monitor"] = MessageOriginTestKeys.For(MessageOriginCatalog.MarketMonitor).Public,
                ["MessageOrigin:VerifyKeys:risk_management"] = MessageOriginTestKeys.For(MessageOriginCatalog.RiskManagement).Public,
            };
            if (previous)
                values["MessageOrigin:PreviousVerifyKeys:market_monitor"] = oldPair.Public;
            return new ConfigurationBuilder().AddInMemoryCollection(values).Build();
        }

        MessageOriginProbeHandler.Reset();
        using (var rotating = await HostAsync(OrderExecution, WithPrevious(previous: true)))
        {
            await InvokeAsync(rotating, message, signedWithOld);
            await InvokeAsync(rotating, message, SignedBy(MessageOriginCatalog.MarketMonitor, MessageOriginTestKeys.For(MessageOriginCatalog.MarketMonitor), message));
            MessageOriginProbeHandler.Handled.Should().HaveCount(2, "前の鍵の署名も新しい鍵の署名も処理する");
            await rotating.StopAsync();
        }

        MessageOriginProbeHandler.Reset();
        using var after = await HostAsync(OrderExecution, WithPrevious(previous: false));
        await InvokeAsync(after, message, SignedBy(MessageOriginCatalog.MarketMonitor, oldPair, message));
        MessageOriginProbeHandler.Handled.Should().BeEmpty("前の鍵を外した後は、前の鍵の署名を処理しない");
        await after.StopAsync();
    }

    // 🔴 IADR-0633 決定 4: 旧い HMAC の印だけのメッセージは、段 1 では（記録して）処理し、段 2 では渡さない。
    [Fact]
    public async Task 旧い印だけのメッセージは段1で処理され段2で渡されない()
    {
        var legacyKey = MessageOriginTestKeys.LegacyKey();
        var legacy = MessageOriginLegacyHmac.Create(new Dictionary<string, string?> { [MessageOriginCatalog.MarketMonitor] = legacyKey });
        var envelope = new Envelope(Trigger());
        new MessageOriginSigningRule(MessageOriginCatalog.MarketMonitor, MessageOriginSignature.Create(null), TimeProvider.System, legacy)
            .Modify(envelope);
        var options = new DeliveryOptions();
        foreach (var (k, v) in envelope.Headers)
            options.WithHeader(k, v!);

        MessageOriginProbeHandler.Reset();
        using (var report = await HostAsync(OrderExecution, Config(
                   "Report", verify: [MessageOriginCatalog.MarketMonitor], legacy: [(MessageOriginCatalog.MarketMonitor, legacyKey)])))
        {
            await InvokeAsync(report, envelope.Message!, options);
            MessageOriginProbeHandler.Handled.Should().ContainSingle();
            await report.StopAsync();
        }

        MessageOriginProbeHandler.Reset();
        using var enforce = await ReceiverAsync(ReceiverConfig("Enforce"));
        await InvokeAsync(enforce, envelope.Message!, options);
        MessageOriginProbeHandler.Handled.Should().BeEmpty();
        await enforce.StopAsync();
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
                opts.UseAiStockTradingMessageOrigin(MarketMonitor, FullConfig(MarketMonitor, "Enforce"));
                opts.StubAllExternalTransports();
            })
            .StartAsync();
        var message = Trigger();

        var session = await host.TrackActivityForTest().PublishMessageAndWaitAsync(message);

        var sent = session.Sent.Envelopes().Where(e => e.Message is StopLossTriggered).ToList();
        sent.Should().NotBeEmpty();
        var verifier = new MessageOriginVerifier(
            MessageOriginMode.Enforce,
            MessageOriginSignature.Create(new Dictionary<string, string?>
            {
                [MessageOriginCatalog.MarketMonitor] = MessageOriginTestKeys.For(MessageOriginCatalog.MarketMonitor).Public,
            }),
            TimeSpan.FromHours(24), TimeSpan.FromMinutes(5), TimeProvider.System,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<MessageOriginVerifier>.Instance);
        sent.Should().AllSatisfy(e =>
        {
            e.Headers[MessageOriginSignature.IssuerHeader].Should().Be(MessageOriginCatalog.MarketMonitor);
            e.Headers.Should().NotContainKey(MessageOriginLegacyHmac.SignatureHeader, "段 2 では旧い印を付けない");
            verifier.Check(e).Should().Be(MessageOriginCheck.Valid);
        });
        await host.StopAsync();
    }

    // 🔴 鍵が無いまま段 2 で起動すると止まる（fail-closed）。自分の秘密の鍵・確かめる発行元の公開の鍵のどれが欠けても止まる。
    [Theory]
    [MemberData(nameof(Services))]
    public async Task 段2で要る鍵が1つでも無ければ起動で止まる(string serviceName)
    {
        var issuerId = MessageOriginCatalog.IssuerIdFor(serviceName);
        var signs = MessageOriginWiringExtensions.RequiresSigningKey(issuerId);
        var verify = MessageOriginWiringExtensions.RequiredVerifyKeys(issuerId);
        (signs || verify.Count > 0).Should().BeTrue();
        var signing = signs ? MessageOriginTestKeys.For(issuerId).Private : null;

        if (signs)
        {
            var act = async () => { using var host = await HostAsync(serviceName, Config("Enforce", null, verify)); };
            (await act.Should().ThrowAsync<InvalidOperationException>()).Which.Message.Should().Contain("MessageOrigin:SigningKey");
        }

        foreach (var absent in verify)
        {
            var act = async () => { using var host = await HostAsync(serviceName, Config("Enforce", signing, verify.Where(k => k != absent))); };
            (await act.Should().ThrowAsync<InvalidOperationException>()).Which.Message.Should().Contain($"MessageOrigin:VerifyKeys:{absent}");
        }
    }

    [Theory]
    [MemberData(nameof(Services))]
    public async Task 段2で要る鍵が揃えば起動する(string serviceName)
    {
        using var host = await HostAsync(serviceName, FullConfig(serviceName, "Enforce"));
        await host.StopAsync();
    }

    // 🔴 段 2 では旧い共有の鍵を構成しただけで起動を止める（受け手に共有の鍵を持たせない）。
    [Theory]
    [MemberData(nameof(Services))]
    public async Task 段2で旧い共有の鍵があれば起動で止まる(string serviceName)
    {
        var full = FullConfig(serviceName, "Enforce");
        var withLegacy = new ConfigurationBuilder().AddConfiguration(full)
            .AddInMemoryCollection(new Dictionary<string, string?> { [$"MessageOrigin:Keys:{MessageOriginCatalog.AllIssuers[0]}"] = MessageOriginTestKeys.LegacyKey() })
            .Build();

        var act = async () => { using var host = await HostAsync(serviceName, withLegacy); };

        (await act.Should().ThrowAsync<InvalidOperationException>()).Which.Message.Should().Contain("旧い共有の鍵");
    }

    // 発行しないサービスに秘密の鍵を置かない（段に依らず止める）。
    [Theory]
    [InlineData("ai-stock-trading.cost-control-service")]
    [InlineData("ai-stock-trading.audit-service")]
    [InlineData("ai-stock-trading.notification-service")]
    public async Task 発行しないサービスに秘密の鍵があれば起動で止まる(string serviceName)
    {
        var act = async () => { using var host = await HostAsync(serviceName, Config("Report", MessageOriginTestKeys.NewPair().Private)); };

        (await act.Should().ThrowAsync<InvalidOperationException>()).Which.Message.Should().Contain("MessageOrigin:SigningKey");
    }

    // 記録だけの受け手（監査）には旧い共有の鍵を渡さない（共有の鍵から全発行元を名乗れる）。段 1 でも止める。
    [Fact]
    public async Task 監査に旧い共有の鍵があれば段1でも起動で止まる()
    {
        var act = async () =>
        {
            using var host = await HostAsync(
                "ai-stock-trading.audit-service", Config("Report", legacy: [(MessageOriginCatalog.TradeDecision, MessageOriginTestKeys.LegacyKey())]));
        };

        (await act.Should().ThrowAsync<InvalidOperationException>()).Which.Message.Should().Contain("記録だけの受け手");
    }

    // chart は env の名前をシェルの識別子にするため、発行元の識別子の - を _ で書く（MessageOrigin__VerifyKeys__trade_decision）。
    [Fact]
    public async Task 鍵の名前の下線は発行元の識別子のハイフンとして読む()
    {
        const string risk = "ai-stock-trading.risk-management-service";
        using var host = await HostAsync(risk, FullConfig(risk, "Enforce", underscore: true));
        await host.StopAsync();
    }

    [Fact]
    public async Task 段1は鍵が無くても起動する()
    {
        using var host = await HostAsync(OrderExecution, Config("Report"));
        await host.StopAsync();
    }

    // 🔴 IADR-0633 決定 5: 監査は記録だけの受け手。段 2 でも、署名の無いメッセージを処理する（証跡を欠かさない）。
    [Fact]
    public async Task 監査は段2でも署名の無い表の型を処理する()
    {
        AuditProbeHandler.Reset();
        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.PinDynamicTypeLoadMode();
                opts.Discovery.DisableConventionalDiscovery();
                opts.Discovery.IncludeType<AuditProbeHandler>();
                opts.UseAiStockTradingMessageOrigin("ai-stock-trading.audit-service", FullConfig("ai-stock-trading.audit-service", "Enforce"));
                opts.StubAllExternalTransports();
            })
            .StartAsync();
        host.Services.GetRequiredService<MessageOriginVerifier>().RecordOnly.Should().BeTrue();

        await InvokeAsync(host, MessageOriginSamples.Create(typeof(OrderExecuted)));

        AuditProbeHandler.Handled.Should().ContainSingle();
        await host.StopAsync();
    }

    // 🔴 NFR, FR-09, IADR-0638 決定 2: 通知は記録だけの受け手ではない。段 2 では確かめられない表の型を処理しない（偽の事象を知らせない）、
    //   正しい印は処理する。段 1 は記録して処理する（受け手を足すときの段）。
    // 陰性対照: 表の行から Notification を外すと、段 2 の印の無い行が処理されて赤になる。
    [Theory]
    [InlineData("Enforce", false)]
    [InlineData("Report", true)]
    public async Task 通知は段2で印の無い表の型を処理せず正しい印は処理する(string mode, bool unsignedHandled)
    {
        const string notification = "ai-stock-trading.notification-service";
        AuditProbeHandler.Reset();
        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.PinDynamicTypeLoadMode();
                opts.Discovery.DisableConventionalDiscovery();
                opts.Discovery.IncludeType<AuditProbeHandler>();
                opts.UseAiStockTradingMessageOrigin(notification, FullConfig(notification, mode));
                opts.StubAllExternalTransports();
            })
            .StartAsync();
        host.Services.GetRequiredService<MessageOriginVerifier>().RecordOnly.Should().BeFalse();

        await InvokeAsync(host, MessageOriginSamples.Create(typeof(OrderExecuted)));
        AuditProbeHandler.Handled.Should().HaveCount(unsignedHandled ? 1 : 0);

        var signed = MessageOriginSamples.Create(typeof(OrderExecuted));
        await InvokeAsync(host, signed, SignedBy(MessageOriginCatalog.OrderExecution, MessageOriginTestKeys.For(MessageOriginCatalog.OrderExecution), signed));
        AuditProbeHandler.Handled.Should().HaveCount(unsignedHandled ? 2 : 1, "正しい印の約定は知らせる");
        await host.StopAsync();
    }

    [Fact]
    public void 知らない段は起動で止まる()
    {
        var act = () => new WolverineOptions().UseAiStockTradingMessageOrigin(OrderExecution, Config("Strict"));

        act.Should().Throw<InvalidOperationException>();
    }

    // 鍵を持つ（発行元か受け手の）サービス。前提条件は表の型を発行も確かめもしない。監査・通知は公開の鍵だけを持つ（IADR-0638 決定 2）。
    public static TheoryData<string> Services() =>
    [
        "ai-stock-trading.audit-service",
        "ai-stock-trading.notification-service",
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
    [InlineData("ai-stock-trading.configuration-service")]
    public void 表の型を発行も確かめもしないサービスは鍵が要らない(string serviceName)
    {
        var issuer = MessageOriginCatalog.IssuerIdFor(serviceName);
        MessageOriginWiringExtensions.RequiresSigningKey(issuer).Should().BeFalse();
        MessageOriginWiringExtensions.RequiredVerifyKeys(issuer).Should().BeEmpty();
    }

    // 🔴 NFR, FR-09, IADR-0638 決定 2: 通知は秘密の鍵を持たず、受ける表の型の発行元（発注執行・市場監視・リスク管理）の公開の鍵だけを要る。
    [Fact]
    public void 通知は秘密の鍵を持たず受ける表の型の発行元の公開の鍵だけを要る()
    {
        MessageOriginWiringExtensions.RequiresSigningKey(MessageOriginCatalog.Notification).Should().BeFalse();
        MessageOriginWiringExtensions.RequiredVerifyKeys(MessageOriginCatalog.Notification).Should().BeEquivalentTo(
            [MessageOriginCatalog.MarketMonitor, MessageOriginCatalog.OrderExecution, MessageOriginCatalog.RiskManagement]);
        MessageOriginCatalog.IsRecordOnly(MessageOriginCatalog.Notification).Should().BeFalse();
    }

    [Fact]
    public void 監査は秘密の鍵を持たず表の発行元すべての公開の鍵だけを要る()
    {
        MessageOriginWiringExtensions.RequiresSigningKey("audit").Should().BeFalse();
        MessageOriginWiringExtensions.RequiredVerifyKeys("audit").Should().BeEquivalentTo(MessageOriginCatalog.AllIssuers);
    }
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

// 試験用の監査の役（記録だけの受け手）。
public sealed class AuditProbeHandler
{
    private static readonly ConcurrentQueue<object> HandledQueue = new();

    public static IReadOnlyCollection<object> Handled => HandledQueue;

    public static void Reset() => HandledQueue.Clear();

    public void Handle(OrderExecuted message) => HandledQueue.Enqueue(message);
}

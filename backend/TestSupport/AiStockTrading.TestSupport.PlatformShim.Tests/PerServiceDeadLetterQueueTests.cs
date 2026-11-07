using System.Text.RegularExpressions;
using AiStockTrading.TestSupport.PlatformShim.Foundation.Extensions;
using AwesomeAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Wolverine;
using Wolverine.Configuration;
using Wolverine.RabbitMQ;
using Wolverine.RabbitMQ.Internal;
using Wolverine.Runtime;
using Wolverine.Runtime.Routing;
using Xunit;

namespace AiStockTrading.TestSupport.PlatformShim.Tests;

// 🔴 NFR-18, IADR-0636: **全サービスのデッドレター（受信キューの DLQ と transport の既定の DLQ）は、そのサービスの接頭辞の中にある。**
//
// Wolverine は transport の既定の DLQ（既定の名前は全サービスで同じ wolverine-dead-letter-queue）を起動のたびに宣言する
// （キュー・同名の exchange・束ね。RabbitMqTransport の enabledDeadLetterQueues。WolverineFx.RabbitMQ 6.24.5 を逆コンパイルして確かめた）。
// 共有の名前のままだと、ブローカの権限で全サービスに同じ 1 本の configure・write・read を与えることになる。共通配線はこれを
// <ServiceName>.dead-letter にし、受信キューの DLQ は <queue>_error のまま（どちらも <ServiceName>. で始まる）。
//
// 実ブローカには繋がない（SubscribedExchangeDeclarationTests と同じく StubAllExternalTransports ＋ 起動と同じ発見の呼び出し）。
// 実ブローカで「共有の名前の権限が無い利用者でも起動し、失敗したメッセージが自分の DLQ へ移る」ことは
// Integration E2E（BrokerPerServiceDeadLetterE2ETests）が確かめる。
public sealed partial class PerServiceDeadLetterQueueTests
{
    private const string Shared = RabbitMqTransport.DeadLetterQueueName;

    private static string RepoRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "backend", "backend.slnx"))) return dir.FullName;
        }

        throw new InvalidOperationException("リポジトリの根（backend/backend.slnx）が見つからない。");
    }

    [GeneratedRegex(@"^const string ServiceName = ""([^""]+)"";", RegexOptions.Multiline)]
    private static partial Regex ServiceNameConst();

    [GeneratedRegex(@"\.UseAiStockTradingRabbitMq\(\s*ServiceName\b")]
    private static partial Regex WiredWithServiceName();

    /// <summary>各サービスの Program.cs（サービス名の定数と共通配線の呼び出し）。</summary>
    private static List<(string Dir, string ServiceName, string Code)> Services()
    {
        var root = Path.Combine(RepoRoot(), "backend", "Services");
        var found = new List<(string, string, string)>();
        foreach (var program in Directory.GetFiles(root, "Program.cs", SearchOption.AllDirectories))
        {
            var rel = Path.GetRelativePath(root, program).Replace('\\', '/');
            // サービスの根の Program.cs だけ（新樹形 <Svc>/Program.cs・旧樹形 <Svc>/src/<Svc>.Api/Program.cs）。試験の下は読まない。
            if (rel.Contains("/Tests/", StringComparison.Ordinal) || rel.Contains("/tests/", StringComparison.Ordinal)) continue;
            var code = File.ReadAllText(program);
            if (!code.Contains("UseWolverine", StringComparison.Ordinal)) continue;
            var m = ServiceNameConst().Match(code);
            m.Success.Should().BeTrue($"{rel} は Wolverine を配線するのにサービス名の定数（const string ServiceName）が無い");
            found.Add((rel, m.Groups[1].Value, code));
        }

        return found;
    }

    public static TheoryData<string> ServiceNames()
    {
        var data = new TheoryData<string>();
        foreach (var (_, name, _) in Services()) data.Add(name);
        return data;
    }

    private static async Task<(IHost Host, RabbitMqTransport Transport)> StartAsync(Action<WolverineOptions> wire)
    {
        var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.PinDynamicTypeLoadMode();
                wire(opts);
                opts.StubAllExternalTransports();
            })
            .StartAsync();
        var runtime = host.Services.GetRequiredService<IWolverineRuntime>();

        // stub でない起動（WolverineRuntime.discoverListenersFromConventions）と同じ呼び出し（SubscribedExchangeDeclarationTests と同じ）。
        var property = typeof(WolverineOptions).GetProperty(
            "RoutingConventions", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("WolverineOptions.RoutingConventions が見つからない（Wolverine の版で変わった）。");
        var conventions = ((IEnumerable<IMessageRoutingConvention>)property.GetValue(runtime.Options)!).ToList();
        conventions.Should().NotBeEmpty("規約ルーティングを 1 つ登録する");
        IReadOnlyList<Type> handled = [typeof(BscSubscribedProbeEvent)];
        foreach (var convention in conventions) convention.DiscoverListeners(runtime, handled);
        foreach (var convention in conventions) convention.PreregisterSenders(handled, runtime);
        return (host, runtime.Options.Transports.GetOrCreate<RabbitMqTransport>());
    }

    private static async Task StopAsync(IHost host)
    {
        await host.StopAsync();
        host.Dispose();
    }

    /// <summary>
    /// 起動で宣言されるデッドレター（Wolverine の enabledDeadLetterQueues と同じ選び方: transport の既定の DLQ と、受信の役のキューの DLQ。
    /// どちらも WolverineStorage 以外）の、キュー・exchange・束ねの名前。
    /// </summary>
    private static List<string> DeclaredDeadLetterNames(RabbitMqTransport transport)
    {
        var dlqs = new List<DeadLetterQueue>();
        if (transport.DeadLetterQueue.Mode != DeadLetterQueueMode.WolverineStorage) dlqs.Add(transport.DeadLetterQueue);
        dlqs.AddRange(transport.Queues
            .Where(q => q.IsDurable && q.Role == EndpointRole.Application && q.DeadLetterQueue is { Mode: not DeadLetterQueueMode.WolverineStorage })
            .Select(q => q.DeadLetterQueue!));
        return [.. dlqs.SelectMany(d => new[] { d.QueueName, d.ExchangeName, d.BindingName ?? d.QueueName }).Distinct()];
    }

    /// <summary>接頭辞（ブローカの権限の <c>^&lt;ServiceName&gt;\..*$</c>）の外にあるデッドレターの名前。</summary>
    private static List<string> OutsidePrefix(string serviceName, IEnumerable<string> names)
    {
        var own = new Regex($"^{Regex.Escape(serviceName + ".")}.*$");
        return [.. names.Where(n => !own.IsMatch(n))];
    }

    [Fact]
    public void サービスごとの既定のデッドレターの名前は自分の接頭辞の中にあり共有の名前でない()
    {
        var name = WolverineExtensions.ServiceDeadLetterQueueNameFor("ai-stock-trading.audit-service");

        name.Should().Be("ai-stock-trading.audit-service.dead-letter");
        name.Should().NotBe(Shared);
        OutsidePrefix("ai-stock-trading.audit-service", [name]).Should().BeEmpty();
        OutsidePrefix("ai-stock-trading.report-service", [name]).Should().Equal([name], "他のサービスの接頭辞には入らない");
        // 受信キューの型名（<ServiceName>.<型名>）とは衝突しない（型名にハイフンは使えない）。
        name.Should().NotBe(WolverineExtensions.QueueNameFor("ai-stock-trading.audit-service", typeof(BscSubscribedProbeEvent)));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    public void サービス名が無ければ既定のデッドレターの名前を作らない(string? serviceName)
    {
        var act = () => WolverineExtensions.ServiceDeadLetterQueueNameFor(serviceName!);

        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void 共通配線を使う取引ユニットのサービスは11個で全てがサービス名の定数で配線する()
    {
        var services = Services();

        services.Select(s => s.ServiceName).Should().HaveCount(11, "取引ユニットのサービス（ブローカの利用者）は 11 個")
            .And.OnlyHaveUniqueItems()
            .And.AllSatisfy(n => n.Should().StartWith("ai-stock-trading."));
        services.Should().AllSatisfy(s => WiredWithServiceName().IsMatch(s.Code).Should().BeTrue(
            $"{s.Dir} は共通配線へサービス名の定数を渡す（デッドレターの接頭辞がサービス名から決まる）"));
    }

    [Theory]
    [MemberData(nameof(ServiceNames))]
    public async Task 全サービスのデッドレターは自分の接頭辞の中にあり共有の名前を宣言しない(string serviceName)
    {
        var (host, transport) = await StartAsync(
            opts => opts.UseAiStockTradingRabbitMq(serviceName, "amqp://guest:guest@localhost:5672", SubscribedExchangeDeclaration.Passive));
        try
        {
            var names = DeclaredDeadLetterNames(transport);

            names.Should().Contain(WolverineExtensions.ServiceDeadLetterQueueNameFor(serviceName), "transport の既定の DLQ はサービスごとの名前");
            names.Should().Contain(
                WolverineExtensions.DeadLetterQueueNameFor(WolverineExtensions.QueueNameFor(serviceName, typeof(BscSubscribedProbeEvent))),
                "受信キューの DLQ は <queue>_error のまま");
            names.Should().NotContain(Shared);
            OutsidePrefix(serviceName, names).Should().BeEmpty($"{serviceName} のデッドレターは全て自分の接頭辞の中");

            // 起動の後の transport の構成にも、共有の名前のキュー・exchange は無い（列挙は作らない。添字で引くと作られる）。
            transport.Queues.Select(q => q.QueueName).Should().NotContain(Shared);
            transport.Exchanges.Select(e => e.Name).Should().NotContain(Shared);
        }
        finally
        {
            await StopAsync(host);
        }
    }

    // 陰性対照: 共通配線を通さない Wolverine の既定（同じ規約ルーティング・AutoProvision）では、transport の既定の DLQ が共有の名前になり、
    // 上の検査（接頭辞の外の名前）が拾う。検査が空振りしないことを確かめる。
    [Fact]
    public async Task 陰性対照_共通配線を通さない既定は共有の名前を宣言し検査が拾う()
    {
        const string serviceName = "ai-stock-trading.dlq-negative-control";
        var (host, transport) = await StartAsync(opts =>
        {
            opts.ServiceName = serviceName;
            opts.ApplicationAssembly = typeof(PerServiceDeadLetterQueueTests).Assembly;
            opts.Policies.DisableConventionalLocalRouting();
            opts.UseRabbitMq(new Uri("amqp://guest:guest@localhost:5672"))
                .AutoProvision()
                .UseConventionalRouting(c => c.QueueNameForListener(t => WolverineExtensions.QueueNameFor(serviceName, t)));
        });
        try
        {
            var names = DeclaredDeadLetterNames(transport);

            names.Should().Contain(Shared, "Wolverine の既定の DLQ は全サービスで同じ名前");
            OutsidePrefix(serviceName, names).Should().Contain(Shared, "接頭辞の外の名前として検査が拾う");
        }
        finally
        {
            await StopAsync(host);
        }
    }
}

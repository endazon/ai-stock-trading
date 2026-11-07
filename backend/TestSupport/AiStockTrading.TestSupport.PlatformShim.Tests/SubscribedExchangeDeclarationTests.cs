using System.Text.RegularExpressions;
using AiStockTrading.TestSupport.PlatformShim.Foundation.Extensions;
using AwesomeAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Wolverine;
using Wolverine.RabbitMQ.Internal;
using Wolverine.Runtime;
using Wolverine.Runtime.Routing;
using Xunit;

namespace AiStockTrading.TestSupport.PlatformShim.Tests;

// 🔴 NFR-18, IADR-0628: 購読側の権限を、購読に要るもの（exchange の read と自分のキュー）だけへ絞るための配線を固定する。
//
// Passive では、購読する exchange（自分のキューを束ねる exchange）を宣言しない（受動の宣言＝在るかの確かめだけ）。
// キューの宣言と束ねは続ける。発行だけの exchange・デッドレターの exchange は従来どおり宣言する。
// Declare（既定）は従来の挙動（購読側も宣言する）。切り替えは環境変数 WOLVERINE_SUBSCRIBED_EXCHANGES（chart の
// global.rabbitmq.subscribedExchanges が Passive のときだけ描く）。
//
// 実ブローカには繋がない（StubAllExternalTransports）。Wolverine の構成（transport の exchange とキューの束ね）を読む。
// 🔴 外部の transport を stub にすると、Wolverine は規約ルーティングのリスナーの発見を飛ばす（WolverineRuntime の
//   discoverListenersFromConventions）。stub でないときに起動が呼ぶのと同じ呼び出し（各規約の DiscoverListeners と
//   PreregisterSenders）を、起動の後に試験から呼ぶ（DiscoverAsStartupDoes）。
// 実ブローカで権限を絞った利用者が起動・束ね・受信できることは Integration E2E（BrokerSubscriberLeastPrivilegeE2ETests）が確かめる。
// 購読する型（下のハンドラが受ける）と、発行だけの型（ハンドラが無い）。入れ子にしない
// （本物の契約と同じく、exchange の名前が型の完全名になる形で確かめる）。
public record BscSubscribedProbeEvent(string Id);

public record BscPublishOnlyProbeEvent(string Id);

// Wolverine は public なハンドラ型しか発見しない（WolverineTopologyTests と同じ）。
public class BscSubscribedProbeEventHandler
{
    public void Handle(BscSubscribedProbeEvent message) => _ = message;
}

public partial class SubscribedExchangeDeclarationTests
{
    private const string ServiceName = "ai-stock-trading.bsc-probe-service";

    private static async Task<(IHost Host, RabbitMqTransport Transport)> StartAsync(Action<WolverineOptions> wire)
    {
        var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.PinDynamicTypeLoadMode();
                wire(opts);
                // 実ブローカへ接続しない（ローカル・CI ともに RabbitMQ を要求しない）。
                opts.StubAllExternalTransports();
            })
            .StartAsync();
        var runtime = host.Services.GetRequiredService<IWolverineRuntime>();
        DiscoverAsStartupDoes(runtime);
        return (host, runtime.Options.Transports.GetOrCreate<RabbitMqTransport>());
    }

    // stub でない起動（WolverineRuntime.discoverListenersFromConventions）と同じ呼び出し。規約の一覧は Wolverine の internal なので
    // 反射で読む（名前が変われば例外で赤になる。黙って 0 件へ倒れない）。
    private static void DiscoverAsStartupDoes(IWolverineRuntime runtime)
    {
        var property = typeof(WolverineOptions).GetProperty(
            "RoutingConventions", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("WolverineOptions.RoutingConventions が見つからない（Wolverine の版で変わった）。");
        var conventions = ((IEnumerable<IMessageRoutingConvention>)property.GetValue(runtime.Options)!).ToList();
        conventions.Should().NotBeEmpty("共通配線は規約ルーティングを 1 つ登録する");
        IReadOnlyList<Type> handled = [typeof(BscSubscribedProbeEvent)];
        foreach (var convention in conventions) convention.DiscoverListeners(runtime, handled);
        foreach (var convention in conventions) convention.PreregisterSenders(handled, runtime);
    }

    private static RabbitMqQueue ListenerQueue(RabbitMqTransport transport) =>
        transport.Queues[WolverineExtensions.QueueNameFor(ServiceName, typeof(BscSubscribedProbeEvent))];

    // 購読のキューを束ねる exchange（デッドレターの exchange を除く）。
    private static List<RabbitMqExchange> BoundExchanges(RabbitMqTransport transport) =>
        [.. ListenerQueue(transport).Bindings().Select(b => b.ExchangeName).Distinct().Select(name => transport.Exchanges[name])];

    [Theory]
    [InlineData(null, SubscribedExchangeDeclaration.Declare)]
    [InlineData("", SubscribedExchangeDeclaration.Declare)]
    [InlineData("  ", SubscribedExchangeDeclaration.Declare)]
    [InlineData("Declare", SubscribedExchangeDeclaration.Declare)]
    [InlineData("Passive", SubscribedExchangeDeclaration.Passive)]
    [InlineData(" passive ", SubscribedExchangeDeclaration.Passive)]
    public void 宣言の仕方は環境変数の値から決まり未設定は従来の宣言である(string? configured, SubscribedExchangeDeclaration expected)
    {
        WolverineExtensions.ResolveSubscribedExchangeDeclaration(configured).Should().Be(expected);
    }

    [Theory]
    [InlineData("Pasive")]
    [InlineData("1")]
    [InlineData("true")]
    public void 宣言の仕方の綴りの誤りは起動で止める(string configured)
    {
        var act = () => WolverineExtensions.ResolveSubscribedExchangeDeclaration(configured);

        act.Should().Throw<ArgumentException>().WithMessage($"*{WolverineExtensions.SubscribedExchangeDeclarationVariable}*");
    }

    [Fact]
    public async Task Passiveでは購読するexchangeを宣言せずキューの束ねは続ける()
    {
        var (host, transport) = await StartAsync(
            opts => opts.UseAiStockTradingRabbitMq(ServiceName, "amqp://guest:guest@localhost:5672", SubscribedExchangeDeclaration.Passive));
        try
        {
            var queue = ListenerQueue(transport);
            var exchanges = BoundExchanges(transport);

            exchanges.Select(e => e.Name).Should().Contain(
                typeof(BscSubscribedProbeEvent).FullName!,
                "購読のキューは購読する型の exchange（型の完全名）へ束ね続ける");
            exchanges.Should().AllSatisfy(e => e.DeclarePassive.Should().BeTrue(
                $"購読する exchange {e.Name} は受動で宣言する（configure を要らない）"));

            // デッドレターの exchange（自分のキューの接頭辞の名前）は従来どおり宣言する（自分の名前空間なので configure を持つ）。
            var dlq = queue.DeadLetterQueue;
            dlq.Should().NotBeNull();
            transport.Exchanges[dlq!.ExchangeName].DeclarePassive.Should().BeFalse("デッドレターの exchange は受動にしない");
        }
        finally
        {
            await host.StopAsync();
            host.Dispose();
        }
    }

    [Fact]
    public async Task Passiveでも発行だけのexchangeは宣言する()
    {
        var (host, transport) = await StartAsync(
            opts => opts.UseAiStockTradingRabbitMq(ServiceName, "amqp://guest:guest@localhost:5672", SubscribedExchangeDeclaration.Passive));
        try
        {
            // 発行の経路は 1 通目の発行のときに作られる（遅延）。経路を引いて作らせる。
            var runtime = host.Services.GetRequiredService<IWolverineRuntime>();
            runtime.RoutingFor(typeof(BscPublishOnlyProbeEvent)).Routes.Should().NotBeEmpty();

            transport.Exchanges[typeof(BscPublishOnlyProbeEvent).FullName!].DeclarePassive.Should().BeFalse(
                "発行側は自分の exchange を宣言し続ける");
        }
        finally
        {
            await host.StopAsync();
            host.Dispose();
        }
    }

    [Fact]
    public async Task Declareでは購読するexchangeも従来どおり宣言する()
    {
        var (host, transport) = await StartAsync(
            opts => opts.UseAiStockTradingRabbitMq(ServiceName, "amqp://guest:guest@localhost:5672", SubscribedExchangeDeclaration.Declare));
        try
        {
            var exchanges = BoundExchanges(transport);

            exchanges.Select(e => e.Name).Should().Contain(typeof(BscSubscribedProbeEvent).FullName!);
            exchanges.Should().AllSatisfy(e => e.DeclarePassive.Should().BeFalse($"Declare では {e.Name} を宣言する（従来の挙動）"));
        }
        finally
        {
            await host.StopAsync();
            host.Dispose();
        }
    }

    // 呼び出し側（各サービスの Program.cs）の形は変えない。環境変数で切り替わることを、既定の入口で確かめる。
    // 環境変数はプロセスに 1 つなので、試験の中で戻す（他の試験は宣言の仕方を見ない）。
    [Fact]
    public async Task 既定の入口は環境変数で受動へ切り替わる()
    {
        var previous = Environment.GetEnvironmentVariable(WolverineExtensions.SubscribedExchangeDeclarationVariable);
        Environment.SetEnvironmentVariable(WolverineExtensions.SubscribedExchangeDeclarationVariable, "Passive");
        IHost? host = null;
        try
        {
            (host, var transport) = await StartAsync(
                opts => opts.UseAiStockTradingRabbitMq(ServiceName, "amqp://guest:guest@localhost:5672"));

            BoundExchanges(transport).Should().NotBeEmpty()
                .And.AllSatisfy(e => e.DeclarePassive.Should().BeTrue());
        }
        finally
        {
            Environment.SetEnvironmentVariable(WolverineExtensions.SubscribedExchangeDeclarationVariable, previous);
            if (host is not null)
            {
                await host.StopAsync();
                host.Dispose();
            }
        }
    }

    // 🔴 chart の切り替え: 既定は Declare、values-cloud だけが Passive。テンプレートはコードと同じ環境変数の名前を描く。
    private static string RepoRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "backend", "backend.slnx"))) return dir.FullName;
        }

        throw new InvalidOperationException("リポジトリの根（backend/backend.slnx）が見つからない。");
    }

    private static string Chart(string file) =>
        File.ReadAllText(Path.Combine(RepoRoot(), "deploy", "helm", "ai-stock-trading", file)).Replace("\r\n", "\n");

    [GeneratedRegex(@"^    subscribedExchanges: (\w+)$", RegexOptions.Multiline)]
    private static partial Regex SubscribedExchangesLine();

    [Fact]
    public void chartの切り替えは既定がDeclareでvalues_cloudだけがPassiveである()
    {
        SubscribedExchangesLine().Matches(Chart("values.yaml")).Select(m => m.Groups[1].Value)
            .Should().Equal(["Declare"], "既定は従来の挙動");
        SubscribedExchangesLine().Matches(Chart("values-cloud.yaml")).Select(m => m.Groups[1].Value)
            .Should().Equal(["Passive"], "クラウドは購読の exchange を宣言しない");
        SubscribedExchangesLine().Matches(Chart("values-local.yaml")).Should().BeEmpty("ローカルは既定（Declare）のまま");

        Chart(Path.Combine("templates", "deployment.yaml"))
            .Should().Contain($"- name: {WolverineExtensions.SubscribedExchangeDeclarationVariable}\n              value: \"Passive\"");
    }
}

using System.Net.Security;
using AiStockTrading.TestSupport.PlatformShim.Foundation.Extensions;
using AwesomeAssertions;
using RabbitMQ.Client;
using Wolverine;
using Wolverine.RabbitMQ;
using Wolverine.RabbitMQ.Internal;
using Xunit;

namespace AiStockTrading.TestSupport.PlatformShim.Tests;

// NFR, NFR-18, #1535, IADR-0621 決定4: ブローカへの amqps は、サーバ証明書の CA と**名前**の両方を検証する。
// RabbitMQ.Client は amqps の URI を受けると、名前の不一致を既定で受け入れる（基盤の IADR-0591 決定 5 の実測）。
// 共通の配線（UseAiStockTradingRabbitMq）がこれを空に戻し、ServerName を URI の host にすることを固定する。
public class RabbitMqTlsNameVerificationTests
{
    private static ConnectionFactory FactoryOf(WolverineOptions options) =>
        options.Transports.OfType<RabbitMqTransport>().Single().ConnectionFactory
        ?? throw new InvalidOperationException("RabbitMqTransport.ConnectionFactory が null です（Wolverine の版更新で観測点が変わった可能性があります）。");

    [Fact]
    public void 前提_素のUseRabbitMqはamqpsで名前の不一致を受け入れる()
    {
        // 変異が当たることの前提。既定が既に None なら下の試験は何も証明しない。
        var options = new WolverineOptions();
        options.UseRabbitMq(new Uri("amqps://ast-report:x@rabbitmq:5671"));

        FactoryOf(options).Ssl.AcceptablePolicyErrors.Should().Be(SslPolicyErrors.RemoteCertificateNameMismatch);
    }

    [Fact]
    public void 共通の配線はamqpsで受け入れてよい誤りを空にし名前を照合する()
    {
        var options = new WolverineOptions();
        options.UseAiStockTradingRabbitMq("ai-stock-trading.report-service", "amqps://ast-report:x@rabbitmq:5671");

        var factory = FactoryOf(options);
        factory.Ssl.Enabled.Should().BeTrue();
        factory.Ssl.AcceptablePolicyErrors.Should().Be(SslPolicyErrors.None);
        factory.Ssl.ServerName.Should().Be("rabbitmq");
        factory.Port.Should().Be(5671);
        factory.UserName.Should().Be("ast-report");
    }

    [Fact]
    public void 共通の配線は平文のamqpではTLSを使わず接続先は従来と同じ()
    {
        var plain = new WolverineOptions();
        plain.UseRabbitMq(new Uri("amqp://ast-report:x@rabbitmq:5672"));
        var options = new WolverineOptions();
        options.UseAiStockTradingRabbitMq("ai-stock-trading.report-service", "amqp://ast-report:x@rabbitmq:5672");

        var a = FactoryOf(plain);
        var b = FactoryOf(options);
        b.Ssl.Enabled.Should().BeFalse();
        b.HostName.Should().Be(a.HostName);
        b.Port.Should().Be(a.Port);
        b.UserName.Should().Be(a.UserName);
        b.Password.Should().Be(a.Password);
        b.VirtualHost.Should().Be(a.VirtualHost);
    }
}

using System.Net;
using AiStockTrading.TradeDecision.Application.Ports;
using AiStockTrading.TradeDecision.Worker.Composable.Adapters;
using FluentAssertions;
using MassTransit;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Xunit;

namespace AiStockTrading.TradeDecision.Worker.Tests;

// #79, FR-04, IADR-0017: LlmGateway:BaseUrl の有無で ILlmCompletionClient が安全既定（プレースホルダ＝常に Hold）/
// 実 egress（Http）に切り替わることを検証する（fail-safe: 未設定なら実 LLM を呼ばない）。
public class LlmCompletionClientSelectionTests
{
    [Fact]
    public void BaseUrl未設定は安全既定のプレースホルダ()
    {
        using var factory = new Factory(llmGatewayBaseUrl: null);
        _ = factory.CreateClient(); // ホスト起動

        using var scope = factory.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<ILlmCompletionClient>().Should().BeOfType<PlaceholderLlmCompletionClient>();
    }

    [Fact]
    public void BaseUrl設定時は_LLM_ゲートウェイを呼ぶ_Http実装()
    {
        using var factory = new Factory(llmGatewayBaseUrl: "http://llm-gateway");
        _ = factory.CreateClient();

        using var scope = factory.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<ILlmCompletionClient>().Should().BeOfType<HttpLlmCompletionClient>();
    }

    // #11, IADR-0061 決定2: タイムアウトは LlmGateway:TimeoutSeconds（秒）。
    // fail-safe: 未設定・不正・非正値は既定 30 秒（＝従来値）。無限待ちや 0 秒には倒さない。
    [Theory]
    [InlineData(null, 30)]      // 未設定
    [InlineData("", 30)]        // 空
    [InlineData("abc", 30)]     // 非数値
    [InlineData("0", 30)]       // 0 秒（即時タイムアウト）は既定へ
    [InlineData("-5", 30)]      // 負値は既定へ
    [InlineData("90", 90)]      // 明示設定は反映
    public void TimeoutSeconds_は不正値を既定30秒へ倒す(string? configured, int expectedSeconds)
    {
        using var factory = new Factory(llmGatewayBaseUrl: "http://llm-gateway", timeoutSeconds: configured);
        _ = factory.CreateClient();

        var http = factory.Services.GetRequiredService<IHttpClientFactory>().CreateClient("llm");
        http.Timeout.Should().Be(TimeSpan.FromSeconds(expectedSeconds));
    }

    // #11, FR-11, IADR-0061 決定1: LlmGateway:LogPrompts の**構成キーが Program.cs の配線を通って**実際に
    // 全量ログの有無を切り替えることを end-to-end で検証する（キー名のタイプミス・既定値の反転を検出する）。
    // ILogger<HttpLlmCompletionClient> を差し替えて実際の出力を捕捉し、実 egress は stub ハンドラで受ける。
    [Theory]
    [InlineData("true", true)]   // 明示有効化で全量ログが出る
    [InlineData("false", false)] // 明示無効化では出ない
    [InlineData(null, false)]    // 未設定（既定）では出ない＝機微を既定でログ基盤へ流さない
    public async Task LogPrompts_構成キーが全量ログの有無を切り替える(string? configured, bool expectLogged)
    {
        var logger = new CapturingLogger();
        using var factory = new Factory(llmGatewayBaseUrl: "http://llm-gateway", logPrompts: configured, logger: logger);
        _ = factory.CreateClient();

        using var scope = factory.Services.CreateScope();
        var client = scope.ServiceProvider.GetRequiredService<ILlmCompletionClient>();
        await client.CompleteAsync("私のプロンプト");

        var log = string.Join("\n", logger.Messages);
        if (expectLogged)
            log.Should().Contain("私のプロンプト");
        else
            log.Should().NotContain("私のプロンプト");
    }

    // 出力されたログ本文を記録するだけの fake（Program.cs の DI へ差し込む）。
    private sealed class CapturingLogger : ILogger<HttpLlmCompletionClient>
    {
        public List<string> Messages { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) => Messages.Add(formatter(state, exception));
    }

    // 実 egress を受ける stub（実ネットワーク不使用）。成功応答を返す。
    private sealed class StubLlmHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    """{"text":"{\"action\":\"Hold\"}","model":"claude","inputTokens":1,"outputTokens":1,"sent":true}"""),
            });
    }

    private sealed class Factory(
        string? llmGatewayBaseUrl,
        string? timeoutSeconds = null,
        string? logPrompts = null,
        ILogger<HttpLlmCompletionClient>? logger = null) : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Testing");
            builder.ConfigureAppConfiguration((_, cfg) =>
            {
                var settings = new Dictionary<string, string?>
                {
                    ["RabbitMq:ConnectionString"] = "amqp://localhost",
                    ["Otlp:Endpoint"] = "http://localhost:4317",
                };
                if (llmGatewayBaseUrl is not null)
                    settings["LlmGateway:BaseUrl"] = llmGatewayBaseUrl;
                if (timeoutSeconds is not null)
                    settings["LlmGateway:TimeoutSeconds"] = timeoutSeconds;
                if (logPrompts is not null)
                    settings["LlmGateway:LogPrompts"] = logPrompts;
                cfg.AddInMemoryCollection(settings);
            });
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<IBusControl>();
                services.AddMassTransitTestHarness(x => x.AddConsumer<Composable.Steps.PriceMovementDetectedConsumer>());

                if (logger is not null)
                {
                    // Program.cs は ILogger<HttpLlmCompletionClient> を DI から解決するため、閉じた型で上書きする。
                    services.AddSingleton(logger);
                    // 実 LLM へ出ないよう名前付きクライアント "llm" の一次ハンドラを stub に差し替える。
                    services.AddHttpClient("llm").ConfigurePrimaryHttpMessageHandler(() => new StubLlmHandler());
                }
            });
        }
    }
}

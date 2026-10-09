using System.Collections.Concurrent;
using AiStockTrading.Shared.Contracts.Llm;
using AiStockTrading.Shared.Infrastructure.Composable.Llm;
using AwesomeAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace ReportService.Tests;

// NFR（費用）, FR-04, #817, IADR-0122（2026-09-17 追記）: LLM ゲートウェイ（REST の BaseUrl か gRPC のアドレス）が
// 構成されているのに単価が実質 0（モデル別の表が空 かつ 従来キーも無い）なら、起動時に WARNING を出す。
// 稼働では env 名のハイフンがイメージの `sh -c` 起動で落ち、表が空のまま**無音で**全呼び出しが 0 円計上になっていた。
// 例外は投げない（IADR-0055: 0 は無害な fail-safe のまま。目的は可視化）——ただし配備（Production）では起動しない
// （NFR-13, #1197, IADR-0499。下の T-10-2371 / T-10-2372）。警告の試験は試験の環境名（Testing）＝配備でない環境で回る。
public class LlmPricingStartupWarningTests(ReportWorkerWebApplicationFactory factory)
    : IClassFixture<ReportWorkerWebApplicationFactory>
{
    private const string Marker = "LLM 単価が未設定";

    private IReadOnlyCollection<string> StartAndCaptureWarnings(
        IDictionary<string, string?> settings, string? environment = null)
    {
        var logs = new CapturingLoggerProvider();
        using var configured = factory.WithWebHostBuilder(b =>
        {
            if (environment is not null)
                b.UseEnvironment(environment);
            foreach (var (key, value) in settings)
                b.UseSetting(key, value);
            // Program.cs は AddSerilog で ILoggerFactory を差し替えるため、ConfigureLogging の provider には届かない。
            // 捕捉用の ILoggerFactory を後勝ちで登録する（app.Logger も ILoggerFactory から作られる）。
            b.ConfigureServices(services => services.AddSingleton<ILoggerFactory>(new LoggerFactory([logs])));
        });

        _ = configured.Services;
        return logs.Warnings;
    }

    [Fact]
    public void BaseUrl_構成ありで単価が無ければ警告する()
    {
        StartAndCaptureWarnings(new Dictionary<string, string?> { ["LlmGateway:BaseUrl"] = "http://llm-gateway" })
            .Should().Contain(m => m.Contains(Marker));
    }

    [Fact]
    public void Grpc_構成ありで単価が無ければ警告する()
    {
        StartAndCaptureWarnings(new Dictionary<string, string?> { [LlmGatewayGrpc.AddressKey] = "http://llmgateway-service:8081" })
            .Should().Contain(m => m.Contains(Marker));
    }

    // env 名のアンダースコア形（#817 以後の values-local.yaml）で単価があれば警告しない。
    [Fact]
    public void アンダースコア形の単価があれば警告しない()
    {
        StartAndCaptureWarnings(new Dictionary<string, string?>
        {
            ["LlmGateway:BaseUrl"] = "http://llm-gateway",
            ["LlmPricing:PerModel:claude_opus_5_5:InputPer1kTokens"] = "0.819",
            ["LlmPricing:PerModel:claude_opus_5_5:OutputPer1kTokens"] = "4.093",
        }).Should().NotContain(m => m.Contains(Marker));
    }

    // ゲートウェイ未構成（プレースホルダ散文＝LLM を呼ばない）なら 0 円に実害が無いので警告しない。
    [Fact]
    public void ゲートウェイ未構成なら警告しない()
    {
        StartAndCaptureWarnings(new Dictionary<string, string?>())
            .Should().NotContain(m => m.Contains(Marker));
    }

    // T-10-2371（NFR-13, #1197, IADR-0499）: 配備（Production）でゲートウェイ（REST / gRPC）が構成されているのに単価が実質 0 なら
    // 起動しない（警告に留めない）。report の単価表は trade-decision と独立に構成される（IADR-0296）ので、こちらも止める。
    [Theory]
    [InlineData("LlmGateway:BaseUrl", "http://llm-gateway")]
    [InlineData(LlmGatewayGrpc.AddressKey, "http://llmgateway-service:8081")]
    public void 配備でゲートウェイ構成ありで単価が無ければ起動しない(string key, string value)
    {
        var start = () => StartAndCaptureWarnings(new Dictionary<string, string?> { [key] = value }, "Production");

        start.Should().Throw<InvalidOperationException>()
            .Which.Message.Should().Contain(LlmPricingStartupGuard.Marker);
    }

    // T-10-2372（NFR-13, #1197, IADR-0499）: 配備でも単価があれば起動する・ゲートウェイ未構成（本番既定 values.yaml）なら起動する。
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void 配備でも単価があるかゲートウェイ未構成なら起動する(bool gatewayWithPrices)
    {
        var settings = gatewayWithPrices
            ? new Dictionary<string, string?>
            {
                ["LlmGateway:BaseUrl"] = "http://llm-gateway",
                ["LlmPricing:PerModel:claude_opus_5_5:InputPer1kTokens"] = "0.819",
                ["LlmPricing:PerModel:claude_opus_5_5:OutputPer1kTokens"] = "4.093",
            }
            : new Dictionary<string, string?>();

        StartAndCaptureWarnings(settings, "Production").Should().NotContain(m => m.Contains(Marker));
    }

    private sealed class CapturingLoggerProvider : ILoggerProvider
    {
        private readonly ConcurrentQueue<string> _warnings = new();

        public IReadOnlyCollection<string> Warnings => _warnings.ToArray();

        public ILogger CreateLogger(string categoryName) => new CapturingLogger(_warnings);

        public void Dispose() { }

        private sealed class CapturingLogger(ConcurrentQueue<string> warnings) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
                Func<TState, Exception?, string> formatter)
            {
                if (logLevel == LogLevel.Warning)
                    warnings.Enqueue(formatter(state, exception));
            }
        }
    }
}

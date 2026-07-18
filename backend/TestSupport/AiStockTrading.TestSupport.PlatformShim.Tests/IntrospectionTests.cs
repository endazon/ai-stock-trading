using System.Text.Json;
using AiStockTrading.TestSupport.PlatformShim.Foundation.Introspection;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace AiStockTrading.TestSupport.PlatformShim.Tests;

// ADR-0001, #22 受け入れ基準③: 自己申告（実効構成）の組み立てと fail-safe を検証する。
public class IntrospectionTests
{
    private const string PipelineJson = """
    {
      "version": 1,
      "events": ["A", "B", "C"],
      "steps": [
        { "name": "decide", "service": "trade-decision-service", "input": "A", "outputs": ["B"], "enabled": true },
        { "name": "risk", "service": "risk-management-service", "input": "B", "outputs": ["C"] },
        { "name": "disabled-step", "service": "trade-decision-service", "input": "A", "outputs": [], "enabled": false }
      ]
    }
    """;

    [Fact]
    public void ServiceName定数はpipelineのserviceキーへ写像される()
    {
        IntrospectionExtensions.ToPipelineServiceKey("ai-stock-trading.trade-decision-service")
            .Should().Be("trade-decision-service");
        // 接頭辞が無ければそのまま返す（フォールバック）。
        IntrospectionExtensions.ToPipelineServiceKey("custom-service").Should().Be("custom-service");
    }

    [Fact]
    public void ParseStepsForServiceはservice一致の段のみ返しenabled既定はtrue()
    {
        using var doc = JsonDocument.Parse(PipelineJson);
        var steps = PipelineDeclaration.ParseStepsForService(doc.RootElement, "trade-decision-service");

        steps.Should().HaveCount(2);
        steps.Should().ContainSingle(s => s.Name == "decide")
            .Which.Outputs.Should().ContainSingle().Which.Should().Be("B");
        steps.Single(s => s.Name == "disabled-step").Enabled.Should().BeFalse();
        steps.Single(s => s.Name == "decide").Enabled.Should().BeTrue();
    }

    [Fact]
    public void ParseStepsForServiceはenabled未指定をtrueとして扱う()
    {
        using var doc = JsonDocument.Parse(PipelineJson);
        var steps = PipelineDeclaration.ParseStepsForService(doc.RootElement, "risk-management-service");

        steps.Should().ContainSingle().Which.Enabled.Should().BeTrue();
    }

    [Fact]
    public void LoadStepsForServiceはパス未設定なら空へ縮退する()
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection().Build();
        PipelineDeclaration.LoadStepsForService(config, "trade-decision-service").Should().BeEmpty();
    }

    [Fact]
    public void LoadStepsForServiceはファイル不在なら空へ縮退する()
    {
        var config = Config(new() { [PipelineDeclaration.ConfigPathKey] = "no-such-file.json" });
        PipelineDeclaration.LoadStepsForService(config, "trade-decision-service").Should().BeEmpty();
    }

    [Fact]
    public void LoadStepsForServiceは不正JSONなら空へ縮退する()
    {
        var path = Path.Combine(Path.GetTempPath(), $"pipeline-{Guid.NewGuid():N}.json");
        File.WriteAllText(path, "{ not json");
        try
        {
            var config = Config(new() { [PipelineDeclaration.ConfigPathKey] = path });
            PipelineDeclaration.LoadStepsForService(config, "trade-decision-service").Should().BeEmpty();
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void LoadStepsForServiceは実ファイルの段を読み出す()
    {
        var path = Path.Combine(Path.GetTempPath(), $"pipeline-{Guid.NewGuid():N}.json");
        File.WriteAllText(path, PipelineJson);
        try
        {
            var config = Config(new() { [PipelineDeclaration.ConfigPathKey] = path });
            PipelineDeclaration.LoadStepsForService(config, "trade-decision-service").Should().HaveCount(2);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void AddAiStockTradingIntrospectionは段_ポート_バージョンを組み立てる()
    {
        var path = Path.Combine(Path.GetTempPath(), $"pipeline-{Guid.NewGuid():N}.json");
        File.WriteAllText(path, PipelineJson);
        try
        {
            var config = Config(new()
            {
                [PipelineDeclaration.ConfigPathKey] = path,
                [IntrospectionExtensions.ConfigVersionKey] = "abc123",
            });
            var services = new ServiceCollection();
            services.AddAiStockTradingIntrospection(config, "ai-stock-trading.trade-decision-service",
                b => b.AddPort("llm-completion", "http", "https://llm").AddPort("broker", "paper"));

            var dto = services.BuildServiceProvider().GetRequiredService<ServiceIntrospectionDto>();
            dto.Service.Should().Be("trade-decision-service");
            dto.Steps.Should().HaveCount(2);
            dto.Ports.Should().HaveCount(2);
            dto.ConfigVersion.Should().Be("abc123");
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void AddAiStockTradingIntrospectionはバージョン未注入ならnull_段なしでも申告する()
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection().Build();
        var services = new ServiceCollection();
        services.AddAiStockTradingIntrospection(config, "ai-stock-trading.audit-service");

        var dto = services.BuildServiceProvider().GetRequiredService<ServiceIntrospectionDto>();
        dto.Service.Should().Be("audit-service");
        dto.Steps.Should().BeEmpty();
        dto.Ports.Should().BeEmpty();
        dto.ConfigVersion.Should().BeNull();
    }

    [Fact]
    public void AddPortFromBaseUrlはURL有無で実装を切替える()
    {
        var b = new IntrospectionBuilder();
        b.AddPortFromBaseUrl("a", "https://x", "http", "placeholder");
        b.AddPortFromBaseUrl("b", null, "http", "placeholder");

        b.Ports.Should().SatisfyRespectively(
            p => { p.Port.Should().Be("a"); p.Implementation.Should().Be("http"); p.Target.Should().Be("https://x"); },
            p => { p.Port.Should().Be("b"); p.Implementation.Should().Be("placeholder"); p.Target.Should().BeNull(); });
    }

    private static IConfiguration Config(Dictionary<string, string?> values) =>
        new ConfigurationBuilder().AddInMemoryCollection(values).Build();
}

using System.Text.Json;
using AwesomeAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using ReportService.Domain;
using ReportService.Features.Reports;
using Xunit;

namespace ReportService.Tests;

// 🔴 T-10-1843, FR-07, FR-04, ADR-0048 決定 4, #1118, IADR-0467 決定 7（［2026-10-01 追記］監査 🟡-2）:
// **本番の Program.cs の組み立て**で、方針の改訂 LLM へ送るプロンプトの出来高の行が DecisionVolume:Enabled に従う。
// 既定（設定なし）は「出来高: 未提供」、true のときだけ「前営業日の出来高と 20 日平均比」。
// プロンプトの組み立て（T-10-1841）だけでは Program.cs が設定を読み違えても緑のまま（`decisionVolumeProvided: true` の変異が生存した）。
// ここでは LLM ゲートウェイ（名前付きクライアント "report-llm" の一次ハンドラ）だけを差し替え、実際に送られた本文を読む。
public class PolicyRevisionDecisionVolumeWiringTests
{
    [Theory]
    [InlineData(null, false)]
    [InlineData("false", false)]
    [InlineData("yes", false)] // 真偽として読めない値は無効
    [InlineData("true", true)]
    public async Task 方針の改訂の出来高の行は判断の出来高の設定に従う(string? enabled, bool provided)
    {
        var gateway = new PolicyRevisionWiringTests.RecordingGateway(
            """{"policySummary": "現状維持", "watchlistChanges": [], "rationale": "指示どおり"}""");
        await using var baseFactory = new ReportWorkerWebApplicationFactory();
        await using var factory = baseFactory.WithWebHostBuilder(b =>
        {
            b.UseSetting("LlmGateway:BaseUrl", "http://llm-gateway");
            if (enabled is not null)
                b.UseSetting("DecisionVolume:Enabled", enabled);
            b.ConfigureServices(services =>
                services.AddHttpClient("report-llm").ConfigurePrimaryHttpMessageHandler(() => gateway));
        });
        _ = factory.CreateClient();

        var reviser = factory.Services.GetRequiredService<IReportPolicyReviser>();
        await reviser.ReviseAsync(
            new PolicyRevisionContext(ReportKind.Daily, "daily-2026-09-30", "現状維持", null, "モメンタムが確認できれば買い"),
            TestContext.Current.CancellationToken);

        var sent = gateway.Bodies.Should().ContainSingle().Which;
        var prompt = string.Join("\n", Strings(JsonDocument.Parse(sent).RootElement));
        prompt.Should().Contain(PolicyRevisionPromptBuilder.DecisionMaterialsHeading, "方針の改訂のプロンプトが送られている");
        if (provided)
        {
            prompt.Should().Contain(PolicyRevisionPromptBuilder.VolumeProvidedMaterial);
            prompt.Should().NotContain(PolicyRevisionPromptBuilder.VolumeNotProvidedMaterial);
        }
        else
        {
            prompt.Should().Contain(PolicyRevisionPromptBuilder.VolumeNotProvidedMaterial, "既定は出来高を未提供と示す");
            prompt.Should().NotContain(PolicyRevisionPromptBuilder.VolumeProvidedMaterial);
        }
    }

    private static IEnumerable<string> Strings(JsonElement e) => e.ValueKind switch
    {
        JsonValueKind.String => [e.GetString()!],
        JsonValueKind.Object => e.EnumerateObject().SelectMany(p => Strings(p.Value)),
        JsonValueKind.Array => e.EnumerateArray().SelectMany(Strings),
        _ => [],
    };
}

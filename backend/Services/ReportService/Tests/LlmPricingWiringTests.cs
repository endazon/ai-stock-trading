using AiStockTrading.Shared.Contracts.Events;
using AiStockTrading.Shared.Contracts.Llm;
using AiStockTrading.TestSupport.Messaging;
using AwesomeAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using ReportService.Features.Reports;
using Wolverine.Tracking;
using Xunit;

namespace ReportService.Tests;

// FR-04, FR-06, NFR（費用）, #1295, IADR-0524: report-service の単価表は trade-decision と独立に構成される（IADR-0296）。
// プロンプト長の 2 段（claude-haiku-5-5）の構成キー（env 名と同じアンダースコア形）が Program.cs で読まれ、
// 計上（PublishingLlmUsageReporter）が要求ごとの入力トークン数で段を引き分けて計上額へ届くことを固定する。
// 配線が外れると第 2 段が黙って第 1 段（1/5 の単価）になり、長いプロンプトが過小計上になる。
public class LlmPricingWiringTests
{
    private static readonly Dictionary<string, string?> TieredHaiku = new()
    {
        ["LlmPricing:PerModel:claude_haiku_5_5:InputPer1kTokens"] = "0.0164",
        ["LlmPricing:PerModel:claude_haiku_5_5:OutputPer1kTokens"] = "0.0819",
        ["LlmPricing:PerModel:claude_haiku_5_5:LongContextThresholdTokens"] = "100000",
        ["LlmPricing:PerModel:claude_haiku_5_5:LongContextInputPer1kTokens"] = "0.0819",
        ["LlmPricing:PerModel:claude_haiku_5_5:LongContextOutputPer1kTokens"] = "0.409",
    };

    [Theory]
    [InlineData(100_000, 1_000, 1.7219)]   // 境界ちょうど＝第 1 段: 100 × 0.0164 + 1 × 0.0819
    [InlineData(100_001, 1_000, 8.5990819)] // 100,000 超＝第 2 段: 100.001 × 0.0819 + 1 × 0.409
    [InlineData(120_000, 1_000, 10.237)]   // 第 2 段: 120 × 0.0819 + 1 × 0.409
    public async Task プロンプト長の2段の単価が構成から計上額まで届く(int inputTokens, int outputTokens, double expected)
    {
        await using var factory = new ReportWorkerWebApplicationFactory();
        using var configured = factory.WithWebHostBuilder(b =>
        {
            foreach (var (key, value) in TieredHaiku)
                b.UseSetting(key, value);
        });

        var reporter = configured.Services.GetRequiredService<ILlmUsageReporter>();
        var session = await configured.Services.ExecuteAndWaitForTestAsync(() =>
            reporter.ReportAsync(new LlmUsage(LlmPurposes.ReportDaily, inputTokens, outputTokens, LlmAssignments.Haiku55)));

        session.Sent.MessagesOf<LlmCostIncurred>().Should().ContainSingle()
            .Which.Amount.Should().Be((decimal)expected);
    }

    // NFR-13, ADR-0064 決定9, #1305, IADR-0553: 表に無いモデル（旧世代・モデル名なし）は専用の設定値「未知モデルの計上単価」で計上する。
    // 構成キー（env 名 `LlmPricing__UnknownModel__*`）が report-service の Program.cs でも読まれ、未設定なら既定 $10 / $50 になる。
    [Theory]
    [InlineData("claude-opus-5", null, null, 18.009)]   // 既定: 1.637 + 2 × 8.186
    [InlineData(null, null, null, 18.009)]
    [InlineData("claude-opus-5", "2.5", "12.5", 27.5)]  // 設定値: 2.5 + 2 × 12.5
    public async Task 表に無いモデルは未知モデルの計上単価で計上される(string? model, string? input, string? output, double expected)
    {
        await using var factory = new ReportWorkerWebApplicationFactory();
        using var configured = factory.WithWebHostBuilder(b =>
        {
            foreach (var (key, value) in TieredHaiku)
                b.UseSetting(key, value);
            b.UseSetting("LlmPricing:UnknownModel:InputPer1kTokens", input);
            b.UseSetting("LlmPricing:UnknownModel:OutputPer1kTokens", output);
        });

        var reporter = configured.Services.GetRequiredService<ILlmUsageReporter>();
        var session = await configured.Services.ExecuteAndWaitForTestAsync(() =>
            reporter.ReportAsync(new LlmUsage(LlmPurposes.ReportMonthly, 1_000, 2_000, model)));

        session.Sent.MessagesOf<LlmCostIncurred>().Should().ContainSingle()
            .Which.Amount.Should().Be((decimal)expected);
    }
}

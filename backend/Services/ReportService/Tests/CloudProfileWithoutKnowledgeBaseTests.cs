using System.Net;
using System.Net.Http.Json;
using AiStockTrading.Shared.KnowledgeBase.Ports;
using AwesomeAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using ReportService.Features.Reports;
using Xunit;

namespace ReportService.Tests;

// FR-06, FR-08, ADR-0006, #1525, IADR-0608 決定1: 単一ノードのクラウドの構成（values-cloud）は KB の宛先を持たず、
// 報告書の KB の写し（ReportDraftKnowledge）も無効にする。宛先を空にしたまま起動し、日報の 1 回（作成→確定）が通り、
// 確定済みの方針が取引判断の読む経路へ出ることを固定する（KB への保存は no-op）。
public class CloudProfileWithoutKnowledgeBaseTests
{
    [Fact]
    public async Task KBの宛先が空でも起動し日報の作成と確定が通る()
    {
        await using var factory = new ReportWorkerWebApplicationFactory().WithWebHostBuilder(b =>
            b.ConfigureAppConfiguration((_, cfg) => cfg.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["KnowledgeBase:Documents:BaseUrl"] = "",
                ["KnowledgeBase:Search:BaseUrl"] = "",
                [ReportDraftKnowledgeOptions.EnabledKey] = "false",
            })));
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add(TestAuthHandler.RolesHeader, "trading-owner");

        (await client.GetAsync("/health/live")).StatusCode.Should().Be(HttpStatusCode.OK);
        factory.Services.GetRequiredService<IKnowledgeBaseWriter>().GetType().Name.Should().Be("NoOpKnowledgeBaseWriter");
        factory.Services.GetRequiredService<ReportDraftKnowledgeOptions>().Enabled.Should().BeFalse();

        var put = await client.PutAsJsonAsync("/reports/daily-2026-07-10", new
        {
            Kind = "Daily",
            PeriodStart = "2026-07-10",
            BasedOn = (string?)null,
            AssumptionsVersion = 1,
            PolicySummary = "翌営業日は押し目買い",
            ExpectedVersion = 0,
        });
        put.StatusCode.Should().Be(HttpStatusCode.OK);
        var confirm = await client.PostAsJsonAsync("/reports/daily-2026-07-10/confirm", new { ExpectedVersion = 1 });
        confirm.StatusCode.Should().Be(HttpStatusCode.OK);
    }
}

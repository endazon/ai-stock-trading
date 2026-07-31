using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace AiStockTrading.Report.Worker.Tests;

// T-7, FR-06/16, IADR-0123 決定1/2/4, #308: 散文 LLM のタイムアウト構成が Program.cs へ結線されていることを固定する。
//
// 名前付きクライアント "report-llm" の Timeout は「全種別の解決値の最大」＝要求単位の打ち切りが壊れても
// 無制限に待たない上限（多層防御）。この値を通して、構成が実際に読まれていることを外側から観測できる。
public class ReportNarrativeTimeoutWiringTests(ReportWorkerWebApplicationFactory factory)
    : IClassFixture<ReportWorkerWebApplicationFactory>
{
    private static TimeSpan ClientTimeout(IServiceProvider services) =>
        services.GetRequiredService<IHttpClientFactory>().CreateClient("report-llm").Timeout;

    // IADR-0123 決定4: 設定を 1 つも足さない環境（本番 values.yaml は空文字を注入）でも週報・月報は 120 秒。
    // 上限＝最大値なので 120 秒になる。従来はここが 30 秒で、週報が構造的に間に合わなかった。
    [Fact]
    public void 未設定なら上限は組込既定の最大_120秒()
    {
        ClientTimeout(factory.Services).Should().Be(TimeSpan.FromSeconds(120));
    }

    // IADR-0123 決定3: 既存の全種別設定は生きたまま（設定済みデプロイの非破壊）。
    [Fact]
    public void 全種別設定を入れると上限がその値になる()
    {
        using var configured = factory.WithWebHostBuilder(b => b.UseSetting("LlmGateway:TimeoutSeconds", "45"));

        ClientTimeout(configured.Services).Should().Be(TimeSpan.FromSeconds(45));
    }

    // 種別別設定（values-local が経路B で使う設定点）が読まれていること。
    [Fact]
    public void 種別別設定を入れると上限がその最大になる()
    {
        using var configured = factory.WithWebHostBuilder(b =>
        {
            b.UseSetting("LlmGateway:TimeoutSeconds", "45");
            b.UseSetting("LlmGateway:TimeoutSecondsByKind:Weekly", "300");
        });

        ClientTimeout(configured.Services).Should().Be(TimeSpan.FromSeconds(300));
    }
}

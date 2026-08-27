using AiStockTrading.InformationCollection.Application.Adapters;
using AiStockTrading.InformationCollection.Application.Ports;
using AiStockTrading.InformationCollection.Domain;
using AiStockTrading.InformationCollection.Infrastructure.Composable.Adapters;
using AwesomeAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Wolverine;
using Xunit;

namespace AiStockTrading.InformationCollection.Api.Tests;

// FR-01, IADR-0022/0064: Program の構成束縛（Collection:Source:*）が実際に情報源の選択へ効くことを検証する。
// 構成キーの綴り違いは「静かに全ソース無効（ゼロ件収集）」として現れるため、ホスト起動込みで確かめる。
public class InformationSourceSelectionTests
{
    [Fact]
    public void 構成未設定は外部接続しない安全既定()
    {
        using var factory = new Factory([]);
        _ = factory.CreateClient();

        factory.Services.GetRequiredService<ISourceFetcher>().Should().BeOfType<NoSourcesFetcher>();
    }

    [Fact]
    public void 単一ソースの構成が揃えば当該コネクタが選ばれる()
    {
        using var factory = new Factory(new Dictionary<string, string?>
        {
            ["Collection:Source:Provider"] = "fred",
            ["Collection:Source:Fred:ApiKey"] = "key",
            ["Collection:Source:Fred:SeriesIds:0"] = "DGS10",
        });
        _ = factory.CreateClient();

        SourceNames(factory).Should().Equal("fred");
    }

    [Fact]
    public void 案A_プラス_の複数ソース指定は合成される()
    {
        using var factory = new Factory(new Dictionary<string, string?>
        {
            ["Collection:Source:Provider"] = "sec-edgar,boj",
            ["Collection:Source:SecEdgar:UserAgent"] = "AiStockTrading/1.0 (owner@example.com)",
            ["Collection:Source:SecEdgar:Ciks:0"] = "320193",
            ["Collection:Source:Boj:Db"] = "CO",
            ["Collection:Source:Boj:SeriesCodes:0"] = "TK99F1000601GCQ01000",
        });
        _ = factory.CreateClient();

        SourceNames(factory).Should().Equal("sec-edgar", "boj");
    }

    // FR-01, ADR-0020 決定2: ニュース系 2 系統の構成キーが Program の束縛に効くこと。
    // **綴り違いは「静かにニュース系ゼロ」として現れ、必須条件を満たさないまま緑になる。**
    [Fact]
    public void ニュース系2系統の構成キーが選択に効く()
    {
        using var factory = new Factory(new Dictionary<string, string?>
        {
            ["Collection:Source:Provider"] = "finnhub-news,google-news",
            ["Collection:Source:Finnhub:ApiKey"] = "key",
            ["Collection:Source:Finnhub:Symbols:0"] = "AAPL",
            ["Collection:Source:GoogleNews:Queries:0"] = "AAPL 株価",
        });
        _ = factory.CreateClient();

        SourceNames(factory).Should().Equal("finnhub-news", "google-news");
    }

    // ADR-0005 決定5: 一時降格の構成キーがカタログへ効くこと。
    [Fact]
    public void 一時降格の構成キーがカタログへ効く()
    {
        using var factory = new Factory(new Dictionary<string, string?>
        {
            ["Collection:Source:DemotedToRecommended"] = "finnhub-news",
        });
        _ = factory.CreateClient();

        var catalog = factory.Services.GetRequiredService<InformationSourceCatalog>();
        catalog.Find("finnhub-news")!.Tier.Should().Be(SourceTier.Recommended);
    }

    [Fact]
    public void 必須構成を欠くソースだけが除外される()
    {
        using var factory = new Factory(new Dictionary<string, string?>
        {
            // EDINET はキー未設定のため除外され、FRED だけが残る（1 ソースのキー切れで全体を止めない）。
            ["Collection:Source:Provider"] = "edinet,fred",
            ["Collection:Source:Fred:ApiKey"] = "key",
            ["Collection:Source:Fred:SeriesIds:0"] = "DGS10",
        });
        _ = factory.CreateClient();

        SourceNames(factory).Should().Equal("fred");
    }

    private static IReadOnlyList<string> SourceNames(Factory factory) =>
        factory.Services.GetRequiredService<ISourceFetcher>().Should().BeOfType<SourceFetchRunner>().Which.SourceNames;

    private sealed class Factory(Dictionary<string, string?> settings) : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Testing");
            builder.ConfigureAppConfiguration((_, cfg) =>
            {
                cfg.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["RabbitMq:ConnectionString"] = "amqp://localhost",
                    ["Otlp:Endpoint"] = "http://localhost:4317",
                    // 巡回が起動直後に走らないよう十分長い間隔にする（選択の検証のみ・実接続しない）。
                    ["Collection:PollIntervalSeconds"] = "3600",
                });
                cfg.AddInMemoryCollection(settings);
            });

            builder.ConfigureServices(services =>
            {
                // ADR-0013, IADR-0129, #354: 実 RabbitMQ へ接続しない（Wolverine の外部トランスポートを無効化する）。
                services.DisableAllExternalWolverineTransports();
            });
        }
    }
}

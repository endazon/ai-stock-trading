using TradeDecisionService.Features.TradeDecision;
using TradeDecisionService.Infrastructure.ExternalServices;
using AwesomeAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Wolverine;
using Xunit;
// IADR-0128: consumer は Infrastructure へ移った。相対名（Composable.Steps.*）参照をテスト本文を触らずに解決する。
using Composable = TradeDecisionService.Infrastructure;

namespace TradeDecisionService.Tests;

// FR-02, FR-13, UC-06, SC-02, IADR-0095: MarketMonitor:BaseUrl の有無で IWatchlistProvider が構成ベース（未結線の後方互換）/
// 権威源への s2s 同期照会（Http）に切り替わることを検証する。選択は解決時に構成を読む（WebApplicationFactory の構成上書きに追随する）。
public class WatchlistProviderSelectionTests
{
    [Fact]
    public void BaseUrl未設定は構成ベース_後方互換()
    {
        using var factory = new Factory(monitorBaseUrl: null);
        _ = factory.CreateClient(); // ホスト起動

        using var scope = factory.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<IWatchlistProvider>().Should().BeOfType<ConfigurationWatchlistProvider>();
    }

    [Fact]
    public void BaseUrl設定時は権威源を同期照会する_Http実装()
    {
        using var factory = new Factory(monitorBaseUrl: "http://monitor");
        _ = factory.CreateClient();

        using var scope = factory.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<IWatchlistProvider>().Should().BeOfType<HttpWatchlistProvider>();
    }

    // T-10-1998, FR-02, #1134, IADR-0475: 🔴 直前に読めた一覧は**本番の組み立てで singleton**（供給口はスコープごとに作られる）。
    // 別のスコープで覚えた一覧を、次のスコープの供給口が権威源の不達時に返す（scoped だと次のサイクルで不明に戻る）。
    // 宛先は接続を拒否する予約ポート（127.0.0.1:9）＝照会は必ず失敗する。
    [Fact]
    public async Task T_10_1998_直前に読めた一覧はスコープを跨いで残り_次のサイクルの供給口が使う()
    {
        using var factory = new Factory(monitorBaseUrl: "http://127.0.0.1:9");
        _ = factory.CreateClient();

        WatchlistLastKnown first;
        using (var scope = factory.Services.CreateScope())
        {
            first = scope.ServiceProvider.GetRequiredService<WatchlistLastKnown>();
            first.Record([new WatchedSymbol("AAPL", AiStockTrading.Shared.Contracts.Trading.Market.UnitedStates)]);
        }

        using (var scope = factory.Services.CreateScope())
        {
            scope.ServiceProvider.GetRequiredService<WatchlistLastKnown>().Should().BeSameAs(first);
            var provider = scope.ServiceProvider.GetRequiredService<IWatchlistProvider>();
            provider.Should().BeOfType<HttpWatchlistProvider>();

            (await provider.GetWatchlistAsync()).Should().ContainSingle().Which.Symbol.Should().Be("AAPL");
        }
    }

    private sealed class Factory(string? monitorBaseUrl) : WebApplicationFactory<Program>
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
                if (monitorBaseUrl is not null)
                    settings["MarketMonitor:BaseUrl"] = monitorBaseUrl;
                cfg.AddInMemoryCollection(settings);
            });
            builder.ConfigureServices(services =>
            {
                // ADR-0013, IADR-0129, #354: 実 RabbitMQ を避けて Wolverine の外部トランスポートを無効化する
                // （ハンドラの発見は Program.cs 側の配線が担う）。
                services.DisableAllExternalWolverineTransports();
            });
        }
    }
}

using System.Text.RegularExpressions;
using AwesomeAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using NotificationService.Features.Notifications;
using NotificationService.Infrastructure.ExternalServices;
using Wolverine;
using Xunit;
using MonitorProto = AiStockTrading.Shared.Grpc.MarketMonitor.V1;
using ReportProto = AiStockTrading.Shared.Grpc.Report.V1;
using RiskProto = AiStockTrading.Shared.Grpc.RiskManagement.V1;

namespace NotificationService.Tests;

// T-10-1731, NFR, FR-14, MSP:ADR-0029, ADR-0047 決定 1・2, IADR-0284 決定 5（段 5）, IADR-0449 決定 4・5, #753:
// **本番の Program.cs の組み立て**で、`RiskManagement:Grpc` / `Reports:Grpc` / `MarketMonitor:Grpc` の有無がボットの読み取りの実装を切り替え、
// **既定は REST** であることを固定する。🔴 型を見るだけでなく、組み立てた実装で**実際に呼び**、偽の提供側（127.0.0.1 の h2c）が
// **ボットの owner トークン**（`Notifications:Discord:OwnerAuth` の client_credentials で偽の取得先から取ったもの）を受け取ることまで見る。
// あわせて、helm（values・values-local）と compose の既定の描画が通知の gRPC を宣言していない（＝配備前に切り替わらない）ことを固定する。
public class BotReadGrpcWiringTests
{
    private static readonly string[] GrpcKeys = ["RiskManagement:Grpc", "Reports:Grpc", "MarketMonitor:Grpc"];

    [Fact]
    public async Task T_10_1731_宣言すれば読み取りが_gRPC_実装になりボットの_owner_トークンで提供側を呼ぶ()
    {
        var behavior = new BotReadStubBehavior
        {
            RiskStatus = BotReadStubBehavior.Returns(new RiskProto.GetRiskStatusResponse()),
            Review = BotReadStubBehavior.Returns(new ReportProto.GetReportReviewResponse { Version = 4 }),
            Watchlist = BotReadStubBehavior.Returns(new MonitorProto.GetWatchlistResponse
            {
                Items = { new MonitorProto.WatchlistItem { Symbol = "7203", Market = MonitorProto.Market.Japan } },
            }),
        };
        await using var host = await BotReadGrpcStubHost.StartAsync(behavior);
        using var factory = new Factory(new()
        {
            ["RiskManagement:Grpc"] = host.Address,
            ["Reports:Grpc"] = host.Address,
            ["MarketMonitor:Grpc"] = host.Address,
            ["Notifications:Discord:OwnerAuth:TokenEndpoint"] = host.TokenEndpoint,
            ["Notifications:Discord:OwnerAuth:ClientId"] = "ai-stock-trading-owner",
            ["Notifications:Discord:OwnerAuth:ClientSecret"] = "test-only",
        });
        _ = factory.CreateClient();
        var sp = factory.Services;

        sp.GetRequiredService<IPauseController>().Should().BeOfType<GrpcPauseController>("宣言があれば BaseUrl より gRPC を優先する");
        sp.GetRequiredService<IStageGateController>().Should().BeOfType<GrpcStageGateController>();
        sp.GetRequiredService<IReportReviewController>().Should().BeOfType<GrpcReportReviewController>();
        sp.GetRequiredService<IPolicyRevisionController>().Should().BeOfType<GrpcPolicyRevisionController>();
        sp.GetRequiredService<IMarketMonitorWatchlistController>().Should().BeOfType<GrpcMarketMonitorWatchlistController>();

        (await sp.GetRequiredService<IReportReviewController>().GetReviewAsync("daily-2026-09-01")).Version.Should().Be(4);
        (await sp.GetRequiredService<IMarketMonitorWatchlistController>().GetWatchlistAsync()).Succeeded.Should().BeTrue();
        await sp.GetRequiredService<IPauseController>().GetStatusAsync();

        behavior.Received.Select(r => r.Rpc).Should().BeEquivalentTo(["GetReportReview", "GetWatchlist", "GetRiskStatus"]);
        behavior.Received.Should().OnlyContain(r => r.Authorization == $"Bearer {behavior.IssuedToken}",
            "メタデータにはボットの owner マップ機密クライアントのトークンを載せる（ADR-0047 決定 2）");
        behavior.TokenRequests.Should().ContainSingle("3 つの輸送が 1 つの取得器（トークンのキャッシュ）を共有する");
    }

    // 陰性対照 1: 宣言が無ければ従来どおり REST（輸送そのものが登録されない）。
    [Fact]
    public void T_10_1731_宣言が無ければ_REST_のまま()
    {
        using var factory = new Factory([]);
        _ = factory.CreateClient();
        var sp = factory.Services;

        sp.GetService<RiskManagementGrpcTransport>().Should().BeNull();
        sp.GetService<ReportsGrpcTransport>().Should().BeNull();
        sp.GetService<MarketMonitorGrpcTransport>().Should().BeNull();
        sp.GetService<DiscordOwnerGrpcCredentials>().Should().BeNull();
        sp.GetRequiredService<IPauseController>().Should().BeOfType<HttpPauseController>();
        sp.GetRequiredService<IStageGateController>().Should().BeOfType<HttpStageGateController>();
        sp.GetRequiredService<IReportReviewController>().Should().BeOfType<HttpReportReviewController>();
        sp.GetRequiredService<IPolicyRevisionController>().Should().BeOfType<HttpPolicyRevisionController>();
        sp.GetRequiredService<IMarketMonitorWatchlistController>().Should().BeOfType<HttpMarketMonitorWatchlistController>();
    }

    // 陰性対照 2: 報告書だけを宣言すれば、リスク管理と市場監視は REST のまま（輸送が混ざらない）。
    [Fact]
    public void T_10_1731_報告書だけを宣言すれば他は_REST_のまま()
    {
        using var factory = new Factory(new() { ["Reports:Grpc"] = "http://report-service:8081" });
        _ = factory.CreateClient();
        var sp = factory.Services;

        sp.GetRequiredService<IReportReviewController>().Should().BeOfType<GrpcReportReviewController>();
        sp.GetRequiredService<IPolicyRevisionController>().Should().BeOfType<GrpcPolicyRevisionController>();
        sp.GetRequiredService<IPauseController>().Should().BeOfType<HttpPauseController>();
        sp.GetRequiredService<IStageGateController>().Should().BeOfType<HttpStageGateController>();
        sp.GetRequiredService<IMarketMonitorWatchlistController>().Should().BeOfType<HttpMarketMonitorWatchlistController>();
    }

    // 陰性対照 3: 宣言してあるのに使えない宛先は起動時に落とす（黙って REST へ戻さない）。
    [Theory]
    [InlineData("RiskManagement:Grpc", "https://risk-management-service:8081")]
    [InlineData("Reports:Grpc", "report-service:8081")]
    [InlineData("MarketMonitor:Grpc", "/relative")]
    public void T_10_1731_使えない宛先は起動時に落とす(string key, string address)
    {
        using var factory = new Factory(new() { [key] = address });

        var act = () => factory.CreateClient();

        act.Should().Throw<InvalidOperationException>().WithMessage($"*{key}*");
    }

    // 🔴 配備の既定は REST（gRPC の面は既定の配備で開いていない。#1068 の配備の注意）。helm の既定・values-local・compose の
    // 通知の構成に gRPC の宛先が無いこと。宣言は利用者が提供側の gRPC ポートを開いた後に足す（values.yaml のコメントの手順）。
    [Theory]
    [InlineData("deploy/helm/ai-stock-trading/values.yaml", "notification", true)]
    [InlineData("deploy/helm/ai-stock-trading/values-local.yaml", "notification", true)]
    [InlineData("docker-compose.yml", "notification-service", false)]
    public void T_10_1731_既定の配備は通知の_gRPC_を宣言しない(string path, string service, bool extraEnvRequired)
    {
        var block = ServiceBlock(File.ReadAllText(Path.Combine([RepoRoot(), .. path.Split('/')])), service);

        block.Should().NotBeEmpty($"{path} に {service} がある（空どうしの不在は何も証明しない）");
        if (extraEnvRequired)
            block.Should().Contain("extraEnv:");
        foreach (var key in GrpcKeys)
        {
            var env = key.Replace(":", "__", StringComparison.Ordinal);
            // コメント行（# で始まる）の手順の記述は除き、設定として現れないこと。
            Regex.IsMatch(block, @"(?m)^(?!\s*#).*" + Regex.Escape(env))
                .Should().BeFalse($"{path} の {service} に {env} を置かない（既定は REST）");
        }
    }

    // `  <name>:` から次の同じ深さのキーまで（なければ空）。
    internal static string ServiceBlock(string text, string name)
    {
        var lines = text.Replace("\r\n", "\n").Split('\n');
        var start = Array.IndexOf(lines, $"  {name}:");
        if (start < 0) return string.Empty;
        var end = start + 1;
        var sibling = new Regex(@"^  [A-Za-z0-9_-]+:\s*$|^[A-Za-z]");
        while (end < lines.Length && !sibling.IsMatch(lines[end])) end++;
        return string.Join('\n', lines[start..end]);
    }

    internal static string RepoRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "backend", "backend.slnx"))) return dir.FullName;
        }
        throw new InvalidOperationException("リポジトリの根（backend/backend.slnx）が見つからない。");
    }

    internal sealed class Factory(Dictionary<string, string> settings, Action<IServiceCollection>? configure = null) : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Testing");
            foreach (var (k, v) in settings)
                builder.UseSetting(k, v);
            builder.ConfigureAppConfiguration((_, cfg) => cfg.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["RabbitMq:ConnectionString"] = "amqp://localhost",
                ["Otlp:Endpoint"] = "http://localhost:4317",
                ["RiskManagement:BaseUrl"] = "http://risk-rest-must-not-be-used",
                ["Reports:BaseUrl"] = "http://report-rest-must-not-be-used",
                ["MarketMonitor:BaseUrl"] = "http://monitor-rest-must-not-be-used",
            }));
            builder.ConfigureServices(services =>
            {
                services.DisableAllExternalWolverineTransports();
                configure?.Invoke(services);
            });
        }
    }
}

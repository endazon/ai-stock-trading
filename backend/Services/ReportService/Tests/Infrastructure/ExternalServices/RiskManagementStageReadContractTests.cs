extern alias RiskManagementWorker;

using System.Net;
using System.Text;
using System.Text.Json;
using AiStockTrading.Shared.Contracts.Trading;
using AiStockTrading.Shared.Kernel.Trading;
using ReportService.Domain;
using ReportService.Infrastructure.ExternalServices;
using AwesomeAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using RiskDomain = RiskManagementWorker::RiskManagementService.Domain;
using RiskFeatures = RiskManagementWorker::RiskManagementService.Features.RiskManagement;

namespace ReportService.Tests;

// 🔴 T-10-936〜937, FR-06, FR-15, FR-20, #957, IADR-0271, IADR-0408（2026-09-25 追記。T-10-882 の同型）: 報告書が読むリスク管理の
// OpenD 稼働率（GET /risk-controls/session-uptime）と運用段階（GET /risk-controls/stage-gate）に、**送り手の本物の型を web 既定
// （camelCase・列挙は数値）で直列化した応答**を読ませる。各アダプタの既存テストは手書きの JSON であり、送り手で項目名を変えても
// 緑のまま、実行時は既定値で読む（`Days`・`CurrentStage` → 未供給／`Stage1CumulativeCountedDays` → 0）。
// OpenD 稼働率の応答の外側 `SessionUptimeView` は送り手で internal のため、行は本物の型 `OpenDSessionUptimeDay` で作り、外側は
// 送り手と同じ項目名で組む（その名前は T-10-939 が送り手の本物の Program.cs で固定する）。送り手が web 既定のまま出していることは
// リスク管理側の T-10-805 が固定する。
public class RiskManagementStageReadContractTests
{
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);
    private static readonly DateOnly From = new(2026, 9, 21);
    private static readonly DateOnly To = new(2026, 9, 25);

    private static HttpClient Client(string body) =>
        new(new StubHandler(body)) { BaseAddress = new Uri("http://risk-management") };

    // 🔴 T-10-936: OpenD 稼働率（日ごとの稼働率と累計算入日数）。
    [Fact]
    public async Task OpenD_稼働率は送り手の本物の型を直列化した応答から読める()
    {
        IReadOnlyList<RiskDomain.OpenDSessionUptimeDay> days =
        [
            new(new DateOnly(2026, 9, 22), 1.0m),
            new(new DateOnly(2026, 9, 23), 0.75m),
        ];
        var body = JsonSerializer.Serialize(new { days, stage1CumulativeCountedDays = 42 }, Web);
        var source = new HttpOpenDUptimeSource(Client(body), NullLogger<HttpOpenDUptimeSource>.Instance);

        var read = await source.GetUptimeAsync(From, To);

        read.Should().NotBeNull("送り手の応答は供給済み");
        read!.Days.Should().Equal(
            new OpenDUptimeDay(new DateOnly(2026, 9, 22), 1.0m),
            new OpenDUptimeDay(new DateOnly(2026, 9, 23), 0.75m));
        read.Stage1CumulativeCountedDays.Should().Be(42);
    }

    // 🔴 T-10-937: 運用段階（段階ゲートの現況のうち現段階）。
    [Fact]
    public async Task 運用段階は送り手の本物の型を直列化した応答から読める()
    {
        var status = new RiskFeatures.StageGateStatus(
            TradingStage.Stage2MinimalLive,
            new RiskDomain.StageSettings(TradingStage.Stage2MinimalLive, BrokerProvider.MoomooReal, 0.30m),
            [
                new RiskDomain.StageTransition(
                    1, TradingStage.Stage1Simulate, TradingStage.Stage2MinimalLive, RiskDomain.StageTransitionKind.Promotion,
                    "owner", new DateTimeOffset(2026, 9, 1, 0, 0, 0, TimeSpan.Zero), "昇格"),
            ],
            new RiskDomain.PromotionAssessment(TradingStage.Stage3ScaledLive, false, []),
            new RiskDomain.WithdrawalAssessment(false, null, false, null),
            new RiskDomain.Stage1Progress(60, 120),
            RiskDomain.Stage1GateCriteria.Default,
            new RiskFeatures.ShortSellReleaseState(RiskDomain.ShortSellReleaseVerdictStatus.Missing, null, "fp", "strategy", false, null));
        var source = new HttpStageProgressSource(
            Client(JsonSerializer.Serialize(status, Web)), NullLogger<HttpStageProgressSource>.Instance);

        var stage = await source.GetCurrentStageAsync();

        stage.Should().Be(TradingStage.Stage2MinimalLive);
    }

    private sealed class StubHandler(string body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            });
    }
}

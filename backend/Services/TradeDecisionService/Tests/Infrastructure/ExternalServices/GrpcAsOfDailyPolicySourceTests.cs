extern alias ReportWorker;

using AiStockTrading.Shared.Contracts.Trading;
using AwesomeAssertions;
using Grpc.Core;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using ReportWorker::ReportService.Features.Reports;
using TradeDecisionService.Features.TradeDecision;
using TradeDecisionService.Features.TradeDecision.RecordStage0Decisions;
using TradeDecisionService.Infrastructure.ExternalServices;
using Xunit;
using ReportProto = AiStockTrading.Shared.Grpc.Report.V1;

namespace TradeDecisionService.Tests;

// T-10-2761, T-10-2762, NFR, FR-04, FR-07, FR-15, IADR-0528 決定 3・7, IADR-0557 決定 2・4, #1388 (#753):
// Stage 0 の記録の「当時の確定日報の方針」を gRPC（`DailyPolicyRead/GetConfirmedDailyPolicyAsOf`）で読む受け手と、その選び方。
// 🔴 REST（HttpAsOfDailyPolicySource）と同じ解釈・同じ撃ち直し: 「無かった」（policy の無い応答）は null、「取れなかった」（失敗・
// 欠けた応答）は撃ち直してから記録を止める、照会の時刻より後の確定は撃ち直さずに止める。実 Kestrel の h2c（127.0.0.1）で往復させる。
public class GrpcAsOfDailyPolicySourceTests
{
    private static readonly DateOnly AsOf = new(2026, 9, 15);
    private static readonly DateTimeOffset At = new(2026, 9, 15, 20, 0, 0, TimeSpan.Zero);

    private static ServiceProvider Compose(string address)
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Reports:Grpc"] = address })
            .Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddAiStockTradingReportsGrpc(config);
        return services.BuildServiceProvider();
    }

    private static GrpcAsOfDailyPolicySource Source(ServiceProvider sp) =>
        new(sp.GetRequiredService<ReportsGrpcTransport>(), TimeProvider.System,
            NullLogger<GrpcAsOfDailyPolicySource>.Instance, TimeSpan.Zero);

    // 送り手の本物の型（ConfirmedDailyPolicyAsOf）を送り手の写し（DailyPolicyWireMapping）で線上へ出した応答。
    private static ReportProto.GetConfirmedDailyPolicyAsOfResponse Sent(ConfirmedDailyPolicyAsOf policy) =>
        new() { Policy = DailyPolicyWireMapping.ToProto(policy), ConfirmedAt = DailyPolicyWireMapping.Instant(policy.ConfirmedAt) };

    private static async Task<(DailyPolicy? Policy, Stage4ReadStubBehavior Behavior)> ReadAsync(
        Func<int, CancellationToken, Task<ReportProto.GetConfirmedDailyPolicyAsOfResponse>> respond, DateTimeOffset? at = null)
    {
        var behavior = new Stage4ReadStubBehavior { PolicyAsOf = respond };
        await using var host = await Stage4ReadStubHost.StartAsync(behavior);
        await using var sp = Compose(host.Address);
        return (await Source(sp).GetAsync(at ?? At, TestContext.Current.CancellationToken), behavior);
    }

    [Fact]
    public async Task T_10_2761_送り手の型の当時の方針を_REST_と同じ値で読み_時刻は_REST_と同じ書式で渡す()
    {
        var sent = new ConfirmedDailyPolicyAsOf(AsOf.AddDays(-1), "前日に確定した方針", 3, At.AddHours(-20), "daily-2026-09-14", 3);

        var (policy, behavior) = await ReadAsync(
            Stage4ReadStubBehavior.Returns(Sent(sent)), new DateTimeOffset(2026, 9, 15, 16, 0, 0, TimeSpan.FromHours(-4)));

        (policy!.Date, policy.Summary).Should().Be((AsOf.AddDays(-1), "前日に確定した方針"));
        policy.Reference.Should().Be(new DailyPolicyReference("daily-2026-09-14", 3));
        behavior.LastPolicyAsOfAt.Should().Be("2026-09-15T20:00:00Z", "REST の at と同じ（UTC・秒まで）");
        behavior.PolicyAsOfCalls.Should().Be(1);
    }

    // 「無かった」は撃ち直さない（REST の 404 と同じ）。
    [Fact]
    public async Task T_10_2761_確定日報が無ければ方針は無い_撃ち直さない()
    {
        var (policy, behavior) = await ReadAsync(Stage4ReadStubBehavior.Returns(new ReportProto.GetConfirmedDailyPolicyAsOfResponse()));

        policy.Should().BeNull();
        behavior.PolicyAsOfCalls.Should().Be(1);
    }

    // 🔴 「取れなかった」: 照会の失敗・欠けた応答は撃ち直し（3 回）の後に記録を止める。null（無かった）へ倒さない。
    public static TheoryData<string> Unreadable => ["失敗", "確定の時刻なし", "確定の時刻が読めない", "日付なし", "日付が読めない", "要約なし"];

    [Theory]
    [MemberData(nameof(Unreadable))]
    public async Task T_10_2761_取れなければ撃ち直してから記録を止める(string how)
    {
        var good = Sent(new ConfirmedDailyPolicyAsOf(AsOf, "方針", 1, At.AddHours(-1), "daily-2026-09-15", 2));
        Func<int, CancellationToken, Task<ReportProto.GetConfirmedDailyPolicyAsOfResponse>> respond = how switch
        {
            "失敗" => Stage4ReadStubBehavior.Fails<ReportProto.GetConfirmedDailyPolicyAsOfResponse>(StatusCode.Unavailable),
            "確定の時刻なし" => Stage4ReadStubBehavior.Returns(new ReportProto.GetConfirmedDailyPolicyAsOfResponse { Policy = good.Policy }),
            "確定の時刻が読めない" => Stage4ReadStubBehavior.Returns(
                new ReportProto.GetConfirmedDailyPolicyAsOfResponse { Policy = good.Policy, ConfirmedAt = "2026/09/15 19:00" }),
            "日付なし" => Stage4ReadStubBehavior.Returns(With(good, p => p.ClearDate())),
            "日付が読めない" => Stage4ReadStubBehavior.Returns(With(good, p => p.Date = "2026-9-15")),
            "要約なし" => Stage4ReadStubBehavior.Returns(With(good, p => p.ClearSummary())),
            _ => throw new ArgumentOutOfRangeException(nameof(how)),
        };
        var behavior = new Stage4ReadStubBehavior { PolicyAsOf = respond };
        await using var host = await Stage4ReadStubHost.StartAsync(behavior);
        await using var sp = Compose(host.Address);

        var act = () => Source(sp).GetAsync(At, TestContext.Current.CancellationToken);

        await act.Should().ThrowAsync<AsOfInputUnavailableException>();
        behavior.PolicyAsOfCalls.Should().Be(3);
    }

    // 🔴 照会の時刻より後に確定した方針は送り手の契約違反。撃ち直さずにすぐ記録を止める（REST と同じ）。
    [Fact]
    public async Task T_10_2761_照会の時刻より後に確定した方針は撃ち直さずに記録を止める()
    {
        var behavior = new Stage4ReadStubBehavior
        {
            PolicyAsOf = Stage4ReadStubBehavior.Returns(
                Sent(new ConfirmedDailyPolicyAsOf(AsOf, "未来に確定", 1, At.AddSeconds(1), "daily-2026-09-15", 4))),
        };
        await using var host = await Stage4ReadStubHost.StartAsync(behavior);
        await using var sp = Compose(host.Address);

        var act = () => Source(sp).GetAsync(At, TestContext.Current.CancellationToken);

        (await act.Should().ThrowAsync<AsOfInputUnavailableException>()).Which.Message.Should().Contain("契約違反");
        behavior.PolicyAsOfCalls.Should().Be(1);
    }

    // ---- T-10-2762: 選び方（Stage0AsOfComposition） ----

    [Theory]
    [InlineData(null, "http://order-execution-service:8080", "http://report-service:8081", true)]
    [InlineData("http://report-service:8080", "http://order-execution-service:8080", null, true)]
    [InlineData(null, "http://order-execution-service:8080", null, false)]
    [InlineData(null, null, "http://report-service:8081", false)]
    public void T_10_2762_報告書の口は_gRPC_の宣言でも満たす(string? reportsBaseUrl, string? orderExecutionBaseUrl, string? reportsGrpc, bool expected)
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Reports:BaseUrl"] = reportsBaseUrl,
            ["OrderExecution:BaseUrl"] = orderExecutionBaseUrl,
            ["Reports:Grpc"] = reportsGrpc,
        }).Build();

        Stage0AsOfComposition.HasBaseEndpoints(config).Should().Be(expected);
    }

    [Fact]
    public void T_10_2762_宣言があれば当時の方針は_gRPC_で_REST_の宛先より優先し_無ければ_REST()
    {
        static IAsOfDailyPolicySource Pick(Dictionary<string, string?> values)
        {
            var config = new ConfigurationBuilder().AddInMemoryCollection(values).Build();
            var services = new ServiceCollection();
            services.AddLogging();
            services.AddHttpClient();
            services.AddAiStockTradingReportsGrpc(config);
            using var sp = services.BuildServiceProvider();
            return Stage0AsOfComposition.PolicySource(
                sp, config, sp.GetRequiredService<IHttpClientFactory>(), TimeProvider.System, NullLoggerFactory.Instance);
        }

        Pick(new() { ["Reports:BaseUrl"] = "http://report-rest-must-not-be-used", ["Reports:Grpc"] = "http://report-service:8081" })
            .Should().BeOfType<GrpcAsOfDailyPolicySource>();
        Pick(new() { ["Reports:BaseUrl"] = "http://report-service:8080" })
            .Should().BeOfType<HttpAsOfDailyPolicySource>();
    }

    private static ReportProto.GetConfirmedDailyPolicyAsOfResponse With(
        ReportProto.GetConfirmedDailyPolicyAsOfResponse response, Action<ReportProto.DailyPolicyRecord> change)
    {
        var copy = response.Clone();
        change(copy.Policy);
        return copy;
    }
}

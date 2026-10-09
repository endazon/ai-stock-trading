using System.Text.RegularExpressions;
using AwesomeAssertions;
using Microsoft.Extensions.DependencyInjection;
using NotificationService.Infrastructure.ExternalServices;
using Xunit;

namespace NotificationService.Tests;

// T-10-2507, FR-14, FR-07, #243, IADR-0522 の 2026-10-10 追記: `/policy` の外側の上限（REST の名前付き HttpClient
// `report-policy-revision` と gRPC の deadline の既定）はどちらも 120 秒で、内側（報告書サービスの方針の改訂の LLM 上限の既定
// 95 秒）＋建玉の照会（10 秒）より長い（外側＞内側）。外側が先に切れると、報告書サービスが案を保存しても利用者には
// 「結果が分かりません」になる。内側の値は報告書サービスの Program.cs の定数を読んで突き合わせる（サービス間は参照しない）。
public class PolicyRevisionTimeoutOrderingTests
{
    // 報告書サービスが LLM の後に引く建玉の照会の上限（`risk-ledger` の HttpClient.Timeout）。
    private static readonly TimeSpan OpenPositionsLookup = TimeSpan.FromSeconds(10);

    private static TimeSpan ReportServiceDefault()
    {
        var source = File.ReadAllText(Path.Combine(
            BotReadGrpcWiringTests.RepoRoot(), "backend", "Services", "ReportService", "Program.cs"));
        var match = Regex.Match(source, @"public const int DefaultPolicyRevisionTimeoutSeconds = (\d+);");
        match.Success.Should().BeTrue("報告書サービスの既定の定数が読める（読めないまま緑にしない）");
        return TimeSpan.FromSeconds(int.Parse(match.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture));
    }

    [Fact]
    public void T_10_2507_方針の改訂の外側の上限はRESTとgRPCとも120秒で報告書サービスの上限より長い()
    {
        var inner = ReportServiceDefault();
        inner.Should().Be(TimeSpan.FromSeconds(95));

        using var factory = new BotReadGrpcWiringTests.Factory(new() { ["Reports:Grpc"] = "http://report-service:8081" });
        _ = factory.CreateClient();
        var rest = factory.Services.GetRequiredService<IHttpClientFactory>().CreateClient("report-policy-revision").Timeout;
        var grpc = factory.Services.GetRequiredService<ReportsGrpcTransport>().PolicyRevisionCalls.Timeout;

        rest.Should().Be(TimeSpan.FromSeconds(120));
        grpc.Should().Be(rest, "gRPC の deadline の既定は REST の HttpClient.Timeout と同値");
        rest.Should().BeGreaterThan(inner + OpenPositionsLookup, "外側は内側（LLM の上限＋建玉の照会）より長い");
    }
}

using AwesomeAssertions;
using Microsoft.Extensions.Configuration;
using ReportService.Features.Reports;
using Xunit;

namespace ReportService.Tests;

// T-10-2506, FR-14, FR-07, #243, IADR-0522 の 2026-10-10 追記: 方針の改訂の LLM 上限（`Reports:PolicyRevision:TimeoutSeconds`）の既定は 95 秒。
// 出力上限 8192 の余裕を使えるよう 60 秒から上げた。🔴 基盤のゲートウェイが上流の LLM を待つ 100 秒（固定。MSP#1872）より短く、
// REST の輸送の HttpClient.Timeout（散文の上限の最大＝既定 120 秒）より短い（内側の上限がどちらにも先取りされない）。
// 外側（通知サービス）がこれより長いことは NotificationService.Tests の T-10-2507 が固定する。
public class PolicyRevisionTimeoutTests
{
    // 基盤（MSP）の LLM ゲートウェイが上流（Anthropic）を待つ HttpClient の既定（.NET の HttpClient.Timeout 既定＝100 秒・構成できない）。
    private static readonly TimeSpan GatewayUpstreamTimeout = TimeSpan.FromSeconds(100);

    private static IConfiguration Config(string? seconds) => new ConfigurationBuilder()
        .AddInMemoryCollection(new Dictionary<string, string?> { ["Reports:PolicyRevision:TimeoutSeconds"] = seconds })
        .Build();

    [Fact]
    public void T_10_2506_方針の改訂の上限の既定は95秒でゲートウェイの100秒と散文の輸送の上限より短い()
    {
        var timeout = Program.PolicyRevisionTimeout(Config(null));

        timeout.Should().Be(TimeSpan.FromSeconds(95));
        timeout.Should().BeLessThan(GatewayUpstreamTimeout, "ゲートウェイの側で先に切れると上限を上げた意味が無い");
        new ReportNarrativeTimeouts(null, null, null, null).Max.Should().BeGreaterThan(
            timeout, "REST の輸送の HttpClient.Timeout（散文の上限の最大）が方針の改訂を先に切らない");
        new ReportNarrativeTimeouts("", "30", "120", "120").Max.Should().BeGreaterThan(
            timeout, "values-local の種別別の上限（30/120/120）でも先に切らない");
    }

    [Theory]
    [InlineData("30", 30)]
    [InlineData("0", 95)]
    [InlineData("-5", 95)]
    [InlineData("abc", 95)]
    [InlineData("", 95)]
    public void T_10_2506_方針の改訂の上限は構成に従い不正値は既定へ倒す(string seconds, int expected)
    {
        Program.PolicyRevisionTimeout(Config(seconds)).Should().Be(TimeSpan.FromSeconds(expected));
    }
}

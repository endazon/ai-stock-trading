using AiStockTrading.Notification.Application.Ports;
using AiStockTrading.Notification.Application.State;
using AiStockTrading.Notification.Infrastructure.Composable.Steps;
using AiStockTrading.Shared.Contracts.Events;
using AiStockTrading.Shared.Contracts.Llm;
using AiStockTrading.TestSupport.Messaging;
using AwesomeAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Wolverine;
using Wolverine.Tracking;
using Xunit;

namespace AiStockTrading.Notification.Infrastructure.Tests;

// FR-04, FR-06, FR-09, UC-01, ADR-0017 決定2・決定4-(2), #335, IADR-0216/0217:
// LLM 割当の逸脱（フォールバック発火）と、割当モデル不可による取引判断の見送りの**通知経路**。
//
// 🔴 ここで守っているのは**重大度の設計**である。決定4-(2) は発火を「埋もれない経路で出す」と定める一方、
// 決定2 は見送りを「障害ではなく設計上の正常な結果」と定める。したがって
// **発火も見送りも Warning**であり、どちらかを Info に落とせば沈黙し、Critical に上げれば
// 運用が障害として扱って「善意のフォールバック追加」を招く——決定2 が最も避けたい結末である。
public class LlmGovernanceNotificationTests
{
    private static readonly DateTimeOffset OccurredAt = new(2026, 8, 3, 1, 30, 0, TimeSpan.Zero);

    private static async Task<(IHost Host, RecordingNotificationSender Sender)> BuildAsync()
    {
        var sender = new RecordingNotificationSender();
        var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Services.AddSingleton<INotificationSender>(sender);
                // 本番（Program.cs）と同じ発見範囲。
                opts.Discovery.IncludeAssembly(typeof(OrderExecutedNotificationHandler).Assembly);
                opts.StubAllExternalTransports();
            })
            .StartAsync();
        return (host, sender);
    }

    // 決定4-(2): 「恒常的に発火しているなら設定が誤っている」ため、割当の逸脱は利用者へ届く。
    [Fact]
    public async Task フォールバック発火は割当と実効モデルつきの警告として通知する()
    {
        var (host, sender) = await BuildAsync();
        using var _ = host;

        var session = await host.TrackActivityForTest().InvokeMessageAndWaitAsync(
            new LlmFallbackFired(LlmPurposes.ReportMonthly, LlmAssignments.Opus5, "claude-sonnet-5",
                nameof(LlmAssignmentOutcome.FallbackFired), OccurredAt));
        session.Executed.MessagesOf<LlmFallbackFired>().Should().NotBeEmpty();

        var sent = sender.Sent.Should().ContainSingle().Subject;
        sent.Title.Should().Contain(LlmPurposes.ReportMonthly);
        sent.Content.Should().Contain(LlmAssignments.Opus5);
        sent.Content.Should().Contain("claude-sonnet-5");
        // 設定の見直しへ誘導する文言まで含めて「埋もれない通知」である。
        sent.Content.Should().Contain("割当設定を確認");
        sent.Severity.Should().Be(NotificationSeverity.Warning, "Info では埋もれる（決定4-(2)）");

        await host.StopAsync();
    }

    // 🔴 決定2 の核心。**「モデルが使えないのに発注が出ない」はバグではない。**
    // 通知文がそう読めなければ、運用は障害として扱い、フォールバック禁止を破る是正を入れてしまう。
    [Fact]
    public async Task 取引判断の見送りは正常な結果と読める警告として通知する()
    {
        var (host, sender) = await BuildAsync();
        using var _ = host;

        var session = await host.TrackActivityForTest().InvokeMessageAndWaitAsync(
            new TradeDecisionSkipped(LlmPurposes.TradeDecision, nameof(LlmAssignmentOutcome.Unassigned),
                LlmAssignments.Sonnet5, "claude-haiku-4-5", OccurredAt));
        session.Executed.MessagesOf<TradeDecisionSkipped>().Should().NotBeEmpty();

        var sent = sender.Sent.Should().ContainSingle().Subject;
        sent.Title.Should().Contain("見送り");
        sent.Content.Should().Contain(LlmAssignments.Sonnet5);
        // 発注していないことと、それが設計上の正常な結果であることの両方が読めること。
        sent.Content.Should().Contain("発注も行いませんでした");
        sent.Content.Should().Contain("正常な結果");
        sent.Severity.Should().Be(NotificationSeverity.Warning,
            "Critical は障害扱いを招き、フォールバック禁止（決定2）を破る是正を誘発する");

        await host.StopAsync();
    }
}

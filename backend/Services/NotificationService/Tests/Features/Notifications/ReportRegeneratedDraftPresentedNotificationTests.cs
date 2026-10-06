using AiStockTrading.Shared.Contracts.Events;
using AwesomeAssertions;
using NotificationService.Features.Notifications;
using NotificationService.Infrastructure.Steps;
using Xunit;

namespace NotificationService.Tests;

// T-10-2280, FR-06, FR-09, 計画 ADR-0052 決定 5, #1182, IADR-0491 決定 5（2026-10-06 追記）: `/report regenerate` で作り直した版の
// 提示（ReportDraftPresented・版 N）は、同じ会話キーの版 1 の提示の後でも**抑止されずに届き**、本文に版を書き、重大度は初版と同じ規則
// （要約に未供給の印・利確の書式の印があれば Warning）で上がる。`/report show` は本文を返さないため、これが作り直した版の要約を見る唯一の経路。
public class ReportRegeneratedDraftPresentedNotificationTests
{
    private const string Key = "daily-2026-10-06";
    private static readonly DateTimeOffset T = new(2026, 10, 6, 14, 20, 40, TimeSpan.Zero);

    private static string Summary(string narrative, bool unsupplied) =>
        "日報 2026-10-06（承認待ち）\n実現損益（税引後・費用込み）: 0 USD ／ 費用: 0 USD ／ 取引: 0 件（決済 0・勝ち 0）"
        + (unsupplied ? $"\n{ReportSummaryMarkers.UnsuppliedWarningPrefix}（確定の前に本文を確認してください）: 期間開始時点の在庫" : string.Empty)
        + $"\n\n{narrative}";

    [Fact]
    public async Task 作り直した版の提示は版1の提示の後でも抑止されず版を書いて届く()
    {
        var sender = new RecordingNotificationSender();
        var handler = new ReportDraftPresentedNotificationHandler(sender);

        await handler.Handle(new ReportDraftPresented(Key, "Daily", "2026-10-06", Summary("初版の散文", unsupplied: true), 1, T), CancellationToken.None);
        await handler.Handle(new ReportDraftPresented(Key, "Daily", "2026-10-06", Summary("作り直した散文", unsupplied: true), 2, T.AddHours(1)), CancellationToken.None);

        var sent = sender.Sent.ToArray();
        sent.Should().HaveCount(2, "同じ会話キーでも版 2 の提示は版 1 と別の提示であり、抑止しない");
        sent[0].Content.Should().Contain($"（{Key}・版 1）").And.Contain("初版の散文");
        sent[1].Title.Should().Be(sent[0].Title, "初版と同じ提示の通知");
        sent[1].Content.Should().Contain($"（{Key}・版 2）").And.Contain("作り直した散文").And.NotContain("初版の散文");
        sent[1].Content.Should().Contain(ReportSummaryMarkers.UnsuppliedWarningPrefix);
        sent[1].Severity.Should().Be(NotificationSeverity.Warning, "未供給の印があれば初版と同じく Warning");
    }

    [Fact]
    public async Task 作り直した版に警告が無ければ初版と同じくInfoで届く()
    {
        var sender = new RecordingNotificationSender();
        var handler = new ReportDraftPresentedNotificationHandler(sender);

        await handler.Handle(new ReportDraftPresented(Key, "Daily", "2026-10-06", Summary("散文", unsupplied: false), 2, T), CancellationToken.None);

        sender.Sent.Should().ContainSingle().Which.Severity.Should().Be(NotificationSeverity.Info);
    }
}

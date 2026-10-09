using AiStockTrading.Shared.Contracts.Events;
using AwesomeAssertions;
using NotificationService.Domain;
using NotificationService.Features.Notifications;
using Xunit;

namespace NotificationService.Tests;

// T-06-080, FR-06, FR-07, FR-09, 計画 ADR-0059 決定 2, #1218, IADR-0519 決定 2: 週報の方針に書式どおりの「数値目標:」行が無い警告を、
// 確定の前に Discord で見せる（提示の通知は Warning へ上げ、/policy の承認待ちの案でも確認ボタンの前に警告の行を出す）。確定は止めない。
public class WeeklyGoalLineWarningNotificationTests
{
    private const string WarningLine = ReportSummaryMarkers.WeeklyGoalLineMissingPrefix + "（行がありません ／ 確定はできます）: 「数値目標: -200 〜 +500 USD」の形の行を書いてください。";

    [Fact]
    public void 書式どおりの数値目標の行が無い週報のドラフトの提示は_Warning_で通知する()
    {
        var e = new ReportDraftPresented(
            "weekly-2026-W41", "Weekly", "2026-W41",
            $"週報 2026-W41（承認待ち）\n実現損益（税引後・費用込み）: 0 USD\n{WarningLine}\n\n所感",
            1, DateTimeOffset.UtcNow);

        var msg = NotificationFormatter.From(e);

        msg.Severity.Should().Be(NotificationSeverity.Warning);
        msg.Content.Should().Contain(WarningLine);
    }

    [Fact]
    public void 承認待ちにできた週報の改訂案でも警告の行だけは確認ボタンの前に見せる()
    {
        var serviceMessage = $"方針の改訂案を保存し、承認待ちにしました（確定するまで取引には適用されません）。（本日の /policy: 1/10 回目）\n{WarningLine}";

        var messages = PolicyRevisionMessage.Build("weekly-2026-W41", 2, true, false, serviceMessage, "方針", [], null);

        messages[0].Should().Contain(WarningLine);
        messages[0].Should().NotContain("本日の /policy", "警告の行以外の案内文は承認待ちのとき従来どおり出さない");
    }
}

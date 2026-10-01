using AiStockTrading.Shared.Contracts.Events;
using AwesomeAssertions;
using NotificationService.Domain;
using NotificationService.Features.Notifications;
using Xunit;

namespace NotificationService.Tests;

// T-10-1888, FR-04, FR-07, FR-09, #1129, IADR-0470 決定 4: 日報の方針に書式どおりの「利確:」行が無い警告を、確定の前に Discord で見せる
// （提示の通知は Warning へ上げ、/policy の改訂案は承認待ちにできた案でも確認ボタンの前に警告の行を出す）。確定は止めない。
public class PolicyTakeProfitWarningNotificationTests
{
    private const string WarningLine = ReportSummaryMarkers.PolicyTakeProfitMissingPrefix + "（確定はできます）: 「利確: AAPL +5%」の形の行を足してください。";

    [Fact]
    public void 書式どおりの利確の行が無い方針のドラフトの提示は_Warning_で通知する()
    {
        var e = new ReportDraftPresented(
            "daily-2026-10-01", "Daily", "2026-10-01",
            $"日報 2026-10-01（承認待ち）\n実現損益（税引後・費用込み）: 0 USD\n{WarningLine}\n\n所感",
            1, DateTimeOffset.UtcNow);

        var msg = NotificationFormatter.From(e);

        msg.Severity.Should().Be(NotificationSeverity.Warning);
        msg.Content.Should().Contain(WarningLine);
    }

    [Fact]
    public void 承認待ちにできた改訂案でも警告の行だけは確認ボタンの前に見せる()
    {
        var serviceMessage = $"方針の改訂案を保存し、承認待ちにしました（確定するまで取引には適用されません）。（本日の /policy: 1/10 回目）\n{WarningLine}";

        var messages = PolicyRevisionMessage.Build("daily-2026-10-01", 2, true, false, serviceMessage, "方針", [], null);

        messages[0].Should().Contain(WarningLine);
        messages[0].Should().NotContain("本日の /policy", "警告の行以外の案内文は承認待ちのとき従来どおり出さない");
    }

    [Fact]
    public void 警告が無い承認待ちの改訂案の見出しは従来どおり()
    {
        var plain = PolicyRevisionMessage.Build("daily-2026-10-01", 2, true, false, "保存しました", "方針", [], null);

        plain[0].Should().NotContain(ReportSummaryMarkers.PolicyTakeProfitMissingPrefix);
        plain[0].Should().Be(PolicyRevisionMessage.Build("daily-2026-10-01", 2, true, false, string.Empty, "方針", [], null)[0]);
    }

    [Fact]
    public void 承認待ちにできなかった改訂案は案内文を丸ごと出し警告の行を重ねない()
    {
        var serviceMessage = $"方針の改訂案を保存しましたが、承認待ちにできませんでした。\n{WarningLine}";

        var header = PolicyRevisionMessage.Build("daily-2026-10-01", 2, false, false, serviceMessage, "方針", [], null)[0];

        header.Split(WarningLine).Length.Should().Be(2, "警告の行は 1 回だけ");
    }
}

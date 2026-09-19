using System.Net;
using System.Text;
using AwesomeAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NotificationService.Domain;
using NotificationService.Features.Notifications;
using NotificationService.Features.Notifications.ReviewReport;
using NotificationService.Infrastructure.ExternalServices;
using Xunit;

namespace NotificationService.Tests;

// FR-07, FR-14, UC-03〜05, ADR-0003, #840, IADR-0352 決定 5: 入力が未供給のまま生成された報告書であることを、
// `/report show` と版番号なしの `/report approve`（確認ボタンの前段）が**確定の前に**見せることを検証する。
// 窓口（ReportCommandHandler）から報告書サービスの応答（fake HTTP）までを本物の部品で繋いで見る。
//
// 🔴 本試験は本変更前から在る公開面だけで書いてある（警告の語も定数を引かず文字列で持つ）。
// 変更前のコードでも**コンパイルは通り、実行で落ちる**（＝修正が効いていることの証拠になる）。
public class ReportShowUnsuppliedWarningTests
{
    private const string Guild = "guild-1";
    private const string Channel = "channel-1";
    private const string OwnerUser = "discord-owner-1";
    private const string PeriodKey = "daily-2026-09-18";

    private static ReportCommandHandler Handler(string reviewJson)
    {
        var options = new DiscordBotOptions { GuildId = Guild, ChannelId = Channel };
        options.AllowedUserIds.Add(OwnerUser);
        options.UserMapping[OwnerUser] = "endazon";

        var controller = new HttpReportReviewController(
            new HttpClient(new FakeHandler(reviewJson)) { BaseAddress = new Uri("http://report-service") },
            NullLogger<HttpReportReviewController>.Instance);

        return new ReportCommandHandler(
            controller, new VersionedConfirmationGuard(), options, NullLogger<ReportCommandHandler>.Instance);
    }

    private static DiscordCommandContext Context(string raw) => new(Guild, Channel, OwnerUser, false, raw);

    // ---- 窓口まで通した提示 ----------------------------------------------------------------------

    [Theory]
    [InlineData("/report show daily-2026-09-18")]
    // 版番号なしの approve は確認ボタンの前段。ここに出なければ、利用者は欠落を知らないまま「確定する」を押せる。
    [InlineData("/report approve daily-2026-09-18")]
    public async Task 未供給だった入力は_確定の前の応答に現れる(string command)
    {
        var handler = Handler(
            """{"periodKey":"daily-2026-09-18","state":"PendingApproval","version":1,"unsuppliedInputs":["建玉","OpenD 稼働率","散文（LLM）"]}""");

        var result = await handler.HandleAsync(Context(command));

        result.WasExecuted.Should().BeTrue();
        // 版番号は従来どおり返る（確認ボタンは出る。確定を機械的に拒否はしない）。
        result.Version.Should().Be(1);
        result.Message.Should().StartWith($"報告書 {PeriodKey}: 版 1");
        result.Message.Should().Contain("⚠ 未供給の入力があります");
        result.Message.Should().Contain("建玉、OpenD 稼働率、散文（LLM）");
        result.Message.Should().Contain("確定の前に本文を確認してください");
    }

    [Theory]
    // 未供給なし。
    [InlineData("""{"periodKey":"daily-2026-09-18","state":"PendingApproval","version":1,"unsuppliedInputs":[]}""")]
    // 項目そのものが無い（本変更前の報告書サービス）・null。
    [InlineData("""{"periodKey":"daily-2026-09-18","state":"PendingApproval","version":1}""")]
    [InlineData("""{"periodKey":"daily-2026-09-18","state":"PendingApproval","version":1,"unsuppliedInputs":null}""")]
    // 空白だけの項目は無いのと同じ。
    [InlineData("""{"periodKey":"daily-2026-09-18","state":"PendingApproval","version":1,"unsuppliedInputs":["  ",null]}""")]
    public async Task 未供給が無ければ_警告は現れず従来どおりの文言になる(string reviewJson)
    {
        var result = await Handler(reviewJson).HandleAsync(Context($"/report show {PeriodKey}"));

        result.WasExecuted.Should().BeTrue();
        // 🔴 否定形: 欠けていない報告書を「欠けている」と見せない（本変更前とバイト等価）。
        result.Message.Should().Be($"報告書 {PeriodKey}: 版 1");
    }

    private sealed class FakeHandler(string body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            });
    }
}

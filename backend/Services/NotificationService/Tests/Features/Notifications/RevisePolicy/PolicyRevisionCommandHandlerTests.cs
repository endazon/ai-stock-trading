using AwesomeAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NotificationService.Domain;
using NotificationService.Features.Notifications;
using NotificationService.Features.Notifications.RevisePolicy;
using Xunit;

namespace NotificationService.Tests;

// FR-07, FR-14, UC-03〜05, ADR-0003, #1016, IADR-0431: `/policy`（方針の改訂）の閂（T-10-1322〜1328）。
// 多層認証 → 解析 → 指示の検証 → 報告書サービス。**ここでは確定しない**・**監視銘柄は変えない**・**失敗と不明を区別する**。
public class PolicyRevisionCommandHandlerTests
{
    private const string Guild = "guild-1";
    private const string Channel = "channel-1";
    private const string OwnerUser = "discord-owner-1";

    private static DiscordBotOptions Options()
    {
        var options = new DiscordBotOptions { GuildId = Guild, ChannelId = Channel, KillSwitchConfirmationPhrase = "STOP TRADING" };
        options.AllowedUserIds.Add(OwnerUser);
        options.UserMapping[OwnerUser] = "developer";
        return options;
    }

    private static DiscordCommandContext Context(string raw = "/policy", string user = OwnerUser, bool dm = false) =>
        new(Guild, Channel, user, dm, raw);

    private static PolicyRevisionProposalView Proposal(bool presented = true, string periodKey = "daily-2026-09-27", int version = 1) =>
        new(periodKey, version, Created: true, presented, AutoGenerationSkipped: true, "保存しました",
            "押し目買いを優先する",
            [new WatchlistChangeSuggestionView("add", "NVDA", "AI 需要")],
            "指示どおり");

    private sealed class FakeController(PolicyRevisionCommandOutcome outcome) : IPolicyRevisionController
    {
        public List<(string? PeriodKey, string Instruction, string OnBehalfOf)> Calls { get; } = [];

        public Task<PolicyRevisionCommandOutcome> ReviseAsync(
            string? periodKey, string instruction, string onBehalfOf, CancellationToken cancellationToken = default)
        {
            Calls.Add((periodKey, instruction, onBehalfOf));
            return Task.FromResult(outcome);
        }
    }

    private static (PolicyRevisionCommandHandler Handler, FakeController Controller) Create(PolicyRevisionCommandOutcome? outcome = null)
    {
        var controller = new FakeController(outcome ?? new PolicyRevisionCommandOutcome(true, false, "保存しました", Proposal()));
        return (new PolicyRevisionCommandHandler(controller, Options(), NullLogger<PolicyRevisionCommandHandler>.Instance), controller);
    }

    // T-10-1322: 案が承認待ちになれば、表示文と確認ボタンの会話キー・版を返す。操作者を代理として渡す。
    [Fact]
    public async Task 案が承認待ちなら確認ボタンの版を返し操作者を代理で渡す()
    {
        var (handler, controller) = Create();

        var result = await handler.HandleAsync(Context(), "  もっと積極的に  ");

        result.WasExecuted.Should().BeTrue();
        (result.PeriodKey, result.Version).Should().Be(("daily-2026-09-27", 1));
        result.Message.Should().Contain("押し目買いを優先する").And.Contain("追加 NVDA: AI 需要")
            .And.Contain(PolicyRevisionMessage.WatchlistNotice).And.Contain("確定するまで取引には適用されません");
        controller.Calls.Should().ContainSingle().Which.Should().Be(((string?)null, "もっと積極的に", "developer"));
    }

    // T-10-1323: 会話キーを指定すればそれを渡す（大小文字を保つ）。
    [Fact]
    public async Task 会話キーを指定すればそのまま渡す()
    {
        var (handler, controller) = Create();

        await handler.HandleAsync(Context("/policy weekly-2026-W39"), "指示");

        controller.Calls.Single().PeriodKey.Should().Be("weekly-2026-W39");
    }

    // T-10-1324: 承認待ちにできなかった案には確認ボタンを出さない（未提示の版は確定できない）。
    [Fact]
    public async Task 未提示の案には確認ボタンを出さない()
    {
        var (handler, _) = Create(new PolicyRevisionCommandOutcome(true, false, "保存", Proposal(presented: false)));

        var result = await handler.HandleAsync(Context(), "指示");

        result.WasExecuted.Should().BeTrue();
        result.Version.Should().BeNull();
        result.PeriodKey.Should().BeNull();
    }

    // T-10-1325: 多層認証を通らなければ報告書サービスを呼ばない（DM・許可外・別チャンネル）。
    [Theory]
    [InlineData("stranger", false, Channel)]
    [InlineData(OwnerUser, true, Channel)]
    [InlineData(OwnerUser, false, "other-channel")]
    public async Task 多層認証を通らなければ呼ばない(string user, bool dm, string channel)
    {
        var (handler, controller) = Create();

        var result = await handler.HandleAsync(new DiscordCommandContext(Guild, channel, user, dm, "/policy"), "指示");

        result.IsDenied.Should().BeTrue();
        controller.Calls.Should().BeEmpty();
    }

    // T-10-1326: `/policy` 以外・余分な引数・書式外の会話キー・空や長すぎる指示では呼ばない（LLM の費用を使わない）。
    [Theory]
    [InlineData("/report show daily-2026-09-27", "指示")]
    [InlineData("/policy a b", "指示")]
    [InlineData("/policy ../x", "指示")]
    [InlineData("/policy", "")]
    [InlineData("/policy", "   ")]
    [InlineData("/policy", null)]
    public async Task 解析と指示の検証を通らなければ呼ばない(string raw, string? instruction)
    {
        var (handler, controller) = Create();

        var result = await handler.HandleAsync(Context(raw), instruction);

        result.WasExecuted.Should().BeFalse();
        controller.Calls.Should().BeEmpty();
    }

    [Fact]
    public async Task 長すぎる指示では呼ばない()
    {
        var (handler, controller) = Create();

        var result = await handler.HandleAsync(Context(), new string('a', PolicyRevisionCommandHandler.MaxInstructionLength + 1));

        result.WasExecuted.Should().BeFalse();
        result.Message.Should().Contain("1000");
        controller.Calls.Should().BeEmpty();
    }

    // T-10-1327: 報告書サービスの失敗・不明はそのまま見せ、確認ボタンを出さない（失敗と不明を混ぜない）。
    [Theory]
    [InlineData(false, "AI の案を作れませんでした（AI の呼び出しに失敗しました）。方針は変わっていません。")]
    [InlineData(true, "方針の改訂の結果が分かりません（応答が届きませんでした）。案が保存された可能性があるため、/report show で確認してください。")]
    public async Task 失敗と不明はそのまま見せボタンを出さない(bool indeterminate, string message)
    {
        var (handler, _) = Create(new PolicyRevisionCommandOutcome(false, indeterminate, message));

        var result = await handler.HandleAsync(Context(), "指示");

        result.WasExecuted.Should().BeFalse();
        result.IsDenied.Should().BeFalse();
        result.Message.Should().Be(message);
        result.Version.Should().BeNull();
    }

    // T-10-1328: 表示文は Discord の上限に収まり、監視銘柄は表示のみであることを必ず添える。
    [Fact]
    public void 表示文は上限に収まり監視銘柄は表示のみと添える()
    {
        var text = PolicyRevisionMessage.Format(
            "daily-2026-09-27", 3, presented: true, created: false, autoGenerationSkipped: false,
            new string('方', 5000),
            [.. Enumerable.Range(0, 10).Select(i => ("add", $"SYM{i}", new string('理', 200)))],
            new string('説', 5000));

        text.Length.Should().BeLessThanOrEqualTo(PolicyRevisionMessage.MaxLength);
        text.Should().Contain(PolicyRevisionMessage.WatchlistNotice);

        PolicyRevisionMessage.Format("daily-2026-09-27", 1, true, false, false, "方針", [], null)
            .Should().Contain("【監視銘柄の入れ替え案】").And.Contain("- なし");
    }
}
